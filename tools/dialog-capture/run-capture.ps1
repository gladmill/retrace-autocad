# Runs DialogCapture on the interactive desktop of the logged-on user via a scheduled task.
# (Over SSH you are in session 0, which has no visible desktop; a task created with /it runs in the
# user's interactive session instead.) Run from an elevated PowerShell, with the user logged on.
#
#   .\run-capture.ps1 -DllDir C:\capture\res -OutDir C:\capture\out [-Exe C:\capture\bin\DialogCapture.exe]
#                     [-Only ArxDbg:ARXDBG_DLG_SPLASH] [-TimeoutSec 900]
param(
    [Parameter(Mandatory = $true)][string]$DllDir,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [string]$Exe = (Join-Path $PSScriptRoot 'DialogCapture.exe'),
    [string]$Only = '',
    [int]$TimeoutSec = 900
)
$ErrorActionPreference = 'Stop'

$Exe = (Resolve-Path $Exe).Path
$DllDir = (Resolve-Path $DllDir).Path
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$marker = Join-Path $OutDir 'done.txt'
Remove-Item $marker -ErrorAction SilentlyContinue

$taskArgs = "`"$DllDir`" `"$OutDir`" --hide-console"
if ($Only) { $taskArgs += " --only $Only" }
$tr = "`"$Exe`" $taskArgs"

# /it: only run when the user is logged on, in their interactive session. /f: overwrite an existing task.
schtasks /create /tn DialogCapture /tr $tr /sc once /st 00:00 /it /f /rl highest | Out-Host
if ($LASTEXITCODE -ne 0) { throw "schtasks /create failed ($LASTEXITCODE)" }
schtasks /run /tn DialogCapture | Out-Host
if ($LASTEXITCODE -ne 0) { throw "schtasks /run failed ($LASTEXITCODE)" }

$deadline = (Get-Date).AddSeconds($TimeoutSec)
while (-not (Test-Path $marker)) {
    if ((Get-Date) -gt $deadline) {
        Write-Host "Timed out after $TimeoutSec s waiting for $marker"
        $log = Join-Path $OutDir 'progress.log'
        if (Test-Path $log) { Write-Host '--- last lines of progress.log ---'; Get-Content $log -Tail 15 }
        schtasks /query /tn DialogCapture /v /fo list | Select-String 'Status|Last Result'
        exit 1
    }
    Start-Sleep -Seconds 2
}
Write-Host '--- done.txt ---'
Get-Content $marker
Write-Host "PNG: $((Get-ChildItem $OutDir -Filter *.png).Count)  JSON: $((Get-ChildItem $OutDir -Filter *.json).Count)  in $OutDir"
