#!/bin/bash
# Compile every .rc under a source root into a resource-only ARM64 DLL, and list the dialogs.
#
#   build-res.sh <source-root> <out-dir>
#
# Writes <out-dir>/<name>.dll, <out-dir>/dialogs.json and <out-dir>/build.log.
# The source root is data: it is only read. Per-run temp files (patched copies of each .rc, stub
# includes for project-local files that are missing) live in a temp dir that is removed at the end.
set -u

LLVM_RC=${LLVM_RC:-/opt/homebrew/opt/llvm/bin/llvm-rc}
LLD_LINK=${LLD_LINK:-/opt/homebrew/opt/lld/bin/lld-link}
HERE=$(cd "$(dirname "$0")" && pwd)
STUBS=$HERE/stubs

if [ $# -ne 2 ]; then echo "usage: $0 <source-root> <out-dir>" >&2; exit 2; fi
SRC=$(cd "$1" && pwd) || exit 2
mkdir -p "$2" && OUT=$(cd "$2" && pwd) || exit 2
for t in "$LLVM_RC" "$LLD_LINK"; do [ -x "$t" ] || { echo "missing tool: $t" >&2; exit 2; }; done
# llvm-rc finds clang (its preprocessor) next to itself or on PATH.
export PATH="$(dirname "$LLVM_RC"):$PATH"

TMP=$(mktemp -d "${TMPDIR:-/tmp}/dialog-capture-res.XXXXXX")
trap 'rm -rf "$TMP"' EXIT
LOG=$OUT/build.log
: > "$LOG"
log() { echo "$*" | tee -a "$LOG"; }

rm -f "$OUT"/*.dll "$OUT"/*.lib "$OUT"/dialogs.json
MANIFEST=$TMP/manifest.tsv   # rel-path <tab> dll-name
: > "$MANIFEST"
ok=0; failed=0
declare -a USED=()

while IFS= read -r rc; do
  rel=${rc#"$SRC"/}
  base=$(basename "$rc" .rc)
  name=$base; n=2
  while printf '%s\n' "${USED[@]:-}" | grep -qxF "$name"; do name=${base}_$n; n=$((n+1)); done
  USED+=("$name")

  work=$TMP/w/$rel                      # mirrors the source layout so ..\ includes resolve inside TMP
  mkdir -p "$(dirname "$work")"
  # Windows paths in #include lines (backslashes) -> slashes. Nothing else is touched.
  LC_ALL=C sed -E '/^[[:space:]]*#[[:space:]]*include/ s#\\#/#g' "$rc" > "$work"
  res=$TMP/$name.res
  stubbed=""
  built=0
  for attempt in $(seq 1 40); do
    err=$("$LLVM_RC" /FO "$res" /I "$(dirname "$rc")" /I "$STUBS" /D RC_INVOKED /D _WIN32 /D WIN32 /D UNICODE "$work" 2>&1)
    status=$?
    if [ $status -eq 0 ] && [ -s "$res" ]; then built=1; break; fi
    # project-local include that is absent: stub it in TMP (never in the input) and retry
    # a TYPELIB statement points at a .tlb that is a build product; an empty file satisfies llvm-rc
    tlb=$(printf '%s\n' "$err" | sed -n 's/.*file not found : \(.*\.[tT][lL][bB]\).*/\1/p' | head -1)
    if [ -n "$tlb" ]; then : > "$(dirname "$work")/$tlb"; stubbed="$stubbed $tlb"; continue; fi
    missing=$(printf '%s\n' "$err" | sed -n "s/.*fatal error: '\(.*\)' file not found.*/\1/p" | head -1)
    [ -n "$missing" ] || break
    target=$(dirname "$work")/$missing
    mkdir -p "$(dirname "$target")"
    if [ -f "$STUBS/$(basename "$missing")" ]; then cp "$STUBS/$(basename "$missing")" "$target"
    else : > "$target"; fi
    stubbed="$stubbed $missing"
  done
  if [ $built -eq 1 ] && "$LLD_LINK" /dll /noentry /machine:arm64 "/out:$OUT/$name.dll" "$res" >"$TMP/link.out" 2>&1; then
    ok=$((ok+1))
    log "OK    $rel -> $name.dll${stubbed:+  (stubbed missing includes:$stubbed)}"
    printf '%s\t%s\n' "$rel" "$name" >> "$MANIFEST"
  else
    failed=$((failed+1))
    reason=$( [ $built -eq 1 ] && head -3 "$TMP/link.out" || printf '%s\n' "$err" | grep -m3 -E 'error|Error' )
    log "FAIL  $rel: $(printf '%s' "$reason" | tr '\n' ' ')${stubbed:+  (stubbed:$stubbed)}"
  fi
done < <(find "$SRC" -type f -iname '*.rc' | sort)

rm -f "$OUT"/*.lib
python3 -I "$HERE/dialogs.py" "$SRC" "$MANIFEST" "$OUT/dialogs.json" 2>&1 | tee -a "$LOG"
log "done: $ok compiled, $failed failed -> $OUT"
[ $ok -gt 0 ]
