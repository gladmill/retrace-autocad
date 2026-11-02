# Mutation: changes exactly ONE thing in the active drawing (built for fx-all.dwg, see README) and prints what it changed.
. C:\retrace-vm\fx-lib.ps1
$d = $acad.ActiveDocument
# Trips: attributes
$b = $d.Layouts.Item('A-101 Plan').Block; $r = Get-FxEntity $d $b 'AcDbBlockReference' { param($x) $x.Name -eq 'FX_TITLEBLK' }; $a = $r.GetAttributes() | ? { $_.TagString -eq 'INTERNAL' }; "A-101 Plan title block INTERNAL invisible: $($a.Invisible) -> false"; $a.Invisible = $false
