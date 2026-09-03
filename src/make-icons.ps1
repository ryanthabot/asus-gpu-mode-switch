# make-icons.ps1
# Generates src\gotime.ico and src\ecomode.ico (16/32/48 BMP entries + 256 PNG
# entry) plus 256px PNG previews in icons-preview\, using only GDI+.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root    = Split-Path -Parent $MyInvocation.MyCommand.Path                      # src
$projDir = Split-Path -Parent $root
$preview = Join-Path $projDir 'icons-preview'
New-Item -ItemType Directory -Force -Path $preview | Out-Null

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

# ---------------------------------------------------------------- Eco: leaf
function New-EcoBitmap([int]$s)
{
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode    = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode  = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $f = $s / 256.0

    # green gradient rounded square
    $bgPath = RoundRectPath 0 0 $s $s (56 * $f)
    $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, 0)),
        (New-Object System.Drawing.Point(0, $s)),
        [System.Drawing.Color]::FromArgb(255, 102, 187, 106),
        [System.Drawing.Color]::FromArgb(255, 27, 94, 32))
    $g.FillPath($bgBrush, $bgPath)

    # leaf: pointed oval from bottom-left base to top-right tip, white
    $leaf = New-Object System.Drawing.Drawing2D.GraphicsPath
    $bx = 62 * $f; $by = 194 * $f     # base
    $tx = 194 * $f; $ty = 62 * $f     # tip
    $leaf.StartFigure()
    $leaf.AddBezier($bx, $by, 62 * $f, 112 * $f, 144 * $f, $ty, $tx, $ty)
    $leaf.AddBezier($tx, $ty, 144 * $f, $by, $tx, 144 * $f, $bx, $by)
    $leaf.CloseFigure()
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 240, 250, 238))
    $g.FillPath($white, $leaf)

    # vein cut into the leaf (skip on tiny sizes)
    if ($s -ge 32)
    {
        $vein = New-Object System.Drawing.Pen (
            [System.Drawing.Color]::FromArgb(255, 67, 160, 71), (9 * $f))
        $vein.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $vein.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
        $g.DrawLine($vein, 80 * $f, 176 * $f, 172 * $f, 84 * $f)
        $vein.Dispose()
    }

    # small stem below the base
    $stem = New-Object System.Drawing.Pen (
        [System.Drawing.Color]::FromArgb(255, 240, 250, 238), (13 * $f))
    $stem.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $stem.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($stem, 56 * $f, 200 * $f, 34 * $f, 222 * $f)
    $stem.Dispose()

    $bgBrush.Dispose(); $bgPath.Dispose(); $leaf.Dispose(); $white.Dispose()
    $g.Dispose()
    return $bmp
}

# ------------------------------------------------- Go Time icon
# Rendered separately (browser + nvidia-eye.svg) - see icons-preview\gotime.html
# and src\pack-ico.ps1. Kept here as documentation of the removed hand-drawn
# attempt: superseded by the official glyph.

# ------------------------------------------------------------ ICO container
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
    $h.AddRange([System.BitConverter]::GetBytes([UInt32]40))            # biSize
    $h.AddRange([System.BitConverter]::GetBytes([UInt32]$s))            # biWidth
    $h.AddRange([System.BitConverter]::GetBytes([UInt32]($s * 2)))      # biHeight (XOR+AND)
    $h.AddRange([System.BitConverter]::GetBytes([UInt16]1))             # biPlanes
    $h.AddRange([System.BitConverter]::GetBytes([UInt16]32))            # biBitCount
    $h.AddRange([System.BitConverter]::GetBytes([UInt32]0))             # biCompression
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

# ------------------------------------------------------------------- build
# The "Go Time" icon (official NVIDIA eye on a dark tile) is rendered from
# nvidia-eye.svg in a browser and packed with pack-ico.ps1 - see
# icons-preview\gotime.html. This script builds the "Eco Mode" leaf icon.
$sizes = @(16, 32, 48, 256)

$ecoBmps = $sizes | ForEach-Object { New-EcoBitmap $_ }

Save-Ico $ecoBmps (Join-Path $root 'ecomode.ico')

$ecoBmps[3].Save((Join-Path $preview 'ecomode-256.png'), [System.Drawing.Imaging.ImageFormat]::Png)

Write-Host "Icons written:"
Get-ChildItem (Join-Path $root '*.ico') | Format-Table Name, Length
