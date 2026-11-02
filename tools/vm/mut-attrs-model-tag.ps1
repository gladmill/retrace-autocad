# Mutation: changes exactly ONE thing in the active drawing (built for fx-all.dwg, see README) and prints what it changed.
. C:\retrace-vm\fx-lib.ps1
$d = $acad.ActiveDocument
# Trips: attributes
foreach ($e in $d.ModelSpace) { if ($e.ObjectName -eq 'AcDbBlockReference' -and $e.Name -eq 'FX_TAG') { $a = $e.GetAttributes() | ? { $_.TagString -eq 'TAGNO' -and $_.TextString -eq 'T-205' }; if ($a) { $a.TextString = 'T-206'; 'model space FX_TAG TAGNO: T-205 -> T-206'; break } } }
