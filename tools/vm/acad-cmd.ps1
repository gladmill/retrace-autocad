# Runs inside the interactive session (scheduled task). Sends commands to the running AutoCAD over COM,
# writes the command-line log delta + a screenshot to C:\retrace-vm\out, then done.txt.
# -RunId: the run this worker belongs to (run-in-session.ps1 writes it to C:\retrace-vm\out\run.txt). Once run.txt holds anything else
# (the run timed out or was stopped as stuck, or a newer run started), it sends no more commands and writes nothing more.
param([string]$Cmds = '', [int]$WaitSec = 3, [string]$Query = '', [string]$RunId = '')
$out = 'C:\retrace-vm\out'; New-Item -ItemType Directory -Force $out | Out-Null
$runFile = Join-Path $out 'run.txt'
function Current { -not $RunId -or "$(Get-Content $runFile -Raw -EA SilentlyContinue)".Trim() -eq $RunId }
function Stop-Superseded { if ($RunId) { 'stopped' | Set-Content (Join-Path $out "stopped-$RunId.txt") }; exit 0 }
$res = Join-Path $out 'result.txt'; "" | Set-Content $res
function W($s) { if (Current) { Add-Content $res $s } }
if (-not $Cmds -and (Test-Path C:\retrace-vm\cmds.txt)) { $Cmds = Get-Content C:\retrace-vm\cmds.txt -Raw }
try {
  $acad = [Runtime.InteropServices.Marshal]::GetActiveObject('AutoCAD.Application')
  $doc = $acad.ActiveDocument
  if (-not $doc) { W "no active doc, adding one"; $doc = $acad.Documents.Add(); Start-Sleep 5; $doc = $acad.ActiveDocument }
  W "acad: $($acad.Version) doc: $($doc.Name)"
  # read-only COM queries ($acad/$doc in scope); no LISP
  if ($Query) { foreach ($l in (. $Query | Out-String -Width 200) -split "`r?`n") { if ($l) { W $l } } }
  if ($doc.GetVariable('LOGFILEMODE') -ne 1) { $doc.SetVariable('LOGFILEMODE', 1) }
  $logdir = $doc.GetVariable('LOGFILEPATH'); W "logpath: $logdir"
  $prog = Join-Path $out 'progress.txt'; $n = 0
  foreach ($c in ($Cmds -split '\|')) {
    if ($c -eq '') { continue }
    if (-not (Current)) { Stop-Superseded }
    W ">> $c"
    $n++; Add-Content $prog "sent $n $c"   # run-in-session.ps1 -StallSec watches this
    $doc.SendCommand(($c -replace '\\n', "`n"))
    if (Current) { Add-Content $prog "back $n" }
    Start-Sleep -Seconds $WaitSec
  }
  if (-not (Current)) { Stop-Superseded }
  Start-Sleep -Seconds 1
  $doc.SetVariable('LOGFILEMODE', 0)   # closes the log so it is flushed
  $log = Get-ChildItem $logdir -Filter *.log | sort LastWriteTime -desc | select -first 1
  W "--- $($log.Name) (tail) ---"; Get-Content $log.FullName -Tail 40 | % { W $_ }
  $doc.SetVariable('LOGFILEMODE', 1)
} catch { W "ERROR: $_" }
try {
  Add-Type -AssemblyName System.Windows.Forms, System.Drawing
  $b = [Windows.Forms.Screen]::PrimaryScreen.Bounds
  $bmp = New-Object Drawing.Bitmap $b.Width, $b.Height
  $g = [Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($b.Location, [Drawing.Point]::Empty, $b.Size)
  $bmp.Save((Join-Path $out 'screen.png')); W "screenshot ok"
} catch { W "screenshot ERROR: $_" }
if (-not (Current)) { Stop-Superseded }
$(if ($RunId) { $RunId } else { 'done' }) | Set-Content (Join-Path $out 'done.txt')
