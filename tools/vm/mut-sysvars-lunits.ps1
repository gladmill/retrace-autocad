# Mutation: changes exactly ONE thing in the active drawing (built for fx-all.dwg, see README) and prints what it changed.
. C:\retrace-vm\fx-lib.ps1
$d = $acad.ActiveDocument
# Trips: system variables
"LUNITS: $($d.GetVariable('LUNITS')) -> 2"; $d.SetVariable('LUNITS', [int16]2)
