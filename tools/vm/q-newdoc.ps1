# Query: opens a new drawing (default template) and makes it the active one. Run it in its own run-in-session.ps1 call:
# acad-cmd.ps1 sends later commands to the document that was active BEFORE the query ran.
$d = $acad.Documents.Add()
Start-Sleep 4
"new drawing: " + $acad.ActiveDocument.Name
