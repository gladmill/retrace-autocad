# Mutation: changes exactly ONE thing in the active drawing (built for fx-all.dwg, see README) and prints what it changed.
. C:\retrace-vm\fx-lib.ps1
$d = $acad.ActiveDocument
# Trips: viewports
$v = (Get-FxViewports $d 'Z-Notes')[0]; "Z-Notes vp1 locked: $($v.DisplayLocked) -> true"; $v.DisplayLocked = $true; Back-ToModel $d
