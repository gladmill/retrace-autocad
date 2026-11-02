# Mutation: changes exactly ONE thing in the active drawing (built for fx-all.dwg, see README) and prints what it changed.
. C:\retrace-vm\fx-lib.ps1
$d = $acad.ActiveDocument
# Trips: layer state
$l = $d.Layers.Item('LOCK_L'); "layer LOCK_L locked: $($l.Lock) -> false"; $l.Lock = $false
