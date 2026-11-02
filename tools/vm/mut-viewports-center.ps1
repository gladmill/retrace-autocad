# Mutation: changes exactly ONE thing in the active drawing (built for fx-all.dwg, see README) and prints what it changed.
. C:\retrace-vm\fx-lib.ps1
$d = $acad.ActiveDocument
# Trips: viewports
$v = (Get-FxViewports $d 'A-102 Sections')[0]; "A-102 Sections vp1 view target: $($v.Target -join ',') -> 540,545,0"; $v.Target = (Pt 540 545); Back-ToModel $d
