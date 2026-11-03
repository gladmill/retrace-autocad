#!/usr/bin/env bash
# Regression test: a replay started over COM (SendCommand, as run-test.sh does) must run its typed commands.
#   com-replay-test.sh
# Builds a small recording (typed CIRCLE, LTSCALE, -LAYER Make), opens a new drawing, replays it with `RETRACEREPLAY <path> Auto` over COM
# (run-test.sh), then reads the drawing back over COM and checks that it changed: one entity in model space, LTSCALE 7.25, layer RTCOM
# exists. A replay that reports `ok` without running the commands leaves the drawing as it was and fails here.
# Needs the helpers from tools/vm in C:\retrace-vm\ on the VM and the Retrace bundle loaded in AutoCAD. Host: RETRACE_VM_HOST.
# Exit: 0 PASS, 1 the drawing did not change as expected, 2 infrastructure problem.
set -uo pipefail
HOST="${RETRACE_VM_HOST:-retrace-vm}"
here="$(cd "$(dirname "$0")" && pwd)"
work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
mkdir -p "$work/com-replay"
cat > "$work/com-replay/recording.jsonl" <<'JSONL'
{"type":"header","schema":1,"name":"com-replay","autocad":"25.1.0.0","product":"AutoCAD 2026","locale":"en","workspace":"Drafting","started":"2026-11-02T10:00:00"}
{"t":100,"type":"doc_active","doc":"Drawing1.dwg"}
{"t":1000,"type":"cmd_start","command":"CIRCLE","origin":"typed/other"}
{"t":1100,"type":"prompt","kind":"point","message":"Specify center point:","status":"OK","value":[10,10,0]}
{"t":1200,"type":"prompt","kind":"distance","message":"Specify radius:","status":"OK","value":4}
{"t":1210,"type":"cmd_end","command":"CIRCLE"}
{"t":2000,"type":"cmd_start","command":"LTSCALE","origin":"typed/other"}
{"t":2100,"type":"prompt","kind":"double","message":"Enter new linetype scale factor <1.0000>:","status":"OK","value":7.25}
{"t":2110,"type":"cmd_end","command":"LTSCALE"}
{"t":3000,"type":"cmd_start","command":"-LAYER","origin":"typed/other"}
{"t":3100,"type":"prompt","kind":"keyword","message":"Enter an option [?/Make/Set/New/ON/OFF/Color/Ltype/LWeight/TRansparency/MATerial/Plot/Freeze/Thaw/LOck/Unlock/stAte/Description/rEconcile]:","status":"Keyword","keyword":"Make"}
{"t":3200,"type":"prompt","kind":"string","message":"Enter name for new layer (becomes the current layer) <0>:","status":"OK","value":"RTCOM"}
{"t":3300,"type":"prompt","kind":"keyword","message":"Enter an option [?/Make/Set/New/ON/OFF/Color/Ltype/LWeight/TRansparency/MATerial/Plot/Freeze/Thaw/LOck/Unlock/stAte/Description/rEconcile]:","status":"None"}
{"t":3310,"type":"cmd_end","command":"-LAYER"}
{"t":4000,"type":"session_end"}
JSONL
cat > "$work/q-com-replay.ps1" <<'PS'
"COUNT $($doc.ModelSpace.Count)"
"LTSCALE $($doc.GetVariable('LTSCALE'))"
"LAYER $(@($doc.Layers | % { $_.Name }) -contains 'RTCOM')"
PS

# a new drawing, so that the replay starts from a known state (run-test.sh --new-drawing)
"$here/run-test.sh" "$work/com-replay" --new-drawing --timeout 120
rc=$?
[ "$rc" = 2 ] && { echo "infrastructure problem (run-test.sh exit 2)" >&2; exit 2; }

scp -q "$work/q-com-replay.ps1" "$HOST:C:/retrace-vm/q-com-replay.ps1" || { echo "scp failed" >&2; exit 2; }
after="$(ssh -o BatchMode=yes -o ConnectTimeout=10 "$HOST" "powershell -NoProfile -ExecutionPolicy Bypass -File C:\\retrace-vm\\run-in-session.ps1 -Query C:\\retrace-vm\\q-com-replay.ps1 -WaitSec 1" 2>&1 | grep -E '^(COUNT|LTSCALE|LAYER) ' | tr -d '\r')"
echo "$after"
n="$(echo "$after" | sed -n 's/^COUNT //p')"
lt="$(echo "$after" | sed -n 's/^LTSCALE //p')"
ly="$(echo "$after" | sed -n 's/^LAYER //p')"
[ -n "$n" ] || { echo "could not read the drawing back" >&2; exit 2; }
fail=0
[ "$n" = 1 ] || { echo "FAIL: model space has $n entities, expected 1 (the typed CIRCLE did not run)"; fail=1; }
[ "$lt" = 7.25 ] || { echo "FAIL: LTSCALE is $lt, expected 7.25 (the typed LTSCALE did not run)"; fail=1; }
[ "$ly" = True ] || { echo "FAIL: layer RTCOM does not exist (the typed -LAYER did not run)"; fail=1; }
[ "$fail" = 0 ] && echo "PASS: the COM-started replay changed the drawing"
exit $fail
