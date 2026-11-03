# Mutation: changes exactly ONE thing in the active drawing (built for fx-all.dwg, see README) and prints what it changed.
. C:\retrace-vm\fx-lib.ps1
$d = $acad.ActiveDocument
# Trips: paperspace (and the layouts hash)
$b = $d.Layouts.Item('A-102 Sections').Block; $e = Get-FxEntity $d $b 'AcDbText' { param($x) $x.TextString -like 'Scale varies*' }; "text in layout A-102 Sections: '$($e.TextString)' -> 'Scale varies (edited)'"; $e.TextString = 'Scale varies (edited)'
