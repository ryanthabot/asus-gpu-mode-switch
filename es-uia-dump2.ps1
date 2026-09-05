$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$out = New-Object System.Text.StringBuilder
$root = [System.Windows.Automation.AutomationElement]::RootElement

$trayCond = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ClassNameProperty, "Shell_TrayWnd")
$tray = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $trayCond)
$out.AppendLine("tray found: " + ($tray -ne $null))

$opened = $false
if ($tray -ne $null)
{
    $btns = $tray.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $out.AppendLine("tray elements: " + $btns.Count)
    foreach ($b in $btns)
    {
        $n = ""
        try { $n = $b.Current.Name } catch { continue }
        $l = ""
        if ($n -ne $null) { $l = $n.ToLower() }
        if ($l.Contains("network") -or $l.Contains("battery") -or $l.Contains("volume") -or $l.Contains("speaker"))
        {
            $out.AppendLine("  candidate tray button: '" + $n + "'")
            try
            {
                $inv = $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
                $inv.Invoke()
                $opened = $true
                $out.AppendLine("  invoked OK")
                break
            }
            catch { $out.AppendLine("  invoke failed: " + $_.Exception.Message) }
        }
    }
}

Start-Sleep -Milliseconds 1800
$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
$out.AppendLine("top-level windows now: " + $wins.Count)
foreach ($w in $wins)
{
    $cls = ""; $nm = ""
    try { $cls = $w.Current.ClassName } catch {}
    try { $nm = $w.Current.Name } catch {}
    $l = $cls.ToLower()
    if ($l.Contains("shell") -or $l.Contains("xaml") -or $l.Contains("quick"))
    {
        $out.AppendLine("PANEL WIN class='" + $cls + "' name='" + $nm + "'")
        $els = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        $out.AppendLine("  element count: " + $els.Count)
        foreach ($e in $els)
        {
            $n = ""; $c = ""; $ct = ""
            try { $n = $e.Current.Name } catch {}
            try { $c = $e.Current.ClassName } catch {}
            try { $ct = $e.Current.ControlType.ProgrammaticName } catch {}
            $hasToggle = $false
            try { $null = $e.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern); $hasToggle = $true } catch {}
            $lname = ""
            if ($n -ne $null) { $lname = $n.ToLower() }
            if ($hasToggle -or $lname.Contains("energy") -or $lname.Contains("saver"))
            {
                $out.AppendLine("    [TOGGLE=" + $hasToggle + " " + $ct + "] name='" + $n + "' class='" + $c + "'")
            }
        }
    }
}
$out.AppendLine("dump2 complete")
$out.ToString() | Out-File -FilePath C:\Users\bd799\es-uia-dump2.txt -Encoding utf8
