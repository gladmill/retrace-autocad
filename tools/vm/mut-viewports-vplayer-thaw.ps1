# Mutation: changes exactly ONE thing in the active drawing (built for fx-all.dwg, see README) and prints what it changed.
. C:\retrace-vm\fx-lib.ps1
$d = $acad.ActiveDocument
# Trips: viewports
$v = (Get-FxViewports $d 'A-102 Sections')[0]; 'A-102 Sections vp1: thaw layer HIDE in this viewport only (was frozen there: WALLS, HIDE)'; Send-Cmd $d ("_.VPLAYER`n_T`nHIDE`n_S`n(handent `"$($v.Handle)`")`n`n`n"); Back-ToModel $d
