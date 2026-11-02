# Noise: changes only view state, which a checkpoint must NOT report (only the NOISE section of the dump changes).
. C:\retrace-vm\fx-lib.ps1
$d = $acad.ActiveDocument
$d.Regen(1); Start-Sleep 1; 'REGEN all'
