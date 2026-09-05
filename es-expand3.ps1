$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class K3 {
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, UIntPtr e);
}
"@
$out = New-Object System.Text.StringBuilder

Start-Process "ms-settings:powersleep"
Start-Sleep -Seconds 3

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ClassNameProperty, "ApplicationFrameWindow")
$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
$settings = $null
foreach ($w in $wins) { $nm = ""; try { $nm = $w.Current.Name } catch {}; if ($nm -like "*Settings*") { $settings = $w; break } }
if ($settings -eq $null) { $out.AppendLine("no settings window"); $out.ToString() | Out-File C:\Users\bd799\es-expand3.txt -Encoding utf8; exit }

# locate the Energy saver GROUP + its rect
$all = $settings.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
$esGroup = $null; $gRect = [System.Windows.Automation.Rect]::Empty
foreach ($e in $all)
{
    $n = ""
    try { $n = $e.Current.Name } catch {}
    if ($n -ne $null -and $n.StartsWith("Energy saver"))
    {
        $ct = ""
        try { $ct = $e.Current.ControlType.ProgrammaticName } catch {}
        if ($ct -like "*Group*")
        {
            $esGroup = $e
            $gRect = $e.Current.BoundingRectangle
            $out.AppendLine("ES group rect: " + $gRect.X + "," + $gRect.Y + " " + $gRect.Width + "x" + $gRect.Height)
            break
        }
    }
}
if ($esGroup -eq $null) { $out.AppendLine("ES group not found"); $out.ToString() | Out-File C:\Users\bd799\es-expand3.txt -Encoding utf8; exit }

# find the Show more settings button whose center Y is inside the group rect
$target = $null; $tRect = [System.Windows.Automation.Rect]::Empty
foreach ($e in $all)
{
    $n = ""
    try { $n = $e.Current.Name } catch {}
    if ($n -ne "Show more settings") { continue }
    $r = $e.Current.BoundingRectangle
    if ($r.IsEmpty) { continue }
    $cy = $r.Y + $r.Height / 2
    if ($cy -ge $gRect.Y - 5 -and $cy -le $gRect.Y + $gRect.Height + 5)
    {
        $target = $e; $tRect = $r
        $out.AppendLine("matched button at " + $r.X + "," + $r.Y + " " + $r.Width + "x" + $r.Height)
        break
    }
}
if ($target -eq $null) { $out.AppendLine("no matching button in group rect"); $out.ToString() | Out-File C:\Users\bd799\es-expand3.txt -Encoding utf8; exit }

# click it with a real mouse event
$cx = [int]($tRect.X + $tRect.Width / 2)
$cy = [int]($tRect.Y + $tRect.Height / 2)
$sw = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width
$sh = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height
$ax = [uint32]([Math]::Round($cx * 65535.0 / $sw))
$ay = [uint32]([Math]::Round($cy * 65535.0 / $sh))
$out.AppendLine("clicking " + $cx + "," + $cy)
[K3]::mouse_event(0x8001, $ax, $ay, 0, [UIntPtr]::Zero)
[K3]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
[K3]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 1500

# dump the ES group content now
$els = $esGroup.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
$out.AppendLine("ES group elements after click: " + $els.Count)
foreach ($e in $els)
{
    $n = ""; $ct = ""; $pats = ""
    try { $n = $e.Current.Name } catch {}
    if ($n -eq $null -or $n -eq "") { continue }
    try { $ct = $e.Current.ControlType.ProgrammaticName } catch {}
    foreach ($pn in @("TogglePattern","InvokePattern","RangeValuePattern"))
    {
        $t = [System.Windows.Automation.TogglePattern].Assembly.GetType("System.Windows.Automation." + $pn)
        if ($t -eq $null) { continue }
        $f = $t.GetField("Pattern")
        if ($f -eq $null) { continue }
        try { $null = $e.GetCurrentPattern($f.GetValue($null)); $pats += $pn + " " } catch {}
    }
    $out.AppendLine("  [" + $ct + " | " + $pats + "] '" + $n + "'")
}
$out.AppendLine("expand3 complete")
$out.ToString() | Out-File -FilePath C:\Users\bd799\es-expand3.txt -Encoding utf8
