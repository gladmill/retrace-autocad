# Mutation: changes exactly ONE thing in the active drawing (built for fx-all.dwg, see README) and prints what it changed.
. C:\retrace-vm\fx-lib.ps1
$d = $acad.ActiveDocument
# Trips: attributes
$b = $d.Layouts.Item('A-103 Details').Block; $r = Get-FxEntity $d $b 'AcDbBlockReference' { param($x) $x.Name -eq 'FX_TITLEBLK' }; $a = $r.GetAttributes() | ? { $_.TagString -eq 'DESC' }; $a.TextString = 'sheet A-103\Pdescription 3\Pextra line'; 'A-103 Details title block DESC: added a third line'
