# Query: opens C:\temp\retrace\fixtures\<name>.dwg (name from C:\retrace-vm\fx-arg.txt) and makes it the active drawing.
# An already open copy is closed without saving first, so a mutated drawing is replaced by the file on disk.
$name = (Get-Content C:\retrace-vm\fx-arg.txt -Raw).Trim()
if ($name -notlike "fx-*") { $name = "fx-$name" }
$path = "C:\temp\retrace\fixtures\$name.dwg"
if (-not (Test-Path $path)) { "NO SUCH FIXTURE: $path"; return }
foreach ($d in @($acad.Documents)) { if ($d.FullName -ieq $path) { $d.Close($false); Start-Sleep 1 } }
$d = $acad.Documents.Open($path)
$d.Activate(); Start-Sleep 2
# A viewport reads ViewportOn = False until its layout has been current once IN THIS SESSION, whatever the saved file says (verified: a fixture
# saved after visiting every layout still reads 0 right after opening). Visit each layout once and go back to Model, so that a baseline
# recorded now and a replay that switches layout tabs agree. A layout that was never current in the session has no overall viewport yet.
. C:\retrace-vm\fx-lib.ps1
$d = $acad.ActiveDocument
foreach ($l in (Retry { @($d.Layouts) | ? { $_.Name -ne 'Model' } | sort TabOrder })) { Retry { $d.ActiveLayout = $l }; Start-Sleep -Milliseconds 700; Retry { $d.ActiveSpace = 0 } }
Retry { $d.ActiveSpace = 1 }; Retry { $d.ActiveLayout = $d.Layouts.Item('Model') }
"opened: " + $acad.ActiveDocument.FullName
