# From SSH (session 0): waits until AutoCAD in the interactive session answers over COM and is quiescent; prints "ready after Ns" (exit 0)
# or the last state on timeout (exit 1). Window titles and handles can't be read from session 0, so readiness is asked over COM.
param([int]$TimeoutSec = 240, [int]$EverySec = 2)
$f = 'C:\retrace-vm\out\ready.txt'
# through run-hidden.vbs: a powershell task flashes a console window on every poll, even with -WindowStyle Hidden
$tr = 'wscript //B //Nologo C:\retrace-vm\run-hidden.vbs powershell -NoProfile -ExecutionPolicy Bypass -File C:\retrace-vm\acad-ready.ps1'
schtasks /create /tn AcadReady /tr $tr /sc once /st 00:00 /it /f /rl limited 2>$null | Out-Null
$t0 = Get-Date; $state = 'none: not checked'
try {
  while (((Get-Date) - $t0).TotalSeconds -lt $TimeoutSec) {
    Remove-Item $f -EA SilentlyContinue
    schtasks /run /tn AcadReady | Out-Null
    for ($i = 0; $i -lt 20 -and -not (Test-Path $f); $i++) { Start-Sleep -Milliseconds 250 }
    if (Test-Path $f) { $state = (Get-Content $f -Raw).Trim() }
    if ($state -eq 'ready') { "ready after $([int]((Get-Date) - $t0).TotalSeconds)s"; exit 0 }
    Start-Sleep $EverySec
  }
  "timeout after ${TimeoutSec}s: $state"; exit 1
} finally { schtasks /delete /tn AcadReady /f 2>$null | Out-Null }
