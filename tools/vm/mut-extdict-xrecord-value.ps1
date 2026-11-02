# Mutation: changes exactly ONE thing in the active drawing (built for fx-all.dwg, see README) and prints what it changed.
. C:\retrace-vm\fx-lib.ps1
$d = $acad.ActiveDocument
# Trips: extension dictionaries
$e = Get-FxEntity $d $d.ModelSpace 'AcDbCircle' { param($x) $x.Layer -eq 'EXT' }; $r = $e.GetExtensionDictionary().Item('FX_PARAMS'); $t = $null; $v = $null; $r.GetXRecordData([ref]$t, [ref]$v); "circle on EXT, FX_PARAMS 40: $($v[1]) -> 175"; $v[1] = [double]175; $r.SetXRecordData([int16[]]$t, [object[]]$v)
