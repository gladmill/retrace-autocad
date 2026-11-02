# Noise: changes only view state, which a checkpoint must NOT report (only the NOISE section of the dump changes).
. C:\retrace-vm\fx-lib.ps1
$d = $acad.ActiveDocument
Send-Cmd $d "_.-PAN`n0,0`n25,18`n"; 'view panned in model space (VIEWCTR change only)'
