$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class U32 {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@
$exe = "D:\OneDrive\Desktop New\Big's GPU Switch & Game Optimizer.exe"
$appName = "Big's GPU Switch & Game Optimizer"
Start-Process -FilePath $exe -WorkingDirectory 'D:\OneDrive\Desktop New' | Out-Null
Start-Sleep -Seconds 5
$p = Get-Process | Where-Object { $_.Name -eq $appName } | Select-Object -First 1
if (-not $p) { throw 'app not running' }
$deadline = (Get-Date).AddSeconds(20)
while ($p.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500; $p.Refresh() }
[U32]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Seconds 2
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
$g.Dispose()
$bmp.Save('C:\Users\bd799\bgs_v123_home.png', [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
'captured v123 home'
# leave the app running for the user? No - close it cleanly to leave a clean desktop
$p.CloseMainWindow() | Out-Null
Start-Sleep -Seconds 2
Get-Process | Where-Object { $_.Name -eq $appName } | Stop-Process -Force -ErrorAction SilentlyContinue
'DONE'
