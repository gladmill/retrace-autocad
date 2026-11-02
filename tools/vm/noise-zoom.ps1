# Noise: changes only view state, which a checkpoint must NOT report (only the NOISE section of the dump changes).
. C:\retrace-vm\fx-lib.ps1
$d = $acad.ActiveDocument
$acad.ZoomCenter((Pt 150 120), 150); 'view zoomed in model space (VIEWCTR / VIEWSIZE change only)'
