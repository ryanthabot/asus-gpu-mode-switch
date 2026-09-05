$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

# open the Energy saver settings page in the interactive session
Start-Process "ms-settings:powersaver"
Start-Sleep -Seconds 4

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ClassNameProperty, "ApplicationFrameWindow")
$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
$out = New-Object System.Text.StringBuilder
$out.AppendLine("ApplicationFrameWindows: " + $wins.Count)

foreach ($w in $wins)
{
    $nm = ""
    try { $nm = $w.Current.Name } catch {}
    if ($nm -notlike "*Settings*") { continue }

    $out.AppendLine("SETTINGS WINDOW found: '" + $nm + "'")
    $els = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $out.AppendLine("total elements: " + $els.Count)
    foreach ($e in $els)
    {
        $n = ""; $ct = ""; $patterns = ""
        try { $n = $e.Current.Name } catch {}
        if ($n -eq $null) { $n = "" }
        try { $ct = $e.Current.ControlType.ProgrammaticName } catch {}
        foreach ($pname in @("TogglePattern","InvokePattern","SelectionItemPattern"))
        {
            $pt = [System.Windows.Automation.TogglePattern].Assembly.GetType("System.Windows.Automation." + $pname)
            if ($pt -eq $null) { continue }
            $prop = $pt.GetField("Pattern")
            if ($prop -eq $null) { continue }
            try
            {
                $null = $e.GetCurrentPattern($prop.GetValue($null))
                $patterns += $pname + " "
            }
            catch {}
        }
        $l = $n.ToLower()
        if ($patterns -ne "" -or $l.Contains("energy") -or $l.Contains("saver") -or $l.Contains("turn"))
        {
            $out.AppendLine("  [" + $ct + " | " + $patterns + "] name='" + $n + "'")
        }
    }
}
$out.AppendLine("settings probe complete")
$out.ToString() | Out-File -FilePath C:\Users\bd799\es-settings-probe.txt -Encoding utf8
