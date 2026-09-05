$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Start-Process "ms-settings:powersleep"
Start-Sleep -Seconds 3

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ClassNameProperty, "ApplicationFrameWindow")
$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)

$settings = $null
foreach ($w in $wins)
{
    $nm = ""
    try { $nm = $w.Current.Name } catch {}
    if ($nm -like "*Settings*") { $settings = $w; break }
}
$out = New-Object System.Text.StringBuilder
if ($settings -eq $null) { $out.AppendLine("no Settings window"); $out.ToString() | Out-File C:\Users\bd799\es-page-probe.txt -Encoding utf8; exit }

function FindByName([System.Windows.Automation.AutomationElement]$parent, [string]$name)
{
    $c = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

# navigate: Power & battery
$nav = FindByName $settings "Power & battery"
$out.AppendLine("nav 'Power & battery': " + ($nav -ne $null))
if ($nav -ne $null)
{
    $inv = $null
    try { $inv = $nav.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern) } catch {}
    if ($inv -ne $null) { $inv.Invoke(); Start-Sleep -Seconds 2 }
    else
    {
        try { $sel = $nav.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern); $sel.Select(); Start-Sleep -Seconds 2 } catch {}
    }
}

# navigate: Energy saver entry
$es = FindByName $settings "Energy saver"
$out.AppendLine("nav 'Energy saver': " + ($es -ne $null))
if ($es -ne $null)
{
    $inv = $null
    try { $inv = $es.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern) } catch {}
    if ($inv -eq $null) { try { $sel = $es.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern); $sel.Select() } catch {} }
    else { $inv.Invoke() }
    Start-Sleep -Seconds 2
}

# dump the page: every element with a name, plus patterns
$els = $settings.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
$out.AppendLine("elements on page: " + $els.Count)
foreach ($e in $els)
{
    $n = ""; $ct = ""; $pats = ""
    try { $n = $e.Current.Name } catch {}
    if ($n -eq $null -or $n -eq "") { continue }
    try { $ct = $e.Current.ControlType.ProgrammaticName } catch {}
    foreach ($pn in @("TogglePattern","InvokePattern","SelectionItemPattern","RangeValuePattern"))
    {
        $t = [System.Windows.Automation.TogglePattern].Assembly.GetType("System.Windows.Automation." + $pn)
        if ($t -eq $null) { continue }
        $f = $t.GetField("Pattern")
        if ($f -eq $null) { continue }
        try { $null = $e.GetCurrentPattern($f.GetValue($null)); $pats += $pn + " " } catch {}
    }
    $out.AppendLine("  [" + $ct + " | " + $pats + "] '" + $n + "'")
}
$out.AppendLine("page probe complete")
$out.ToString() | Out-File -FilePath C:\Users\bd799\es-page-probe.txt -Encoding utf8
