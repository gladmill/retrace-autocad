# Mutation: changes exactly ONE thing in the active drawing (built for fx-all.dwg, see README) and prints what it changed.
# Trips: layouts (the layout's own PSLTSCALE, field psltscale). AutoCAD saves PSLTSCALE per paper layout: set it INSIDE one layout, leave via
# another paper layout (AutoCAD writes the variable into the layout then) and end in Model. Do not set the variable again in Model: it is backed
# by the last paper layout (the database's *Paper_Space), so that would change the value of the last layout visited, not Model's.
. C:\retrace-vm\fx-lib.ps1
$d = $acad.ActiveDocument
Retry { $d.ActiveLayout = $d.Layouts.Item('A-101 Plan') }; Start-Sleep -Milliseconds 1200
$was = Retry { $d.GetVariable('PSLTSCALE') }
Retry { $d.SetVariable('PSLTSCALE', [int16]1) }
"A-101 Plan PSLTSCALE (set inside the layout): $was -> 1"
Retry { $d.ActiveLayout = $d.Layouts.Item('A-102 Sections') }; Start-Sleep -Milliseconds 1200
Retry { $d.ActiveLayout = $d.Layouts.Item('Model') }; Start-Sleep -Milliseconds 1200
"left via A-102 Sections, back in Model (PSLTSCALE reads $(Retry { $d.GetVariable('PSLTSCALE') }) there)"
