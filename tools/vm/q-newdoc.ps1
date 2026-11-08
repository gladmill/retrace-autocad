# Query: opens a new drawing (default template) and makes it the active one. Run it in its own run-in-session.ps1 call:
# acad-cmd.ps1 sends later commands to the document that was active BEFORE the query ran.
# SDI = 1: Documents.Add is not available ("Method not available in SDI mode"), and opening a drawing replaces the one that is open (AutoCAD asks to save
# it first, and the query would hang at that question). So with SDI on, a copy of the template is opened only when the open drawing has no unsaved changes.
try { $d = $acad.Documents.Add() }
catch {
  $doc0 = $acad.ActiveDocument
  if ($doc0 -and -not $doc0.Saved) { return "SDI is on and $($doc0.Name) has unsaved changes: not replaced" }
  $tpl = Join-Path $acad.Preferences.Files.TemplateDwgPath 'acad.dwt'
  $copy = Join-Path 'C:\retrace-vm\out' ("blank-" + [guid]::NewGuid().ToString('N').Substring(0, 6) + ".dwg")
  Copy-Item $tpl $copy -Force
  $d = $acad.Documents.Open($copy)
}
Start-Sleep 4
"new drawing: " + $acad.ActiveDocument.Name
