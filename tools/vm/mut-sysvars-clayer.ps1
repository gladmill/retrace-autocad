# Mutation: changes exactly ONE thing in the active drawing (built for fx-all.dwg, see README) and prints what it changed.
. C:\retrace-vm\fx-lib.ps1
$d = $acad.ActiveDocument
# Trips: system variables
"CLAYER: $($d.GetVariable('CLAYER')) -> WALLS"; $d.SetVariable('CLAYER', 'WALLS')
