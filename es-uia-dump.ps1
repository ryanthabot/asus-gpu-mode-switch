$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class K {
    [DllImport("user32.dll")] public static extern void keybd_event(byte b, byte s, uint f, UIntPtr e);
}
"@
[K]::keybd_event(0x5B, 0, 0, [UIntPtr]::Zero)
[K]::keybd_event(0x41, 0, 0, [UIntPtr]::Zero)
[K]::keybd_event(0x41, 0, 2, [UIntPtr]::Zero)
[K]::keybd_event(0x5B, 0, 2, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 1500

$out = New-Object System.Text.StringBuilder
$root = [System.Windows.Automation.AutomationElement]::RootElement
$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
$out.AppendLine("top-level windows: " + $wins.Count)
foreach ($w in $wins) {
    $cls = ""; $nm = ""; $pid2 = 0
    try { $cls = $w.Current.ClassName } catch {}
    try { $nm = $w.Current.Name } catch {}
    try { $pid2 = $w.Current.ProcessId } catch {}
    $isShell = ($cls.ToLower().Contains("shell") -or $cls.ToLower().Contains("xaml") -or $nm.ToLower().Contains("quick"))
    $out.AppendLine("WIN class='" + $cls + "' name='" + $nm + "' pid=" + $pid2 + " shellish=" + $isShell)
    if (-not $isShell) { continue }
    $els = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $out.AppendLine("  element count: " + $els.Count)
    foreach ($e in $els) {
        $n = ""; $c = ""; $ct = ""
        try { $n = $e.Current.Name } catch {}
        try { $c = $e.Current.ClassName } catch {}
        try { $ct = $e.Current.ControlType.ProgrammaticName } catch {}
        $hasToggle = $false
        try { $null = $e.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern); $hasToggle = $true } catch {}
        if ($hasToggle -or ($n -ne $null -and $n.ToLower().Contains("energy"))) {
            $out.AppendLine("    [TOGGLE=" + $hasToggle + " " + $ct + "] name='" + $n + "' class='" + $c + "'")
        }
    }
}
$out.AppendLine("dump complete")
$out.ToString() | Out-File -FilePath C:\Users\bd799\es-uia-dump.txt -Encoding utf8
