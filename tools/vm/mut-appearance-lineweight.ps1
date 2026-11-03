# Mutation: changes exactly ONE thing in the active drawing (built for fx-all.dwg, see README) and prints what it changed.
. C:\retrace-vm\fx-lib.ps1
$d = $acad.ActiveDocument
# Trips: appearance
$e = Get-FxEntity $d $d.ModelSpace 'AcDbCircle' { param($x) $x.Layer -eq 'DOORS' }; "circle on DOORS (centre $($e.Center -join ',')) lineweight: $($e.Lineweight) -> 50"; $e.Lineweight = 50
