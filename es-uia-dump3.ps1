$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$out = New-Object System.Text.StringBuilder
$root = [System.Windows.Automation.AutomationElement]::RootElement

$winsBefore = $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
$before = @{}
foreach ($w in $winsBefore)
{
    $rid = ""
    try { $rid = $w.Current.RuntimeId[1].ToString() + "-" + $w.Current.RuntimeId[2].ToString() } catch {}
    $before[$rid] = $true
}

$trayCond = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ClassNameProperty, "Shell_TrayWnd")
$tray = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $trayCond)
$btns = $tray.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
foreach ($b in $btns)
{
    $n = ""
    try { $n = $b.Current.Name } catch { continue }
    $l = ""
    if ($n -ne $null) { $l = $n.ToLower() }
    if ($l.Contains("network") -or $l.Contains("battery"))
    {
        try
        {
            $inv = $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
            $inv.Invoke()
            $out.AppendLine("invoked tray button: " + $n)
            break
        }
        catch { $out.AppendLine("invoke failed: " + $_.Exception.Message) }
    }
}

Start-Sleep -Milliseconds 2500

$winsAfter = $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
$out.AppendLine("top windows after: " + $winsAfter.Count)
foreach ($w in $winsAfter)
{
    $cls = ""; $nm = ""; $rid = ""
    try { $cls = $w.Current.ClassName } catch {}
    try { $nm = $w.Current.Name } catch {}
    try { $rid = $w.Current.RuntimeId[1].ToString() + "-" + $w.Current.RuntimeId[2].ToString() } catch {}
    $isNew = -not $before.ContainsKey($rid)
    $out.AppendLine("WIN new=" + $isNew + " class='" + $cls + "' name='" + $nm + "'")
    if ($isNew)
    {
        $els = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        $out.AppendLine("  NEW WINDOW element count: " + $els.Count)
        foreach ($e in $els)
        {
            $n = ""; $c = ""; $ct = ""; $tg = "False"
            try { $n = $e.Current.Name } catch {}
            try { $c = $e.Current.ClassName } catch {}
            try { $ct = $e.Current.ControlType.ProgrammaticName } catch {}
            try { $null = $e.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern); $tg = "True" } catch {}
            $out.AppendLine("    [T=" + $tg + " " + $ct + "] '" + $n + "' (" + $c + ")")
        }
    }
}
$out.AppendLine("dump3 complete")
$out.ToString() | Out-File -FilePath C:\Users\bd799\es-uia-dump3.txt -Encoding utf8
