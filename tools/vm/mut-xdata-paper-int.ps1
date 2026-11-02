# Mutation: changes exactly ONE thing in the active drawing (built for fx-all.dwg, see README) and prints what it changed.
. C:\retrace-vm\fx-lib.ps1
$d = $acad.ActiveDocument
# Trips: xdata
$b = $d.Layouts.Item('A-101 Plan').Block; $e = Get-FxEntity $d $b 'AcDbLine' { param($x) $x.Layer -eq 'XD' }; $t = $null; $v = $null; $e.GetXData('FX_APP_A', [ref]$t, [ref]$v); $i = [array]::IndexOf($t, [int16]1070); "paper line in A-101 Plan, FX_APP_A 1070: $($v[$i]) -> 4"; $v[$i] = [int16]4; $e.SetXData([int16[]]$t, [object[]]$v)
