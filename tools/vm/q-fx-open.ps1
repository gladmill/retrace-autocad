# Query: opens C:\temp\retrace\fixtures\<name>.dwg (name from C:\retrace-vm\fx-arg.txt) and makes it the active drawing.
# An already open copy is closed without saving first, so a mutated drawing is replaced by the file on disk.
$name = (Get-Content C:\retrace-vm\fx-arg.txt -Raw).Trim()
if ($name -notlike "fx-*") { $name = "fx-$name" }
$path = "C:\temp\retrace\fixtures\$name.dwg"
if (-not (Test-Path $path)) { "NO SUCH FIXTURE: $path"; return }
foreach ($d in @($acad.Documents)) { if ($d.FullName -ieq $path) { $d.Close($false); Start-Sleep 1 } }
$d = $acad.Documents.Open($path)
$d.Activate(); Start-Sleep 2
"opened: " + $acad.ActiveDocument.FullName
