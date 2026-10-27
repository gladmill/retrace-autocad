#!/usr/bin/env bash
# Runs on your Mac or Linux box: ui-act.sh "<action>" ["<action>" ...] writes the actions to C:\retrace-vm\act.txt, runs ui-act.ps1 in the
# VM's interactive session (one-shot scheduled task), waits for it and prints its log. Actions: see ui-act.ps1. Host: RETRACE_VM_HOST.
set -euo pipefail
HOST="${RETRACE_VM_HOST:-retrace-vm}"
tmp="$(mktemp)"; printf '%s\n' "$@" > "$tmp"
scp -q "$tmp" "$HOST:C:/retrace-vm/act.txt"; rm -f "$tmp"
ssh -o BatchMode=yes "$HOST" 'Remove-Item C:\retrace-vm\out\act-done.txt -EA SilentlyContinue
schtasks /create /tn RetraceUiAct /tr "wscript //B //Nologo C:\retrace-vm\run-hidden.vbs powershell -NoProfile -ExecutionPolicy Bypass -File C:\retrace-vm\ui-act.ps1" /sc once /st 00:00 /it /f /rl limited 2>$null | Out-Null
schtasks /run /tn RetraceUiAct | Out-Null
$end = (Get-Date).AddSeconds(180); while (-not (Test-Path C:\retrace-vm\out\act-done.txt) -and (Get-Date) -lt $end) { Start-Sleep -Milliseconds 300 }
schtasks /delete /tn RetraceUiAct /f | Out-Null
if (Test-Path C:\retrace-vm\out\act-done.txt) { Get-Content C:\retrace-vm\out\act.txt } else { "TIMEOUT"; exit 1 }'
