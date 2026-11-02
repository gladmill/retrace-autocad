# Mutation: changes exactly ONE thing in the active drawing (built for fx-all.dwg, see README) and prints what it changed.
. C:\retrace-vm\fx-lib.ps1
$d = $acad.ActiveDocument
# Trips: xdata
$e = Get-FxEntity $d $d.ModelSpace 'AcDbCircle' { param($x) $x.Layer -eq 'XD' }; $t = $null; $v = $null; $e.GetXData('FX_APP_A', [ref]$t, [ref]$v); $i = [array]::IndexOf($t, [int16]1040); "circle on XD, FX_APP_A 1040: $($v[$i]) -> 4.25"; $v[$i] = [double]4.25; $e.SetXData([int16[]]$t, [object[]]$v)
