# Mutation: changes exactly ONE thing in the active drawing (built for fx-all.dwg, see README) and prints what it changed.
. C:\retrace-vm\fx-lib.ps1
$d = $acad.ActiveDocument
# Trips: xdata
$e = Get-FxEntity $d $d.ModelSpace 'AcDbCircle' { param($x) $x.Layer -eq 'XD' }; 'circle on XD: removed all FX_APP_B xdata'; $e.SetXData([int16[]]@(1001), [object[]]@('FX_APP_B'))
