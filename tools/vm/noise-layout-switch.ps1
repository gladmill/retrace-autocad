# Noise: changes only view state, which a checkpoint must NOT report (only the NOISE section of the dump changes).
. C:\retrace-vm\fx-lib.ps1
$d = $acad.ActiveDocument
$d.ActiveLayout = $d.Layouts.Item('A-101 Plan'); Start-Sleep 1; $d.ActiveLayout = $d.Layouts.Item('Z-Notes'); Start-Sleep 1; Back-ToModel $d; 'switched layout tabs A-101 Plan, Z-Notes and back to Model'
