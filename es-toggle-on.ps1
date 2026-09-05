$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class KC {
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
if ($settings -eq $null) { $out.AppendLine("FAIL: no Settings window"); $out.ToString() | Out-File C:\Users\bd799\es-toggle-result.txt -Encoding utf8; exit }
$out.AppendLine("settings window found")

Start-Sleep -Milliseconds 800
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
        if ($ct.Contains("Group")) { $esGroup = $e; $gRect = $e.Current.BoundingRectangle; break }
    }
}
if ($esGroup -eq $null) { $out.AppendLine("FAIL: no ES group"); $out.ToString() | Out-File C:\Users\bd799\es-toggle-result.txt -Encoding utf8; exit }
$out.AppendLine("ES group found")

$more = $null; $moreRect = [System.Windows.Automation.Rect]::Empty
foreach ($e in $all)
{
    $n = ""
    try { $n = $e.Current.Name } catch {}
    if ($n -ne "Show more settings") { continue }
    $r = [System.Windows.Automation.Rect]::Empty
    try { $r = $e.Current.BoundingRectangle } catch {}
    if ($r.IsEmpty) { continue }
    $cy = $r.Y + $r.Height / 2
    if ($cy -ge $gRect.Y - 5 -and $cy -le $gRect.Y + $gRect.Height + 5) { $more = $e; $moreRect = $r; break }
}
if ($more -ne $null)
{
    $cx = [int]($moreRect.X + $moreRect.Width / 2)
    $cy = [int]($moreRect.Y + $moreRect.Height / 2)
    $sw = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width
    $sh = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height
    $ax = [uint32]([Math]::Round($cx * 65535.0 / $sw)); $ay = [uint32]([Math]::Round($cy * 65535.0 / $sh))
    [KC]::mouse_event(0x8001, $ax, $ay, 0, [UIntPtr]::Zero)
    [KC]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    [KC]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    $out.AppendLine("card expanded via click at " + $cx + "," + $cy)
    Start-Sleep -Milliseconds 1200
}

$toggle = $null
for ($i = 0; $i -lt 8 -and $toggle -eq $null; $i++)
{
    $els = $settings.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($e in $els)
    {
        $n = ""
        try { $n = $e.Current.Name } catch {}
        if ($n -eq "Always use energy saver")
        {
            try { $null = $e.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern); $toggle = $e; break } catch {}
        }
    }
    if ($toggle -eq $null) { Start-Sleep -Milliseconds 350 }
}
if ($toggle -eq $null) { $out.AppendLine("FAIL: toggle not found"); $out.ToString() | Out-File C:\Users\bd799\es-toggle-result.txt -Encoding utf8; exit }

$before = ""
try
{
    $tp = $toggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $before = $tp.Current.ToggleState.ToString()
    if ($before -ne "On") { $tp.Toggle(); Start-Sleep -Milliseconds 700 }
    $after = $tp.Current.ToggleState.ToString()
    $out.AppendLine("RESULT: toggle " + $before + " -> " + $after)
}
catch { $out.AppendLine("FAIL: toggle error " + $_.Exception.Message) }

[KC]::PostMessage($settings.Current.NativeWindowHandle, 0x10, [IntPtr]::Zero, [IntPtr]::Zero)
$out.AppendLine("settings closed")
$out.AppendLine("TEST DONE")
$out.ToString() | Out-File -FilePath C:\Users\bd799\es-toggle-result.txt -Encoding utf8
