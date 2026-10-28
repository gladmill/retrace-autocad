#!/usr/bin/env bash
# Runs on your Mac or Linux box: what dialog-watch.ps1 sees on the VM. Host: RETRACE_VM_HOST.
#   dialogs.sh [n]                 the dialogs open in AutoCAD now and the last n (default 10) lines of the dialog log
#   dialogs.sh --start             start the watcher in the interactive session until logoff (a one-shot task; no-op if it runs)
#   dialogs.sh --pic <file> [dir]  copy a dialog picture (dialogs\<file> in the log) back
set -euo pipefail
HOST="${RETRACE_VM_HOST:-retrace-vm}"
# the watcher's powershell (not this ssh session's, whose command line names the script too)
running='Get-CimInstance Win32_Process | Where-Object { $_.ProcessId -ne $PID -and $_.Name -eq "powershell.exe" -and $_.CommandLine -match "-File\s+\S*dialog-watch\.ps1" }'
case "${1:-}" in
  --pic) scp -q "$HOST:C:/retrace-vm/out/dialogs/${2:?picture file name}" "${3:-.}"; exit 0 ;;
  --start)
    ssh -o BatchMode=yes "$HOST" "if ($running) { 'watcher already running'; exit 0 }
schtasks /create /tn AcadDialogWatchOnce /tr \"wscript //B //Nologo C:\\retrace-vm\\run-hidden.vbs powershell -NoProfile -ExecutionPolicy Bypass -File C:\\retrace-vm\\dialog-watch.ps1\" /sc once /st 00:00 /it /f /rl limited 2>\$null | Out-Null
schtasks /run /tn AcadDialogWatchOnce | Out-Null; Start-Sleep 3; schtasks /delete /tn AcadDialogWatchOnce /f | Out-Null
if ($running) { 'watcher started' } else { 'watcher did not start'; exit 1 }" | LC_ALL=C tr -d '\r'
    exit ;;
esac
ssh -o BatchMode=yes "$HOST" "if (-not ($running)) { 'watcher NOT running (dialogs.sh --start)' }
\$o = Get-Content C:\\retrace-vm\\out\\dialogs-open.txt -EA SilentlyContinue
if (\$o) { 'open now:'; \$o | % { '  ' + \$_ } } else { 'open now: none' }
'log:'; Get-Content C:\\retrace-vm\\out\\dialogs.log -Tail ${1:-10} -EA SilentlyContinue | % { '  ' + \$_ }" | LC_ALL=C tr -d '\r'
