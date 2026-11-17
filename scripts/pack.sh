#!/usr/bin/env bash
# Builds the Retrace plugin (Release, both targets) and copies the autoloader bundle that the build lays out (bundle/Retrace.Bundle.targets) to
# <out-dir>/Retrace.bundle:
#   PackageContents.xml                    written by the build from bundle/PackageContents.template.xml (version; two component entries: R24.3 and R25.1)
#   Contents/Retrace.dll (+ Retrace.pdb)   AutoCAD 2026 (R25.1, net8.0-windows): one dll, Markdig, WebView2 and the native WebView2 loader are embedded in it
#                                          (AutoCAD supplies OpenXml)
#   Contents/2024/Retrace.dll (+ .pdb)     AutoCAD 2024 (R24.3, net48): the same embedded dependencies, and beside it the dlls that are not embedded:
#                                          System.Text.Json and what it pulls in, DocumentFormat.OpenXml (the list is checked below)
#   Contents/trust-retrace.ps1             adds the bundle folder to AutoCAD's Trusted Locations; run it once where the bundle is installed
#   Contents/LICENSE, THIRD-PARTY-NOTICES.txt  Retrace's licence and those of the embedded and shipped dependencies
# Never installs anything (RetraceDeploy=false, also from Git Bash on Windows). A full rebuild with ContinuousIntegrationBuild=true keeps
# local folder names out of the dlls and pdbs (source paths become /_/...); Source Link and the commit hash in the version are off, so
# neither names the repo it was built from. Fails if a Retrace dll or pdb still holds this folder, the home folder or the checkout's git remote
# (UTF-8 and UTF-16) or this account's user name.
# Fails if Contents holds any other .dll or .exe than the ones listed, if an embedded dependency is missing from either Retrace.dll, or if the
# manifest does not have exactly the two entries.
# The samples plugin (Retrace.Samples) is not part of the bundle.
# Usage: pack.sh <out-dir>
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
out="${1:?usage: pack.sh <out-dir>}"
bundle="$out/Retrace.bundle"
proj="$here/src/Retrace/Retrace.csproj"
bin="$here/src/Retrace/bin/Release/net8.0-windows"
bin48="$here/src/Retrace/bin/Release/net48"

dotnet build "$proj" -c Release --nologo -v q --no-incremental -p:RetraceDeploy=false -p:ContinuousIntegrationBuild=true \
  -p:EnableSourceLink=false -p:IncludeSourceRevisionInInformationalVersion=false

# the checkout's own remote, e.g. github.com/owner/repo (embedded third-party dlls carry their own repository URLs, which are fine)
remote="$(git -C "$here" remote get-url origin 2>/dev/null | sed -E 's#^(https?://|git@)##; s#:#/#; s#\.git$##' || true)"
for f in "$bin/Retrace.dll" "$bin/Retrace.pdb" "$bin48/Retrace.dll" "$bin48/Retrace.pdb"; do
  [ -f "$f" ] || { echo "pack.sh: $f is missing" >&2; exit 1; }
  for s in "$here" "$HOME" ${remote:+"$remote"}; do
    if LC_ALL=C grep -qF "$s" "$f"; then echo "pack.sh: $f contains \"$s\"" >&2; exit 1; fi
  done
done

# The same folder names as UTF-16 (the string literals of a dll), and this account's user name (read now, never written down), in the dlls and pdbs.
python3 -I - "$here" "$HOME" "$(id -un)" "$bin/Retrace.dll" "$bin/Retrace.pdb" "$bin48/Retrace.dll" "$bin48/Retrace.pdb" <<'PY'
import sys
here, home, user, files = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4:]
needles = [n for n in (here, home, user) if len(n) >= 3]
for f in files:
    data = open(f, "rb").read()
    for n in needles:
        if n.encode("utf-16-le") in data or n.encode() in data:
            sys.exit('pack.sh: %s contains "%s"' % (f, n))
PY

