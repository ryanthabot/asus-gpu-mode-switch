$ErrorActionPreference = 'SilentlyContinue'
$patterns = 'Wise|Overwolf|OpenBet|Broadcast|Wallpaper|Parsec|DriveFS|GoogleDrive|Jellyfin|Riot|vgc|vgtray| wallpaper'

"=== RUNNING PROCESSES ==="
Get-Process | Where-Object { $_.Name -match $patterns } |
    Select-Object Name, Id, Path | Sort-Object Name | Format-Table -AutoSize | Out-String -Width 300

"=== REGISTRY UNINSTALL (install locations) ==="
$unPaths = @(
  'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
  'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
  'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
)
Get-ItemProperty $unPaths |
  Where-Object { $_.DisplayName -match $patterns } |
  Select-Object DisplayName, DisplayVersion, InstallLocation, DisplayIcon, UninstallString |
  Format-List | Out-String -Width 300

"=== SERVICES ==="
Get-CimInstance Win32_Service |
  Where-Object { $_.Name -match $patterns -or $_.DisplayName -match $patterns -or $_.PathName -match $patterns } |
  Select-Object Name, State, StartMode, PathName | Format-List | Out-String -Width 300

"=== AUTOSTART (Run keys + Startup folders) ==="
$runKeys = @(
  'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run',
  'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run',
  'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
)
foreach ($k in $runKeys) {
  "--- $k"
  (Get-ItemProperty $k).PSObject.Properties |
    Where-Object { $_.Name -notmatch '^PS' -and ($_.Name -match $patterns -or [string]$_.Value -match $patterns) } |
    ForEach-Object { "  $($_.Name) = $($_.Value)" }
}
$startupDirs = @(
  "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup",
  "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\Startup"
)
foreach ($d in $startupDirs) {
  "--- $d"
  Get-ChildItem $d | Where-Object { $_.Name -match $patterns } | ForEach-Object {
    $tgt = ''
    if ($_.Extension -eq '.lnk') {
      $sh = New-Object -ComObject WScript.Shell
      $tgt = $sh.CreateShortcut($_.FullName).TargetPath
    }
    "  $($_.Name) -> $tgt"
  }
}

"=== KNOWN PATH PROBES ==="
$probes = @(
  "$env:ProgramFiles\Wise\Wise Care 365",
  "${env:ProgramFiles(x86)}\Wise\Wise Care 365",
  "$env:ProgramFiles\Overwolf",
  "${env:ProgramFiles(x86)}\Overwolf",
  "$env:LOCALAPPDATA\Overwolf",
  "$env:ProgramFiles\NVIDIA Corporation\NVIDIA Broadcast",
  "$env:ProgramFiles (x86)\Steam\steamapps\common\wallpaper_engine",
  "C:\Program Files (x86)\Steam\steamapps\common\wallpaper_engine",
  "$env:ProgramFiles\Parsec",
  "$env:LOCALAPPDATA\Parsec",
  "$env:ProgramFiles\Google\Drive File Stream",
  "${env:ProgramFiles(x86)}\Google\Drive File Stream",
  "$env:ProgramData\Jellyfin\Server",
  "$env:ProgramFiles\Jellyfin\Server",
  "$env:LOCALAPPDATA\Riot Client",
  "$env:ProgramFiles\Riot Vanguard",
  "${env:ProgramFiles(x86)}\Riot Vanguard"
)
foreach ($p in $probes) {
  if (Test-Path $p) {
    "PRESENT: $p"
    Get-ChildItem $p -Filter *.exe -Recurse -Depth 1 |
      Where-Object { $_.Name -match $patterns -or $_.Name -match 'tray| Wise|Switch' } |
      ForEach-Object { "   EXE: $($_.FullName)" }
  }
}

"=== OPENBLocator / OPENBET DEEP SEARCH (bounded) ==="
foreach ($root in @("$env:USERPROFILE\Desktop", 'D:\OneDrive\Desktop New', "$env:LOCALAPPDATA\Programs", "$env:USERPROFILE\Downloads", 'D:\')) {
  if (Test-Path $root) {
    Get-ChildItem $root -Recurse -Depth 2 -ErrorAction SilentlyContinue |
      Where-Object { $_.Name -match 'openb' } |
      Select-Object -First 12 -ExpandProperty FullName
  }
}

"=== STEAM LIBRARIES (for wallpaper_engine) ==="
$vdf = "C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf"
if (Test-Path $vdf) {
  Select-String -Path $vdf -Pattern '"path"' | ForEach-Object { $_.Line.Trim() }
}

"=== GPU SENSORS AVAILABLE (for monitor design) ==="
Get-CimInstance -Namespace root/wmi -ClassName MSAcpi_ThermalZoneTemperature |
  Select-Object InstanceName, @{n='TempC';e={ [math]::Round(($_.CurrentTemperature/10)-273.15,1) }} |
  Format-Table -AutoSize | Out-String -Width 200
Get-CimInstance Win32_VideoController | Select-Object Name, PNPDeviceID | Format-Table -AutoSize | Out-String -Width 200
"Logical disks:"
Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | Select-Object DeviceID, VolumeName, @{n='SizeGB';e={[math]::Round($_.Size/1GB)}} | Format-Table -AutoSize | Out-String
"DONE"
