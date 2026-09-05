$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class KC3 {
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, UIntPtr e);
}
"@
function Shot([string]$path)
{
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen(0, 0, 0, 0, $bmp.Size)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}

Start-Process "ms-settings:powersleep"
Start-Sleep -Seconds 3
Shot C:\Users\bd799\shot-before.png

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ClassNameProperty, "ApplicationFrameWindow")
$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
$settings = $null
foreach ($w in $wins) { $nm = ""; try { $nm = $w.Current.Name } catch {}; if ($nm -like "*Settings*") { $settings = $w; break } }

$all = $settings.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
$moreRect = [System.Windows.Automation.Rect]::Empty
$gRect = [System.Windows.Automation.Rect]::Empty
foreach ($e in $all)
{
    $n = ""
    try { $n = $e.Current.Name } catch {}
    if ($n -ne $null -and $n.StartsWith("Energy saver"))
    {
        $ct = ""
        try { $ct = $e.Current.ControlType.ProgrammaticName } catch {}
        if ($ct.Contains("Group")) { $gRect = $e.Current.BoundingRectangle }
    }
    if ($n -eq "Show more settings")
    {
        $r = $e.Current.BoundingRectangle
        $cy = $r.Y + $r.Height / 2
        if (-not $gRect.IsEmpty -and $cy -ge $gRect.Y - 5 -and $cy -le $gRect.Y + $gRect.Height + 5)
        {
            $moreRect = $r
        }
    }
}
$out = "groupRect=" + $gRect.X + "," + $gRect.Y + " " + $gRect.Width + "x" + $gRect.Height + " | moreRect=" + $moreRect.X + "," + $moreRect.Y
[System.IO.File]::WriteAllText(C:\Users\bd799\es-shot-info.txt, $out)

if (-not $moreRect.IsEmpty)
{
    $cx = [int]($moreRect.X + $moreRect.Width / 2)
    $cy = [int]($moreRect.Y + $moreRect.Height / 2)
    $sw = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width
    $sh = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height
    $ax = [uint32]([Math]::Round($cx * 65535.0 / $sw))
    $ay = [uint32]([Math]::Round($cy * 65535.0 / $sh))
    [KC3]::mouse_event(0x8001, $ax, $ay, 0, [UIntPtr]::Zero)
    [KC3]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    [KC3]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 1500
}
Shot C:\Users\bd799\shot-after.png
