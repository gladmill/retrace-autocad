# Mutation: changes exactly ONE thing in the active drawing (built for fx-all.dwg, see README) and prints what it changed.
. C:\retrace-vm\fx-lib.ps1
$d = $acad.ActiveDocument
# Trips: layouts
$b = $d.Layouts.Item('A-102 Sections').Block; $e = Get-FxEntity $d $b 'AcDbText' { param($x) $x.TextString -like 'Sheet note 2*' }; "moved text '$($e.TextString)' in layout A-102 Sections by (0, -0.5)"; $e.Move((Pt 0 0), (Pt 0 -0.5))
