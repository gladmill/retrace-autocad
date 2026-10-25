# From SSH (session 0): run acad-cmd.ps1 in the interactive session and wait for it.
#   -StallSec n: (default 0 = off) stop the run when one command has run n s and AutoCAD answers COM as busy twice (a prompt or
#   dialog, or a long command with a progress meter): later commands are not sent, prints STUCK and exits 4, AutoCAD left as it
#   is (C:\retrace-vm\esc.ps1 as a one-shot task leaves a prompt). Off by default: RETRACEREPLAY keeps the command line busy for a long
#   time on purpose.
param([string]$Cmds = '', [int]$WaitSec = 3, [int]$TimeoutSec = 120, [string]$Query = '', [int]$StallSec = 0)
$out = 'C:\retrace-vm\out'; $done = "$out\done.txt"; $prog = "$out\progress.txt"
# This run's id: the worker stops (sends nothing more, writes nothing more) once run.txt holds anything else.
$RunId = [guid]::NewGuid().ToString('N')
Set-Content "$out\run.txt" $RunId
Remove-Item $done, "$out\result.txt", $prog -EA SilentlyContinue
Get-ChildItem $out -Filter 'stopped-*.txt' -EA SilentlyContinue | Remove-Item -EA SilentlyContinue
Get-ChildItem $out -Filter 'ready-*.txt' -EA SilentlyContinue | Remove-Item -EA SilentlyContinue
if ($StallSec -gt 0 -and $TimeoutSec -lt $StallSec + 60) { $TimeoutSec = $StallSec + 60 }
# commands go through a file: schtasks /tr mangles spaces, quotes and parentheses
Set-Content C:\retrace-vm\cmds.txt $Cmds -Encoding UTF8 -NoNewline
$tr = "wscript //B //Nologo C:\retrace-vm\run-hidden.vbs powershell -NoProfile -ExecutionPolicy Bypass -File C:\retrace-vm\acad-cmd.ps1 -WaitSec $WaitSec -RunId $RunId" + $(if ($Query) { " -Query $Query" } else { "" })
schtasks /create /tn AcadCmd /tr $tr /sc once /st 00:00 /it /f /rl limited | Out-Null
schtasks /run /tn AcadCmd | Out-Null

# One readiness probe (acad-ready.ps1) as a one-shot task, into its own file; 'none: no answer' when it didn't reply in time.
$script:probe = 0
function Get-State {
  $script:probe++; $tag = "$RunId-$($script:probe)"; $f = "$out\ready-$tag.txt"
  schtasks /create /tn AcadReadyStall /tr "wscript //B //Nologo C:\retrace-vm\run-hidden.vbs powershell -NoProfile -ExecutionPolicy Bypass -File C:\retrace-vm\acad-ready.ps1 -Tag $tag" /sc once /st 00:00 /it /f /rl limited | Out-Null
  schtasks /run /tn AcadReadyStall | Out-Null
  $until = (Get-Date).AddSeconds(20)
  while (-not (Test-Path $f) -and (Get-Date) -lt $until) { Start-Sleep -Milliseconds 500 }
  schtasks /delete /tn AcadReadyStall /f 2>$null | Out-Null
  if (Test-Path $f) { (Get-Content $f -Raw).Trim() } else { 'none: no answer' }
}

$deadline = (Get-Date).AddSeconds($TimeoutSec); $busy = 0; $next = (Get-Date).AddSeconds($StallSec); $stuck = $null
while (-not ((Test-Path $done) -and "$(Get-Content $done -Raw -EA SilentlyContinue)".Trim() -eq $RunId)) {
  if ((Get-Date) -gt $deadline) { Set-Content "$out\run.txt" "timeout $RunId"; 'TIMEOUT'; break }
  if ($StallSec -gt 0 -and (Get-Date) -ge $next -and (Test-Path $prog)) {
    $last = @(Get-Content $prog -EA SilentlyContinue)[-1]
    if ($last -like 'sent *' -and ((Get-Date) - (Get-Item $prog).LastWriteTime).TotalSeconds -ge $StallSec) {
      $state = Get-State
      $next = (Get-Date).AddSeconds($(if ($state -eq 'none: no answer') { 60 } else { 5 }))
      if ($state -eq 'busy') { $busy++ } else { $busy = 0 }
      if ($busy -ge 2) { $stuck = $last.Substring(5); break }
    } else { $busy = 0 }
  }
  Start-Sleep 1
}
schtasks /delete /tn AcadCmd /f | Out-Null
if ($stuck) {
  Set-Content "$out\run.txt" "stopped $RunId"
  Get-Content "$out\result.txt" -EA SilentlyContinue
  $sn, $sc = $stuck -split ' ', 2
  "STUCK: command $sn ($sc) has run over ${StallSec}s and AutoCAD answers as busy (a prompt or dialog, or a long command showing progress). The run stopped there; later commands were not sent. AutoCAD was left as it is."
  exit 4
}
Get-Content "$out\result.txt"
