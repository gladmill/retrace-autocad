# Runs in the interactive session (scheduled task): one line in C:\retrace-vm\out\ready.txt: "ready" (AutoCAD answers over COM and is quiescent),
# "busy" (answers but a command, prompt or dialog is active), or "none: <reason>" (not running / not registered yet / call rejected).
#   -Tag t: write C:\retrace-vm\out\ready-<t>.txt instead (run-in-session.ps1's stall checks: a late answer can't be mistaken)
param([string]$Tag = '')
$f = if ($Tag) { "C:\retrace-vm\out\ready-$Tag.txt" } else { 'C:\retrace-vm\out\ready.txt' }
try {
  $acad = [Runtime.InteropServices.Marshal]::GetActiveObject('AutoCAD.Application')
  $s = if ($acad.GetAcadState().IsQuiescent) { 'ready' } else { 'busy' }
} catch { $s = 'none: ' + $_.Exception.Message.Split("`n")[0] }
Set-Content $f $s
