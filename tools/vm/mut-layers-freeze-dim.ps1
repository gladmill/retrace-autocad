# Mutation: changes exactly ONE thing in the active drawing (built for fx-all.dwg, see README) and prints what it changed.
. C:\retrace-vm\fx-lib.ps1
$d = $acad.ActiveDocument
# Trips: layer state
$l = $d.Layers.Item('DIM'); "layer DIM frozen: $($l.Freeze) -> true"; $l.Freeze = $true
