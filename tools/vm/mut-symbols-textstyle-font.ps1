# Mutation: changes exactly ONE thing in the active drawing (built for fx-all.dwg, see README) and prints what it changed.
. C:\retrace-vm\fx-lib.ps1
$d = $acad.ActiveDocument
# Trips: symbol tables
$s = $d.TextStyles.Item('FX_Narrow'); "text style FX_Narrow font: $($s.fontFile) -> simplex.shx"; $s.fontFile = 'simplex.shx'
