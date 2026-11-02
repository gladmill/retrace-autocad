# Mutation: changes exactly ONE thing in the active drawing (built for fx-all.dwg, see README) and prints what it changed.
. C:\retrace-vm\fx-lib.ps1
$d = $acad.ActiveDocument
# Trips: viewports
$v = (Get-FxViewports $d 'A-101 Plan')[0]; "A-101 Plan vp1 scale: $($v.CustomScale) -> 0.04"; $v.CustomScale = 0.04; Back-ToModel $d
