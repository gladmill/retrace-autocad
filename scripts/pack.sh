#!/usr/bin/env bash
# Builds the Retrace plugin (Release) and copies the autoloader bundle that the build lays out (bundle/Retrace.Bundle.targets) to
# <out-dir>/Retrace.bundle:
#   PackageContents.xml                    written by the build from bundle/PackageContents.template.xml (version, AutoCAD release)
#   Contents/Retrace.dll (+ Retrace.pdb)   one dll: Markdig, WebView2 and the native WebView2 loader are embedded in it (AutoCAD supplies OpenXml)
#   Contents/trust-retrace.ps1             adds the bundle folder to AutoCAD's Trusted Locations; run it once where the bundle is installed
#   Contents/LICENSE, THIRD-PARTY-NOTICES.txt  Retrace's licence and those of the embedded dependencies
# Never installs anything (RetraceDeploy=false, also from Git Bash on Windows). A full rebuild with ContinuousIntegrationBuild=true keeps
# local folder names out of the dll and pdb (source paths become /_/...); Source Link and the commit hash in the version are off, so
# neither names the repo it was built from. Fails if the dll or pdb still holds this folder, the home folder or the checkout's git remote.
# Fails if Contents holds any other .dll or .exe, or if an embedded dependency is missing from Retrace.dll.
# The samples plugin (Retrace.Samples) is not part of the bundle.
# Usage: pack.sh <out-dir>
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
out="${1:?usage: pack.sh <out-dir>}"
bundle="$out/Retrace.bundle"
proj="$here/src/Retrace/Retrace.csproj"
bin="$here/src/Retrace/bin/Release/net8.0-windows"

dotnet build "$proj" -c Release --nologo -v q --no-incremental -p:RetraceDeploy=false -p:ContinuousIntegrationBuild=true \
  -p:EnableSourceLink=false -p:IncludeSourceRevisionInInformationalVersion=false

# the checkout's own remote, e.g. github.com/owner/repo (embedded third-party dlls carry their own repository URLs, which are fine)
remote="$(git -C "$here" remote get-url origin 2>/dev/null | sed -E 's#^(https?://|git@)##; s#:#/#; s#\.git$##' || true)"
for f in "$bin/Retrace.dll" "$bin/Retrace.pdb"; do
  [ -f "$f" ] || continue
  for s in "$here" "$HOME" ${remote:+"$remote"}; do
    if LC_ALL=C grep -qF "$s" "$f"; then echo "pack.sh: $(basename "$f") contains \"$s\"" >&2; exit 1; fi
  done
done

# The embedded resource names (Retrace.csproj, target EmbedDependencies; Embedded/Dependencies.cs loads them) are in the dll's metadata (UTF-8);
# the private loader name (Dependencies.LoaderFile) is a string literal (UTF-16). Autodesk's licensing agent locked a plain WebView2Loader.dll
# that AutoCAD made visible to it, so the extracted copy must keep the private name.
python3 -I - "$bin/Retrace.dll" <<'PY'
import sys
data = open(sys.argv[1], "rb").read()
want = [("Retrace.Deps.%s.dll" % n).encode() for n in (
    "Markdig", "Microsoft.Web.WebView2.Core", "Microsoft.Web.WebView2.Wpf")] + [b"Retrace.Deps.native.WebView2Loader.dll"]
missing = [w.decode() for w in want if w not in data]
if "Retrace.WebView2Loader.dll".encode("utf-16-le") not in data: missing.append("Dependencies.LoaderFile = Retrace.WebView2Loader.dll")
if missing:
    sys.exit("pack.sh: Retrace.dll is missing: " + ", ".join(missing))
PY

rm -rf "$bundle"
mkdir -p "$out"
cp -R "$here/src/Retrace/bin/Release/Retrace.bundle" "$bundle"

odd="$(find "$bundle" -type f \( -iname '*.dll' -o -iname '*.exe' \) ! -path "$bundle/Contents/Retrace.dll" -print)"
[ -z "$odd" ] || { echo "pack.sh: Contents must hold Retrace.dll as its only dll/exe, also found:" >&2; echo "$odd" >&2; exit 1; }

echo "bundle: $bundle"
(cd "$bundle" && find . -type f | sort | sed 's|^\./||' | while read -r f; do printf '  %-32s %s bytes\n' "$f" "$(wc -c <"$f" | tr -d ' ')"; done)
echo "embedded in Retrace.dll:"; python3 -I -c 'import re,sys; print("\n".join("  "+m.decode() for m in sorted(set(re.findall(rb"Retrace\.Deps\.[A-Za-z0-9_.]+\.dll", open(sys.argv[1],"rb").read())))))' "$bin/Retrace.dll"
echo "install (Windows): copy the Retrace.bundle folder into %APPDATA%\\Autodesk\\ApplicationPlugins\\, then, with AutoCAD 2026 closed, run Contents\\trust-retrace.ps1 -Folder <that folder> (README, Install)."
