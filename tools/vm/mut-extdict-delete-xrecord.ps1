# Mutation: changes exactly ONE thing in the active drawing (built for fx-all.dwg, see README) and prints what it changed.
. C:\retrace-vm\fx-lib.ps1
$d = $acad.ActiveDocument
# Trips: extension dictionaries
$e = Get-FxEntity $d $d.ModelSpace 'AcDbCircle' { param($x) $x.Layer -eq 'EXT' }; $e.GetExtensionDictionary().Remove('FX_NOTES') | Out-Null; 'circle on EXT: deleted xrecord FX_NOTES from its extension dictionary'
