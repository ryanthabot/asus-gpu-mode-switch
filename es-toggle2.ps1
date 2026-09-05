$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class KC2 {
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, UIntPtr e);
    [DllImport("user32.dll")] public static extern IntPtr PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
}
"@
$out = New-Object System.Text.StringBuilder
Start-Process "ms-settings:powersleep"
Start-Sleep -Seconds 3

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ClassNameProperty, "ApplicationFrameWindow")
$settings = $null
for ($i = 0; $i -lt 12 -and $settings -eq $null; $i++)
{
    $wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
    foreach ($w in $wins) { $nm = ""; try { $nm = $w.Current.Name } catch {}; if ($nm -like "*Settings*") { $settings = $w; break } }
    if ($settings -eq $null) { Start-Sleep -Milliseconds 400 }
}
if ($settings -eq $null) { $out.AppendLine("FAIL: no Settings window"); $out.ToString() | Out-File C:\Users\bd799\es-toggle-result2.txt -Encoding utf8; exit }
$out.AppendLine("settings window found")

# helper: find the ES group + its internal toggles
function GetEsState([System.Windows.Automation.AutomationElement]$settings)
{
    $all = $settings.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $g = $null; $gr = [System.Windows.Automation.Rect]::Empty
    $toggle = $null
    foreach ($e in $all)
    {
        $n = ""
        try { $n = $e.Current.Name } catch {}
        if ($n -ne $null -and $n.StartsWith("Energy saver"))
        {
            $ct = ""
            try { $ct = $e.Current.ControlType.ProgrammaticName } catch {}
            if ($ct.Contains("Group")) { $g = $e; $gr = $e.Current.BoundingRectangle }
        }
        if ($n -eq "Always use energy saver")
        {
            try { $null = $e.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern); $toggle = $e } catch {}
        }
    }
    return @($g, $gr, $toggle)
}

$res = GetEsState $settings
$esGroup = $res[0]; $gRect = $res[1]; $toggle = $res[2]
$out.AppendLine("ES group: " + ($esGroup -ne $null) + " | toggle present: " + ($toggle -ne $null))

if ($toggle -eq $null)
{
    # card is collapsed - click its own Show more settings button
    $all2 = $settings.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($e in $all2)
    {
        $n = ""
        try { $n = $e.Current.Name } catch {}
        if ($n -ne "Show more settings") { continue }
        $r = [System.Windows.Automation.Rect]::Empty
        try { $r = $e.Current.BoundingRectangle } catch {}
        if ($r.IsEmpty) { continue }
        $cy = $r.Y + $r.Height / 2
        if ($cy -ge $gRect.Y - 5 -and $cy -le $gRect.Y + $gRect.Height + 5)
        {
            $cx = [int]($r.X + $r.Width / 2)
            $cyc = [int]($r.Y + $r.Height / 2)
            $sw = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width
            $sh = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height
            $ax = [uint32]([Math]::Round($cx * 65535.0 / $sw)); $ay = [uint32]([Math]::Round($cyc * 65535.0 / $sh))
            [KC2]::mouse_event(0x8001, $ax, $ay, 0, [UIntPtr]::Zero)
            [KC2]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
            [KC2]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
            $out.AppendLine("clicked expand at " + $cx + "," + $cyc)
            Start-Sleep -Milliseconds 1200
            break
        }
    }
    $res = GetEsState $settings
    $toggle = $res[2]
    $out.AppendLine("toggle present after expand: " + ($toggle -ne $null))
}

if ($toggle -eq $null) { $out.AppendLine("FAIL: toggle not found"); $out.ToString() | Out-File C:\Users\bd799\es-toggle-result2.txt -Encoding utf8; exit }

$tp = $toggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
$before = $tp.Current.ToggleState.ToString()
$out.AppendLine("current toggle state: " + $before)
if ($before -ne "On")
{
    $tp.Toggle()
    Start-Sleep -Milliseconds 800
    $after = $tp.Current.ToggleState.ToString()
    $out.AppendLine("after toggle: " + $after)
}
else { $out.AppendLine("already On") }

[KC2]::PostMessage($settings.Current.NativeWindowHandle, 0x10, [IntPtr]::Zero, [IntPtr]::Zero)
$out.AppendLine("TEST DONE")
$out.ToString() | Out-File -FilePath C:\Users\bd799\es-toggle-result2.txt -Encoding utf8
