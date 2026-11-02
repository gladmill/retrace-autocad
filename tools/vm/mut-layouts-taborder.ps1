# Mutation: changes exactly ONE thing in the active drawing (built for fx-all.dwg, see README) and prints what it changed.
. C:\retrace-vm\fx-lib.ps1
$d = $acad.ActiveDocument
# Trips: layouts
$l = $d.Layouts.Item('A-101 Plan'); "layout A-101 Plan tab order: $($l.TabOrder) -> 4"; $l.TabOrder = 4
