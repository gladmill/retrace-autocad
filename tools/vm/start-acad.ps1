# From SSH: start AutoCAD 2026 in the interactive session and wait until it is ready (wait-acad.ps1).
# (On an AutoCAD Electrical install, /product ACAD still starts Electrical.)

$tr = "wscript //B //Nologo C:\retrace-vm\run-hidden.vbs C:\retrace-vm\start-acad.cmd"   # hidden cmd; `start` still shows AutoCAD normally
schtasks /create /tn AcadStart /tr $tr /sc once /st 00:00 /it /f /rl limited | Out-Null
schtasks /run /tn AcadStart | Out-Null
Start-Sleep 2
schtasks /delete /tn AcadStart /f | Out-Null   # a leftover one-shot task can fire again later
# then wait until it answers over COM (a CPU threshold misjudged warm starts, which idle well under a cold start's CPU)
$deadline = (Get-Date).AddMinutes(1)
while (-not (Get-Process acad -EA 0) -and (Get-Date) -lt $deadline) { Start-Sleep 1 }
if (-not (Get-Process acad -EA 0)) { 'acad did not start'; exit 1 }
"acad pid $((Get-Process acad | Select-Object -First 1).Id) up"
& (Join-Path $PSScriptRoot 'wait-acad.ps1')
exit $LASTEXITCODE
