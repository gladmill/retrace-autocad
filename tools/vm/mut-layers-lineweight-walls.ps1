# Mutation: changes exactly ONE thing in the active drawing (built for fx-all.dwg, see README) and prints what it changed.
. C:\retrace-vm\fx-lib.ps1
$d = $acad.ActiveDocument
# Trips: layer state
$l = $d.Layers.Item('WALLS'); "layer WALLS lineweight: $($l.Lineweight) -> 35"; $l.Lineweight = 35
