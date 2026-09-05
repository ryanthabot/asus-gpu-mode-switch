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
if ($settings -eq $null) { $out.AppendLine("no Settings window"); $out.ToString() | Out-File C:\Users\bd799\es-expand-probe.txt -Encoding utf8; exit }

function FindByName([System.Windows.Automation.AutomationElement]$parent, [string]$name)
{
    $c = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

# find the Energy saver GROUP and its own Show more settings button
$groups = $settings.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
$esGroup = $null
foreach ($e in $groups)
{
    $n = ""
    try { $n = $e.Current.Name } catch {}
    if ($n -ne $null -and $n.StartsWith("Energy saver"))
    {
        $ct = ""
        try { $ct = $e.Current.ControlType.ProgrammaticName } catch {}
        if ($ct -like "*Group*") { $esGroup = $e; $out.AppendLine("ES group found: '" + $n + "'"); break }
    }
}
if ($esGroup -eq $null) { $out.AppendLine("ES group NOT found"); $out.ToString() | Out-File C:\Users\bd799\es-expand-probe.txt -Encoding utf8; exit }

$more = FindByName $esGroup "Show more settings"
$out.AppendLine("ES 'Show more settings' button: " + ($more -ne $null))
if ($more -ne $null)
{
    try { ($more.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() } catch { $out.AppendLine("invoke failed: " + $_.Exception.Message) }
    Start-Sleep -Seconds 2
}

# dump everything inside the expanded ES group
$els = $esGroup.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
$out.AppendLine("elements in ES group: " + $els.Count)
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
$out.AppendLine("expand probe complete")
$out.ToString() | Out-File -FilePath C:\Users\bd799\es-expand-probe.txt -Encoding utf8
