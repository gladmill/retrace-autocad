# Query: builds fixtures\fx-picks.dwg from a new drawing: plain lines for the pick-point tests (BREAK, TRIM, FILLET), model space only.
#   BREAK  : (0,0)-(100,0)                 pick two points on it
#   TRIM   : cutter (250,-30)-(250,30), target (200,0)-(300,0); pick the right half of the target
#   FILLET : P (400,0)-(500,0), Q (450,-50)-(450,50); pick the left part of P and the upper part of Q (radius 0)
$d = $acad.Documents.Add(); Start-Sleep 4
$d = $acad.ActiveDocument
$ms = $d.ModelSpace
function Ln($a, $b, $c, $e) { [void]$ms.AddLine([double[]]@($a, $b, 0), [double[]]@($c, $e, 0)) }
Ln 0 0 100 0
Ln 250 -30 250 30
Ln 200 0 300 0
Ln 400 0 500 0
Ln 450 -50 450 50
$d.SendCommand("ZOOM E `n")
Start-Sleep 2
$path = 'C:\temp\retrace\fixtures\fx-picks.dwg'
New-Item -ItemType Directory -Force -Path (Split-Path $path) | Out-Null
if (Test-Path $path) { Remove-Item $path -Force }
$d.SaveAs($path)
"saved: " + $d.FullName + "  entities: " + $ms.Count
