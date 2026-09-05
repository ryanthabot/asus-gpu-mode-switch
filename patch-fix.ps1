$f = Join-Path $PSScriptRoot 'src\GpuModeSwitch.cs'
$c = [System.IO.File]::ReadAllText($f)
$dup = "                : `"`"`;" + [Environment]::NewLine + "                : `"`"`;"
$c = $c.Replace($dup, "                : `"`"`;")
$c = $c.Replace("float cy = r.Y + r.Height / 2;", "float cy = (float)(r.Y + r.Height / 2);")
[System.IO.File]::WriteAllText($f, $c)
Write-Host "patched"
