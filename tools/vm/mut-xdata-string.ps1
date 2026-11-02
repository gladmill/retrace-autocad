# Mutation: changes exactly ONE thing in the active drawing (built for fx-all.dwg, see README) and prints what it changed.
. C:\retrace-vm\fx-lib.ps1
$d = $acad.ActiveDocument
# Trips: xdata
$e = Get-FxEntity $d $d.ModelSpace 'AcDbLine' { param($x) $x.Layer -eq 'XD' }; $t = $null; $v = $null; $e.GetXData('FX_APP_A', [ref]$t, [ref]$v); $i = [array]::IndexOf($t, [int16]1000); "line on XD, FX_APP_A 1000: $($v[$i]) -> pump station 5"; $v[$i] = 'pump station 5'; $e.SetXData([int16[]]$t, [object[]]$v)
