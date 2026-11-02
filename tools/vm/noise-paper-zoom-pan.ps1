# Noise: changes only view state, which a checkpoint must NOT report (only the NOISE section of the dump changes).
. C:\retrace-vm\fx-lib.ps1
$d = $acad.ActiveDocument
$d.ActiveLayout = $d.Layouts.Item('A-101 Plan'); Start-Sleep 1
$d.ActiveSpace = 0   # paper space
Send-Cmd $d "_.ZOOM`n0.5`n"; Send-Cmd $d "_.-PAN`n0,0`n3,2`n"
Back-ToModel $d; 'zoomed and panned paper space of A-101 Plan (the overall viewport changes), back to Model'
