$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class K2 {
    [DllImport("user32.dll")] public static extern void keybd_event(byte b, byte s, uint f, UIntPtr e);
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
if ($settings -eq $null) { $out.AppendLine("no settings window"); $out.ToString() | Out-File C:\Users\bd799\es-expand2.txt -Encoding utf8; exit }

function DumpEsGroup([System.Windows.Automation.AutomationElement]$settings, [System.Text.StringBuilder]$out)
{
    $all = $settings.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $esGroup = $null
    foreach ($e in $all)
    {
        $n = ""
        try { $n = $e.Current.Name } catch {}
        if ($n -ne $null -and $n.StartsWith("Energy saver"))
        {
            $ct = ""
            try { $ct = $e.Current.ControlType.ProgrammaticName } catch {}
            if ($ct -like "*Group*") { $esGroup = $e; $out.AppendLine("group: '" + $n + "'"); break }
        }
    }
    if ($esGroup -eq $null) { $out.AppendLine("  ES group not found"); return }
    $els = $esGroup.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $out.AppendLine("  ES group elements: " + $els.Count)
    foreach ($e in $els)
    {
        $n = ""; $ct = ""
        try { $n = $e.Current.Name } catch {}
        if ($n -eq $null -or $n -eq "") { continue }
        try { $ct = $e.Current.ControlType.ProgrammaticName } catch {}
        $out.AppendLine("    [" + $ct + "] '" + $n + "'")
    }
}

DumpEsGroup $settings $out

$all = $settings.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
$more = $null
$moreRect = [System.Windows.Automation.Rect]::Empty
foreach ($e in $all)
{
    $n = ""
    try { $n = $e.Current.Name } catch {}
    if ($n -eq "Show more settings")
    {
        $p = $e.Current.BoundingRectangle
        $inEs = $false
        # take the LAST Show more settings button that sits inside/near the ES card area (y > battery percent area)
        $more = $e; $moreRect = $p
        $out.AppendLine("ShowMore found at " + $p.X + "," + $p.Y + " " + $p.Width + "x" + $p.Height)
    }
}
if ($more -eq $null) { $out.AppendLine("no Show more button"); $out.ToString() | Out-File C:\Users\bd799\es-expand2.txt -Encoding utf8; exit }

# attempt 1: SetFocus + SPACE
try
{
    $more.SetFocus()
    Start-Sleep -Milliseconds 400
    [K2]::keybd_event(0x20, 0, 0, [UIntPtr]::Zero)
    [K2]::keybd_event(0x20, 0, 2, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 1200
    $out.AppendLine("attempt: focus+SPACE done")
}
catch { $out.AppendLine("focus+space failed: " + $_.Exception.Message) }

DumpEsGroup $settings $out

# attempt 2: physical mouse click at button center
$cx = [int]($moreRect.X + $moreRect.Width / 2)
$cy = [int]($moreRect.Y + $moreRect.Height / 2)
$sw = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width
$sh = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height
$ax = [uint32]([Math]::Round($cx * 65535.0 / $sw))
$ay = [uint32]([Math]::Round($cy * 65535.0 / $sh))
$out.AppendLine("attempt 2: mouse click at " + $cx + "," + $cy)
[K2]::mouse_event(0x8001, $ax, $ay, 0, [UIntPtr]::Zero)   # MOVE|ABSOLUTE
[K2]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)       # LEFTDOWN
[K2]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)       # LEFTUP
Start-Sleep -Milliseconds 1500

DumpEsGroup $settings $out

# dump any Turn on/off controls on the whole page
$els = $settings.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
foreach ($e in $els)
{
    $n = ""
    try { $n = $e.Current.Name } catch {}
    if ($n -ne $null)
    {
        $l = $n.ToLower()
        if ($l.Contains("turn on now") -or $l.Contains("turn off now") -or $l.Contains("energy saver") -or $l.Contains("always use"))
        {
            $ct = ""; $pats = ""
            try { $ct = $e.Current.ControlType.ProgrammaticName } catch {}
            foreach ($pn in @("TogglePattern","InvokePattern"))
            {
                $t = [System.Windows.Automation.TogglePattern].Assembly.GetType("System.Windows.Automation." + $pn)
                if ($t -eq $null) { continue }
                $f = $t.GetField("Pattern")
                if ($f -eq $null) { continue }
                try { $null = $e.GetCurrentPattern($f.GetValue($null)); $pats += $pn + " " } catch {}
            }
            $out.AppendLine("  PAGE [" + $ct + " | " + $pats + "] '" + $n + "'")
        }
    }
}
$out.AppendLine("expand2 complete")
$out.ToString() | Out-File -FilePath C:\Users\bd799\es-expand2.txt -Encoding utf8
