# pack-ico.ps1
# Packs a 256x256 source PNG into a multi-size Windows .ico
# (48/32/16 BMP entries downscaled with GDI+ + the original 256 as a PNG entry).
param(
    [Parameter(Mandatory = $true)][string]$SourcePng,
    [Parameter(Mandatory = $true)][string]$OutIco,
    [float]$ClipRadiusPx = 0,      # if set, source is clipped to a rounded rect (corners -> transparent)
    [string]$ClippedPngOut = ""    # optional: also save the clipped 256px image here
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function RoundRectPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r)
{
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Encode-BmpEntry([System.Drawing.Bitmap]$bmp)
{
    $s = $bmp.Width
    $rect = New-Object System.Drawing.Rectangle(0, 0, $s, $s)
    $data = $bmp.LockBits($rect,
        [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $rowBytes = $s * 4
    $xor = New-Object byte[] ($rowBytes * $s)
    for ($y = 0; $y -lt $s; $y++)
    {
        $src = New-Object System.IntPtr (($data.Scan0.ToInt64()) + (($s - 1 - $y) * $data.Stride))
        [System.Runtime.InteropServices.Marshal]::Copy($src, $xor, ($y * $rowBytes), $rowBytes)
    }
    $bmp.UnlockBits($data)

    $maskRow = [int][Math]::Ceiling($s / 8.0)
    $maskRow = [int][Math]::Ceiling($maskRow / 4.0) * 4
    $and = New-Object byte[] ($maskRow * $s)   # zeros = use alpha channel

    $h = New-Object System.Collections.Generic.List[byte]
    $h.AddRange([System.BitConverter]::GetBytes([UInt32]40))
    $h.AddRange([System.BitConverter]::GetBytes([UInt32]$s))
    $h.AddRange([System.BitConverter]::GetBytes([UInt32]($s * 2)))
    $h.AddRange([System.BitConverter]::GetBytes([UInt16]1))
    $h.AddRange([System.BitConverter]::GetBytes([UInt16]32))
    $h.AddRange([System.BitConverter]::GetBytes([UInt32]0))
    $h.AddRange([System.BitConverter]::GetBytes([UInt32]($xor.Length + $and.Length)))
    $h.AddRange([System.BitConverter]::GetBytes([UInt32]0))
    $h.AddRange([System.BitConverter]::GetBytes([UInt32]0))
    $h.AddRange([System.BitConverter]::GetBytes([UInt32]0))
    $h.AddRange([System.BitConverter]::GetBytes([UInt32]0))

    $out = New-Object byte[] (40 + $xor.Length + $and.Length)
    $h.CopyTo($out, 0)
    [Array]::Copy($xor, 0, $out, 40, $xor.Length)
    [Array]::Copy($and, 0, $out, 40 + $xor.Length, $and.Length)
    return ,$out
}

function Save-Ico([System.Drawing.Bitmap[]]$bmps, [string]$outPath)
{
    $imgs = @()
    foreach ($bmp in $bmps)
    {
        $s = $bmp.Width
        if ($s -ge 256)
        {
            $ms = New-Object System.IO.MemoryStream
            $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
            $imgs += @{ size = $s; data = $ms.ToArray() }
            $ms.Dispose()
        }
        else
        {
            $imgs += @{ size = $s; data = (Encode-BmpEntry $bmp) }
        }
    }

    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter($ms)
    $w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$imgs.Count)
    $offset = 6 + 16 * $imgs.Count
    foreach ($img in $imgs)
    {
        $b = 0; if ($img.size -lt 256) { $b = $img.size }
        $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([UInt16]1); $w.Write([UInt16]32)
        $w.Write([UInt32]$img.data.Length); $w.Write([UInt32]$offset)
        $offset += $img.data.Length
    }
    foreach ($img in $imgs) { $w.Write([byte[]]$img.data) }
    $w.Flush()
    [System.IO.File]::WriteAllBytes($outPath, $ms.ToArray())
    $w.Dispose(); $ms.Dispose()
}

$src = New-Object System.Drawing.Bitmap $SourcePng

if ($ClipRadiusPx -gt 0)
{
    # Redraw the square source onto a transparent canvas, clipped to a rounded
    # rect: corners become truly transparent (fixes white/corner artifacts
    # from browser screenshots) and matches the rounded-tile look.
    $clipped = New-Object System.Drawing.Bitmap $src.Width, $src.Height
    $g2 = [System.Drawing.Graphics]::FromImage($clipped)
    $g2.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g2.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $path = RoundRectPath 0 0 $src.Width $src.Height $ClipRadiusPx
    $g2.SetClip($path)
    $g2.DrawImage($src, 0, 0, $src.Width, $src.Height)
    $g2.Dispose()
    if ($ClippedPngOut -ne "")
    {
        $clipped.Save($ClippedPngOut, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    $src.Dispose()
    $src = $clipped
}

$all = @()
foreach ($s in @(16, 32, 48))
{
    $b = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode    = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode  = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.DrawImage($src, (New-Object System.Drawing.Rectangle(0, 0, $s, $s)))
    $g.Dispose()
    $all += $b
}
$all += $src

Save-Ico $all $OutIco
Write-Host "Wrote $OutIco"
