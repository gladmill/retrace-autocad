#!/usr/bin/env bash
# Runs on your Mac or Linux box: marks.sh <test folder name> prints the checkpoints of a recording on the VM (C:\temp\retrace\tests\<name>):
# event number, name, and what the checkpoint shortcut found at the cursor. Needs python3. Host: RETRACE_VM_HOST.
set -euo pipefail
HOST="${RETRACE_VM_HOST:-retrace-vm}"
ssh -o BatchMode=yes "$HOST" "Get-Content C:/temp/retrace/tests/${1:?test folder name}/recording.jsonl" | python3 -c '
import sys, json
for l in sys.stdin:
    try: e = json.loads(l)
    except Exception: continue
    if e.get("type") == "mark": print(e["n"], e.get("note"), "|", e.get("near") or "")
'
