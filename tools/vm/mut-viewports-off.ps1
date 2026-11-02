# Mutation: changes exactly ONE thing in the active drawing (built for fx-all.dwg, see README) and prints what it changed.
. C:\retrace-vm\fx-lib.ps1
$d = $acad.ActiveDocument
# Trips: viewports
$v = (Get-FxViewports $d 'A-102 Sections')[2]; "A-102 Sections vp3 on: $($v.ViewportOn) -> false"; $v.ViewportOn = $false; Back-ToModel $d
