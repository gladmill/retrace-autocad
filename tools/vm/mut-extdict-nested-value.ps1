# Mutation: changes exactly ONE thing in the active drawing (built for fx-all.dwg, see README) and prints what it changed.
. C:\retrace-vm\fx-lib.ps1
$d = $acad.ActiveDocument
# Trips: extension dictionaries
$r = $d.Dictionaries.Item('FX_DICT').Item('FX_SUB').Item('NESTED_REC_1'); $t = $null; $v = $null; $r.GetXRecordData([ref]$t, [ref]$v); "FX_DICT/FX_SUB/NESTED_REC_1 90: $($v[1]) -> 778"; $v[1] = [int32]778; $r.SetXRecordData([int16[]]$t, [object[]]$v)
