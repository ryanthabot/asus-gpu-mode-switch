$ErrorActionPreference = 'Stop'
$new = "D:\OneDrive\Desktop New\Big's GPU Switch & Game Optimizer.exe"
Copy-Item "C:\Users\bd799\app_v130.exe" -Destination $new -Force
Remove-Item "C:\Users\bd799\app_v130.exe" -Force
Get-Process | Where-Object { $_.Name -match 'GPU Mode|Big' } | Stop-Process -Force -ErrorAction SilentlyContinue
Remove-Item "D:\OneDrive\Desktop New\GPU Mode Switch.exe" -ErrorAction SilentlyContinue
"deployed: " + (Get-Item -LiteralPath $new).Length + " bytes"