# The embedded resource names (Retrace.csproj, target EmbedDependencies; Embedded/Dependencies.cs loads them) are in the dll's metadata (UTF-8);
# the native loader's file name is a string literal (UTF-16). net8: Autodesk's licensing agent locked a plain WebView2Loader.dll that AutoCAD made
# visible to it, so the extracted copy keeps a private name. net48: WebView2's .NET Framework build loads the plain name from a folder it is told
# (the per-version cache, not a plugin folder), so there it is the plain name.
python3 -I - "$bin/Retrace.dll" "$bin48/Retrace.dll" <<'PY'
import sys
for path in sys.argv[1:]:
    data = open(path, "rb").read()
    want = [("Retrace.Deps.%s.dll" % n).encode() for n in (
        "Markdig", "Microsoft.Web.WebView2.Core", "Microsoft.Web.WebView2.Wpf")] + [b"Retrace.Deps.native.WebView2Loader.dll"]
    missing = [w.decode() for w in want if w not in data]
    loader = "WebView2Loader.dll" if "/net48/" in path else "Retrace.WebView2Loader.dll"
    if loader.encode("utf-16-le") not in data: missing.append("the loader file name " + loader)
    if missing:
        sys.exit("pack.sh: %s is missing: %s" % (path, ", ".join(missing)))
PY

rm -rf "$bundle"
mkdir -p "$out"
cp -R "$here/src/Retrace/bin/Release/Retrace.bundle" "$bundle"

# Contents holds the 2026 Retrace.dll alone; Contents/2024 holds the 2024 Retrace.dll and exactly the dependencies listed here (a new package must be
# added here on purpose, and to THIRD-PARTY-NOTICES.txt)
deps48="DocumentFormat.OpenXml Microsoft.Bcl.AsyncInterfaces Retrace System.Buffers System.Memory System.Numerics.Vectors System.Runtime.CompilerServices.Unsafe System.Text.Encodings.Web System.Text.Json System.Threading.Tasks.Extensions System.ValueTuple"
odd="$(find "$bundle" -type f \( -iname '*.dll' -o -iname '*.exe' \) ! -path "$bundle/Contents/Retrace.dll" ! -path "$bundle/Contents/2024/*" -print)"
[ -z "$odd" ] || { echo "pack.sh: Contents must hold Retrace.dll as its only dll/exe, also found:" >&2; echo "$odd" >&2; exit 1; }
[ -f "$bundle/Contents/Retrace.dll" ] || { echo "pack.sh: Contents/Retrace.dll is missing" >&2; exit 1; }
for f in "$bundle"/Contents/2024/*; do
  [ -e "$f" ] || continue
  case "$f" in *.dll|*.exe) ;; *.pdb) continue;; *) echo "pack.sh: Contents/2024 holds an unlisted file: $(basename "$f")" >&2; exit 1;; esac
  n="$(basename "$f")"; n="${n%.*}"
  case " $deps48 " in *" $n "*) ;; *) echo "pack.sh: Contents/2024 holds an unlisted file: $(basename "$f")" >&2; exit 1;; esac
done
for n in $deps48; do [ -f "$bundle/Contents/2024/$n.dll" ] || { echo "pack.sh: Contents/2024/$n.dll is missing" >&2; exit 1; }; done

python3 -I - "$bundle/PackageContents.xml" <<'PY'
import re, sys
text = re.sub(r"<!--.*?-->", "", open(sys.argv[1], encoding="utf-8").read(), flags=re.S)
# one Components block = one release and one module (no XML parser: not every python3 here has a working expat)
got = sorted((m.group(1), m.group(2), m.group(3)) for m in re.finditer(
    r'<Components\b[^>]*>\s*<RuntimeRequirements\b[^>]*SeriesMin="([^"]+)"[^>]*SeriesMax="([^"]+)"[^>]*/>\s*<ComponentEntry\b[^>]*ModuleName="([^"]+)"', text))
if text.count("<Components") != 2: sys.exit("pack.sh: PackageContents.xml must have exactly two Components blocks")
want = [("R24.3", "R24.3", "./Contents/2024/Retrace.dll"), ("R25.1", "R25.1", "./Contents/Retrace.dll")]
if got != want: sys.exit("pack.sh: PackageContents.xml entries are %r, expected %r" % (got, want))
PY

echo "bundle: $bundle"
(cd "$bundle" && find . -type f | sort | sed 's|^\./||' | while read -r f; do printf '  %-52s %s bytes\n' "$f" "$(wc -c <"$f" | tr -d ' ')"; done)
echo "embedded in each Retrace.dll:"; python3 -I -c 'import re,sys; print("\n".join("  "+m.decode() for m in sorted(set(re.findall(rb"Retrace\.Deps\.[A-Za-z0-9_.]+\.dll", open(sys.argv[1],"rb").read())))))' "$bin/Retrace.dll"
echo "install (Windows): copy the Retrace.bundle folder into %APPDATA%\\Autodesk\\ApplicationPlugins\\, then, with AutoCAD closed, run Contents\\trust-retrace.ps1 -Folder <that folder> (README, Install)."
