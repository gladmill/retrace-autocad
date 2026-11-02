# Mutation: changes exactly ONE thing in the active drawing (built for fx-all.dwg, see README) and prints what it changed.
. C:\retrace-vm\fx-lib.ps1
$d = $acad.ActiveDocument
# Trips: attributes
$b = $d.Layouts.Item('A-102 Sections').Block; $r = Get-FxEntity $d $b 'AcDbBlockReference' { param($x) $x.Name -eq 'FX_TITLEBLK' }; $a = $r.GetAttributes() | ? { $_.TagString -eq 'REV' }; "A-102 Sections title block REV: $($a.TextString) -> E"; $a.TextString = 'E'
