#!/usr/bin/env bash
# Replays a recorded Retrace test unattended in AutoCAD on the Windows VM and reports the verdict.
#   run-test.sh <test folder or recording.jsonl> [--new-drawing] [--run] [--timeout <seconds>]
# Steps: copy the recording to C:\temp\retrace\tests\<test folder>\ on the VM, optionally open a new drawing, run RETRACEREPLAY <path> Auto
# (with --run: RETRACERUN <path>, which opens its own new drawing and waits for a person at dialogs unless Retrace fills them),
# poll for results\latest.json, copy it back to <test folder>/results/, print the verdict and the checkpoints.
# The VM also keeps results\<yyyy-MM-dd_HH-mm-ss>_<VERDICT>.json. Needs the helpers from tools/vm in C:\retrace-vm\ on the VM (see README.md)
# and the Retrace bundle loaded in AutoCAD.
# Exit: 0 PASS, 1 FAIL / INCOMPLETE / ERROR, 2 usage or infrastructure problem (ssh, copy, timeout, bad result file).
set -uo pipefail

HOST="${RETRACE_VM_HOST:-retrace-vm}"
here="$(cd "$(dirname "$0")" && pwd)"
usage() { echo "usage: $(basename "$0") <test folder or recording.jsonl> [--new-drawing] [--run] [--timeout <seconds>]" >&2; exit 2; }

session=""; newdrawing=0; usetest=0; timeout=300
while [ $# -gt 0 ]; do
  case "$1" in
    --new-drawing) newdrawing=1 ;;
    --run) usetest=1 ;;
    --timeout) shift; [ $# -gt 0 ] && [[ "$1" =~ ^[0-9]+$ ]] || usage; timeout="$1" ;;
    -h|--help) usage ;;
    -*) echo "unknown option: $1" >&2; usage ;;
    *) [ -z "$session" ] || usage; session="$1" ;;
  esac
  shift
done
[ -n "$session" ] || usage
[ -d "$session" ] && session="$session/recording.jsonl"
[ -f "$session" ] || { echo "no such file: $session" >&2; exit 2; }
[ "$(basename "$session")" = "recording.jsonl" ] || { echo "not a recording (expected recording.jsonl in a test folder): $session" >&2; exit 2; }

# the VM gets its own folder per test (named like the local one, with characters the command line dislikes replaced)
# so that results\latest.json of one test is never mistaken for another's
folder="$(basename "$(cd "$(dirname "$session")" && pwd)")"
folder="$(printf '%s' "$folder" | tr -c 'A-Za-z0-9._-' '_')"
remote_dir="C:\\temp\\retrace\\tests\\$folder"
remote_result="$remote_dir\\results\\latest.json"
cmd_path="C:/temp/retrace/tests/$folder/recording.jsonl"   # forward slashes: acad-cmd.ps1 turns a backslash-n in the command text into Enter
local_result="$(dirname "$session")/results/latest.json"
result_name="results/latest.json"

r() { ssh -o BatchMode=yes -o ConnectTimeout=10 "$HOST" "$@"; }

echo "[1/5] checking the VM ($HOST)"
if ! out="$(r "if ((Test-Path C:\\retrace-vm\\run-in-session.ps1) -and (Test-Path C:\\retrace-vm\\acad-cmd.ps1)) { 'ok' } else { 'missing-helpers' }" 2>&1)"; then
  echo "cannot reach $HOST over ssh: $out" >&2; exit 2
fi
if [ "$(echo "$out" | tr -d '\r\n')" != "ok" ]; then
  echo "helpers missing on the VM: scp $here/*.ps1 $HOST:C:/retrace-vm/" >&2; exit 2
fi

echo "[2/5] copying the recording"
# (PowerShell over ssh exits 1 when its last command "failed", even with -ErrorAction SilentlyContinue: end with exit 0)
r "New-Item -ItemType Directory -Force '$remote_dir' | Out-Null; Remove-Item '$remote_result' -ErrorAction SilentlyContinue; exit 0" >/dev/null 2>&1 || { echo "cannot prepare $remote_dir on the VM" >&2; exit 2; }
mkdir -p "$(dirname "$local_result")"
rm -f "$local_result"
scp -q "$session" "$HOST:$cmd_path" || { echo "scp of the session failed" >&2; exit 2; }

