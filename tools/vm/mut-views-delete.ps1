# Mutation: changes exactly ONE thing in the active drawing (built for fx-all.dwg, see README) and prints what it changed.
. C:\retrace-vm\fx-lib.ps1
$d = $acad.ActiveDocument
# Trips: named views
$d.Views.Item('FX_MV_LAYERS').Delete(); 'deleted named view FX_MV_LAYERS (model space)'
