# Mutation: changes exactly ONE thing in the active drawing (built for fx-all.dwg, see README) and prints what it changed.
. C:\retrace-vm\fx-lib.ps1
$d = $acad.ActiveDocument
# Trips: symbol tables
$d.Blocks.Item('FX_INNER').AddCircle((Pt 0 0), 0.5) | Out-Null; 'added a circle to block definition FX_INNER (used by the nested block FX_OUTER)'