if [ "$newdrawing" = 1 ]; then
  echo "[3/5] opening a new drawing"
  scp -q "$here/q-newdoc.ps1" "$HOST:C:/retrace-vm/q-newdoc.ps1" || { echo "scp of q-newdoc.ps1 failed" >&2; exit 2; }
  out="$(r "powershell -NoProfile -ExecutionPolicy Bypass -File C:\\retrace-vm\\run-in-session.ps1 -Query C:\\retrace-vm\\q-newdoc.ps1 -WaitSec 1" 2>&1)" || { echo "opening a new drawing failed: $out" >&2; exit 2; }
  echo "$out" | grep -E "new drawing|ERROR|TIMEOUT" | sed 's/^/      /'
  echo "$out" | grep -qE "ERROR|TIMEOUT" && { echo "opening a new drawing failed" >&2; exit 2; }
else
  echo "[3/5] using the active drawing"
fi

# acad-cmd.ps1: '|' separates commands, a backslash-n is Enter. Prompts: recording, (RETRACEREPLAY only) mode.
if [ "$usetest" = 1 ]; then
  echo "[4/5] running RETRACERUN (FILEDIA 0, so that it takes the typed path)"
  cmds="FILEDIA\\n0\\nRETRACERUN\\n$cmd_path\\n"
else
  echo "[4/5] running RETRACEREPLAY ... Auto"
  cmds="RETRACEREPLAY\\n$cmd_path\\nAuto\\n"
fi
out="$(r "powershell -NoProfile -ExecutionPolicy Bypass -File C:\\retrace-vm\\run-in-session.ps1 -Cmds \"$cmds\" -WaitSec 3" 2>&1)" || { echo "sending the command failed: $out" >&2; exit 2; }
echo "$out" | grep -E "ERROR|TIMEOUT" | sed 's/^/      /'
if echo "$out" | grep -qE "^ERROR|TIMEOUT"; then
  # A replay that stops at a modal prompt (AutoCAD's security prompt: the replay ends at once with what to do) has already written its
  # result, but the open prompt rejects the helper's later COM calls (RPC_E_CALL_REJECTED). Go on if the result is there.
  # Poll for up to 30 s: the replay may run a few steps before the one that brings the prompt up.
  sent=0
  for _ in $(seq 10); do
    sleep 3
    if [ "$(r "Test-Path '$remote_result'" 2>/dev/null | tr -d '\r\n')" = "True" ]; then sent=1; break; fi
  done
  if [ "$sent" != 1 ]; then
    echo "could not send the command to AutoCAD (is it running, and idle?)" >&2; exit 2
  fi
  echo "      (AutoCAD is at a modal prompt now, but the replay already wrote its result)"
fi

echo "[5/5] waiting for $result_name (timeout ${timeout}s)"
deadline=$(( $(date +%s) + timeout ))
found=0
while [ "$(date +%s)" -lt "$deadline" ]; do
  res="$(r "Test-Path '$remote_result'" 2>/dev/null | tr -d '\r\n')"
  if [ "$res" = "True" ]; then found=1; break; fi
  sleep 3
done
if [ "$found" != 1 ]; then
  echo "timeout: no result file after ${timeout}s. Is a dialog open in AutoCAD? (tools/vm/shot.ps1, esc.ps1)" >&2; exit 2
fi
scp -q "$HOST:${cmd_path%/*}/${result_name}" "$local_result" || { echo "copying the result back failed" >&2; exit 2; }
echo "result: $local_result"

python3 -I - "$local_result" <<'PY'
import json, sys
try:
    r = json.load(open(sys.argv[1], encoding="utf-8-sig"))
except Exception as e:
    print("unreadable result file:", e, file=sys.stderr); sys.exit(2)
cps = r.get("checkpoints", [])
print(f"verdict: {r.get('verdict')}  steps {r.get('stepsRun')}/{r.get('stepsTotal')}  core {r.get('coreVersion')}  start state matches: {r.get('startStateMatches', 'n/a')}")
for c in cps:
    p = c.get("pass")
    tag = "PASS" if p is True else "FAIL" if p is False else "NOT VERIFIED"
    note = c.get("note") or ""
    diffs = "; ".join(c.get("differences") or [])
    print(f"  CHECK {c.get('index')} {tag}" + (f" ({note})" if note else "") + (f": {diffs}" if diffs and p is not True else ""))
if r.get("message"):
    print("message:", r["message"])
sys.exit({"PASS": 0}.get(r.get("verdict"), 1))
PY
