$ErrorActionPreference = 'Continue'
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class ESProbe {
    [DllImport("powrprof.dll")] public static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] public static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, ref uint value);
    [DllImport("powrprof.dll")] public static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, ref uint value);
}
"@
$p = [IntPtr]::Zero
$rc = [ESProbe]::PowerGetActiveScheme([IntPtr]::Zero, [ref]$p)
Write-Output ("GetActiveScheme rc=" + $rc)
$scheme = [System.Runtime.InteropServices.Marshal]::PtrToStructure($p, [System.Type][System.Guid])
[System.Runtime.InteropServices.Marshal]::FreeCoTaskMem($p)
Write-Output ("ActiveScheme: " + $scheme)

$subgroups = @(
    "abfc2519-3608-4c2a-94ea-171b0ed546ab",
    "de830923-a562-41af-a086-e3a2c6bad2da",
    "fea3413d-7348-4384-a9fe-79b0934d8ea0",
    "fea3413d-7348-4384-a9fe-3ddfd8d5ca67",
    "00000000-0000-0000-0000-000000000000"
)
$settings = @(
    "e69653ca-cf6f-4166-b25a-4d6a2c1b4e7f",
    "8207fdd8-cc91-45ca-8454-713711e66d39"
)
foreach ($sub in $subgroups) {
    foreach ($set in $settings) {
        $sg = [Guid]$sub
        $st = [Guid]$set
        $dc = [uint32]0; $ac = [uint32]0
        $rcDc = [ESProbe]::PowerReadDCValueIndex([IntPtr]::Zero, [ref]$scheme, [ref]$sg, [ref]$st, [ref]$dc)
        $rcAc = [ESProbe]::PowerReadACValueIndex([IntPtr]::Zero, [ref]$scheme, [ref]$sg, [ref]$st, [ref]$ac)
        if ($rcDc -eq 0 -or $rcAc -eq 0) {
            Write-Output ("FOUND sub=" + $sub + " set=" + $set + " rcDC=" + $rcDc + " valDC=" + $dc + " rcAC=" + $rcAc + " valAC=" + $ac)
        }
    }
}
Write-Output "probe done"
