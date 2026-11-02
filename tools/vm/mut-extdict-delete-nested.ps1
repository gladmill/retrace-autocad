# Mutation: changes exactly ONE thing in the active drawing (built for fx-all.dwg, see README) and prints what it changed.
. C:\retrace-vm\fx-lib.ps1
$d = $acad.ActiveDocument
# Trips: extension dictionaries
$d.Dictionaries.Item('FX_DICT').Item('FX_SUB').Remove('NESTED_REC_2') | Out-Null; 'deleted FX_DICT/FX_SUB/NESTED_REC_2'
