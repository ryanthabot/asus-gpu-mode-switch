$ErrorActionPreference = 'Stop'
Get-Process | Where-Object { $_.Name -like 'Big*' -or $_.Name -like 'GPU Mode*' } | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
$new = "D:\OneDrive\Desktop New\Big's GPU Switch & Game Optimizer.exe"
Copy-Item "C:\Users\bd799\app_v123.exe" -Destination $new -Force
Remove-Item "C:\Users\bd799\app_v123.exe", "C:\Users\bd799\app_v130.exe" -Force -ErrorAction SilentlyContinue
"deployed: " + (Get-Item -LiteralPath $new).Length + " bytes  @ " + (Get-Item -LiteralPath $new).LastWriteTime
