$ErrorActionPreference = 'Stop'
Start-Transcript -Path 'C:\Users\bd799\shot_log.txt' -Force | Out-Null
trap { "FATAL: $($_.Exception.Message) at $($_.InvocationInfo.ScriptLineNumber)" | Out-File C:\Users\bd799\shot_err.txt; Stop-Transcript | Out-Null; exit 1 }
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class U32 {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint data, UIntPtr extra);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@

$exe = "D:\OneDrive\Desktop New\Big's GPU Switch & Game Optimizer.exe"
$appName = "Big's GPU Switch & Game Optimizer"
$p = Get-Process | Where-Object { $_.Name -eq $appName } | Select-Object -First 1
if (-not $p) { Start-Process -FilePath $exe -WorkingDirectory 'D:\OneDrive\Desktop New' | Out-Null; Start-Sleep -Seconds 5 }
$p = Get-Process | Where-Object { $_.Name -eq $appName } | Select-Object -First 1
if (-not $p) { throw 'app not running' }
$deadline = (Get-Date).AddSeconds(20)
while ($p.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500; $p.Refresh() }
[U32]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Seconds 2

$r = New-Object U32+RECT
[U32]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
$scale = ($r.R - $r.L) / 980.0
$ox = $r.L; $oy = $r.T
"window rect: L=$($r.L) T=$($r.T) R=$($r.R) B=$($r.B) scale=$scale"

function Click([int]$lx, [int]$ly) {
  $x = [int]($ox + $lx * $scale); $y = [int]($oy + $ly * $scale)
  [U32]::SetCursorPos($x, $y) | Out-Null
  Start-Sleep -Milliseconds 120
  [U32]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
  [U32]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 150
}
function Wheel([int]$notches) {
  $val = -120 * $notches
  $data = if ($val -lt 0) { [uint32](4294967296 + $val) } else { [uint32]$val }
  [U32]::mouse_event(0x0800, 0, 0, $data, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 250
}
function Shot([string]$name) {
  $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
  $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
  $g.Dispose()
  $bmp.Save("C:\Users\bd799\$name", [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  "captured $name"
}

# nav rail: Home y=76, Opt 134, Mon 192, Hist 250, Theme 308 (in side panel at 6,6)
Click 49 111; Start-Sleep -Milliseconds 700; Shot 'bgs_home.png'
Click 49 169; Start-Sleep -Milliseconds 700; Shot 'bgs_optimize.png'
Wheel 6; Start-Sleep -Milliseconds 400; Shot 'bgs_optimize2.png'
Wheel -8; Start-Sleep -Milliseconds 300
Click 49 227; Start-Sleep -Seconds 4; Shot 'bgs_monitor.png'
Click 49 285; Start-Sleep -Milliseconds 700; Shot 'bgs_history.png'
# session history button: centered 420x64 at section y=282 -> client (533, 320)
Click 533 320; Start-Sleep -Milliseconds 1500; Shot 'bgs_sessionhist.png'
# close popup via Escape, then theme page
[System.Windows.Forms.SendKeys]::SendWait('{ESC}')
Start-Sleep -Milliseconds 500
Click 49 343; Start-Sleep -Milliseconds 700; Shot 'bgs_theme.png'
"DONE ALL"
Stop-Transcript | Out-Null
