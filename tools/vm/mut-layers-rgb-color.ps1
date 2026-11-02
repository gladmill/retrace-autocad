# Mutation: changes exactly ONE thing in the active drawing (built for fx-all.dwg, see README) and prints what it changed.
. C:\retrace-vm\fx-lib.ps1
$d = $acad.ActiveDocument
# Trips: layer state
$l = $d.Layers.Item('RGB_L'); $c = $acad.GetInterfaceObject('AutoCAD.AcCmColor.25'); $c.SetRGB(0, 128, 255); $l.TrueColor = $c; 'layer RGB_L true colour: 255,128,32 -> 0,128,255'
