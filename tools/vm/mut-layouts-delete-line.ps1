# Mutation: changes exactly ONE thing in the active drawing (built for fx-all.dwg, see README) and prints what it changed.
. C:\retrace-vm\fx-lib.ps1
$d = $acad.ActiveDocument
# Trips: layouts
$b = $d.Layouts.Item('Z-Notes').Block; $e = Get-FxEntity $d $b 'AcDbLine' { param($x) $x.Layer -eq 'DIM' }; "deleted line on DIM in layout Z-Notes: $($e.StartPoint -join ',') - $($e.EndPoint -join ',')"; $e.Delete()
