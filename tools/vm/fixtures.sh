#!/usr/bin/env bash
# Builds, lists, mutates and dumps the Retrace test fixtures on the Windows VM (AutoCAD 2026 must be running there).
#   fixtures.sh build [name]       build one fixture (all, layers, ...), no name = every fixture to C:\temp\retrace\fixtures\<name>.dwg
#                                  (`picks` is built only when named: five lines for the BREAK / TRIM / FILLET pick tests)
#   fixtures.sh list               fixtures on the VM, and the mutations in this folder
#   fixtures.sh open <name>        open fixtures\<name>.dwg (an open copy is closed without saving first), then visit every layout once and return to Model
#   fixtures.sh dump               q-fixture-dump.ps1 on the active drawing
#   fixtures.sh mutate <name>      run mut-<name>.ps1 on the active drawing (name = "layers-freeze-dim", without mut-)
#   fixtures.sh check [name|all]   per mutation: reopen fx-all, dump, mutate, dump, print the diff (what a detector should see)
#   fixtures.sh shot <layout> [png]  show a layout of the active drawing and copy the screenshot back (keep it out of git)
#   fixtures.sh upload             copy the scripts to C:\retrace-vm\ only
# The ssh host comes from RETRACE_VM_HOST (default retrace-vm). See README.md, section "Fixtures".
set -uo pipefail
HOST="${RETRACE_VM_HOST:-retrace-vm}"
here="$(cd "$(dirname "$0")" && pwd)"
NAMES="layers layouts viewports xdata extdict symbols sysvars views attrs"
r() { ssh -o BatchMode=yes -o ConnectTimeout=10 "$HOST" "$@"; }
upload() {
  r 'New-Item -ItemType Directory -Force C:\temp\retrace\fixtures, C:\retrace-vm | Out-Null; exit 0' >/dev/null || { echo "cannot reach $HOST" >&2; exit 2; }
  scp -q "$here"/fx-*.ps1 "$here"/mut-*.ps1 "$here"/noise-*.ps1 "$here"/q-fixture-dump.ps1 "$here"/q-fx-open.ps1 "$here"/q-fx-layout.ps1 "$here"/q-fx-picks.ps1 \
      "$here"/acad-cmd.ps1 "$here"/run-in-session.ps1 "$here"/run-hidden.vbs "$HOST:C:/retrace-vm/" || { echo "scp failed" >&2; exit 2; }
}
# run a query file in the interactive session; $2 = timeout seconds
q() {
  r "powershell -NoProfile -ExecutionPolicy Bypass -File C:\\retrace-vm\\run-in-session.ps1 -WaitSec 1 -TimeoutSec ${2:-120} -Query C:\\retrace-vm\\$1" 2>&1 \
    | tr -d '\r' | grep -v '^WARNING: Task may not run'
}
# the query's own output only: drop the "acad:" header and everything from the log tail on
body() { awk '/^logpath: /{exit} {print}' | sed -e '/^acad: /d' -e '/^ERROR: The object invoked/d' | cat -s; }
argset() { r "Set-Content C:\\retrace-vm\\fx-arg.txt '$1' -Encoding ASCII; exit 0" >/dev/null; }
mutfiles() { ls "$here" | grep -E '^(mut|noise)-.*\.ps1$' | sed 's/\.ps1$//'; }

cmd="${1:-}"; shift || true
case "$cmd" in
  build)
    upload; list="${1:-$NAMES all}"
    for n in $list; do
      echo "== build $n"; argset "$n"
      if [ "$n" = picks ]; then q q-fx-picks.ps1 180 | body; else q fx-build.ps1 600 | body; fi
    done ;;
  list)
    r 'Get-ChildItem C:\temp\retrace\fixtures -Filter *.dwg | % { "{0}  {1} bytes  {2}" -f $_.Name, $_.Length, $_.LastWriteTime }; exit 0' | tr -d '\r'
    echo "mutations:"; mutfiles | sed 's/^/  /' ;;
  open)
    [ $# -eq 1 ] || { echo "usage: fixtures.sh open <name>" >&2; exit 2; }
    upload; argset "$1"; q q-fx-open.ps1 | body ;;
  dump)
    upload; q q-fixture-dump.ps1 | body ;;
  mutate)
    [ $# -eq 1 ] || { echo "usage: fixtures.sh mutate <name>" >&2; exit 2; }
    f="$1"; [ -f "$here/$f.ps1" ] || f="mut-$1"; [ -f "$here/$f.ps1" ] || { echo "no such mutation: $1" >&2; exit 2; }
    upload; q "$f.ps1" | body ;;
  check)
    upload; t="${1:-all}"
    list="$t"; [ "$t" = all ] && list="$(mutfiles | tr '\n' ' ')"
    tmp="${TMPDIR:-/tmp}"
    for m in $list; do
      f="$m"; [ -f "$here/$f.ps1" ] || f="mut-$m"; [ -f "$here/$f.ps1" ] || { echo "no such mutation: $m" >&2; exit 2; }
      argset fx-all; q q-fx-open.ps1 | body >/dev/null
      q q-fixture-dump.ps1 | body > "$tmp/fx-before.txt"
      echo "== $f"; q "$f.ps1" | body
      q q-fixture-dump.ps1 | body > "$tmp/fx-after.txt"
      out="$(diff "$tmp/fx-before.txt" "$tmp/fx-after.txt" | grep '^[<>]' || true)"
      if [ -n "$out" ]; then echo "$out"; else echo "(no difference in the dump)"; fi
    done ;;
  shot)
    [ $# -ge 1 ] || { echo "usage: fixtures.sh shot <layout|Model> [out.png]" >&2; exit 2; }
    out="${2:-${TMPDIR:-/tmp}/fx-shot.png}"
    upload; argset "$1"; q q-fx-layout.ps1 | body
    scp -q "$HOST:C:/retrace-vm/out/screen.png" "$out" && echo "screenshot: $out" ;;
  upload) upload ;;
  *) sed -n '2,12p' "$0" | sed 's/^# \{0,1\}//'; exit 2 ;;
esac
