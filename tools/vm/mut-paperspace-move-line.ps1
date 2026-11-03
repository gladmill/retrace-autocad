# Mutation: changes exactly ONE thing in the active drawing (built for fx-all.dwg, see README) and prints what it changed.
. C:\retrace-vm\fx-lib.ps1
$d = $acad.ActiveDocument
# Trips: paperspace (and the layouts hash)
$b = $d.Layouts.Item('A-101 Plan').Block; $e = Get-FxEntity $d $b 'AcDbLine' { param($x) $x.Layer -eq 'DIM' }; "moved line on DIM in layout A-101 Plan by (0, -0.25): $($e.StartPoint -join ',') - $($e.EndPoint -join ',')"; $e.Move((Pt 0 0), (Pt 0 -0.25))
