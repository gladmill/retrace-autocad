# Query: makes the layout named in C:\retrace-vm\fx-arg.txt current (Model, or a layout tab) and zooms to its extents, so the
# screenshot acad-cmd.ps1 takes afterwards (C:\retrace-vm\out\screen.png) shows it. Used by fixtures.sh shot.
$name = (Get-Content C:\retrace-vm\fx-arg.txt -Raw).Trim()
$d = $acad.ActiveDocument
$d.ActiveLayout = $d.Layouts.Item($name)
if ($name -ne 'Model') { $d.ActiveSpace = 0 }
Start-Sleep 1
$acad.ZoomExtents()
Start-Sleep 2
"layout $($d.ActiveLayout.Name) shown in $($d.Name)"
