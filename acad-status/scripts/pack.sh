#!/usr/bin/env bash
# Builds the AcadStatus plugin (Release) and copies the autoloader bundle that the build lays out (bundle/AcadStatus.Bundle.targets) to
# <out-dir>/AcadStatus.bundle:
#   PackageContents.xml                       written by the build from bundle/PackageContents.template.xml (version, AutoCAD release)
#   Contents/AcadStatus.dll (+ AcadStatus.pdb) one dll: nothing is embedded, the plugin uses only the framework and AutoCAD's own assemblies
#   Contents/trust-acadstatus.ps1             adds the bundle folder to AutoCAD's Trusted Locations; run it once where the bundle is installed
#   Contents/LICENSE                          the MIT licence
# Never installs anything (AcadStatusDeploy=false, also from Git Bash on Windows). A full rebuild with ContinuousIntegrationBuild=true keeps
# local folder names out of the dll and pdb (source paths become /_/...); Source Link and the commit hash in the version are off, so
# neither names the repo it was built from. Fails if the dll or pdb still holds this folder, the home folder or the checkout's git remote.
# Fails if Contents holds any other .dll or .exe (an Autodesk dll must never be copied), or if AcadStatus.dll lacks its commands.
# Usage: pack.sh <out-dir>
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
out="${1:?usage: pack.sh <out-dir>}"
bundle="$out/AcadStatus.bundle"
proj="$here/src/AcadStatus/AcadStatus.csproj"
bin="$here/src/AcadStatus/bin/Release/net8.0-windows"

dotnet build "$proj" -c Release --nologo -v q --no-incremental -p:AcadStatusDeploy=false -p:ContinuousIntegrationBuild=true \
  -p:EnableSourceLink=false -p:IncludeSourceRevisionInInformationalVersion=false

# the checkout's own remote, e.g. github.com/owner/repo
remote="$(git -C "$here" remote get-url origin 2>/dev/null | sed -E 's#^(https?://|git@)##; s#:#/#; s#\.git$##' || true)"
for f in "$bin/AcadStatus.dll" "$bin/AcadStatus.pdb"; do
  [ -f "$f" ] || continue
  for s in "$here" "$HOME" ${remote:+"$remote"}; do
    if LC_ALL=C grep -qF "$s" "$f"; then echo "pack.sh: $(basename "$f") contains \"$s\"" >&2; exit 1; fi
  done
done

# the command names are string literals in the dll's metadata (UTF-8, in the #US heap as UTF-16)
python3 -I - "$bin/AcadStatus.dll" <<'PY'
import sys
data = open(sys.argv[1], "rb").read()
missing = [n for n in ("ACADSTATUS", "ACADSTATUSPANE") if n.encode("utf-16-le") not in data and n.encode() not in data]
if missing:
    sys.exit("pack.sh: AcadStatus.dll is missing the command(s): " + ", ".join(missing))
PY

rm -rf "$bundle"
mkdir -p "$out"
cp -R "$here/src/AcadStatus/bin/Release/AcadStatus.bundle" "$bundle"

odd="$(find "$bundle" -type f \( -iname '*.dll' -o -iname '*.exe' \) ! -path "$bundle/Contents/AcadStatus.dll" -print)"
[ -z "$odd" ] || { echo "pack.sh: Contents must hold AcadStatus.dll as its only dll/exe, also found:" >&2; echo "$odd" >&2; exit 1; }

echo "bundle: $bundle"
(cd "$bundle" && find . -type f | sort | sed 's|^\./||' | while read -r f; do printf '  %-32s %s bytes\n' "$f" "$(wc -c <"$f" | tr -d ' ')"; done)
echo "install (Windows): copy the AcadStatus.bundle folder into %APPDATA%\\Autodesk\\ApplicationPlugins\\, then, with AutoCAD 2026 closed, run Contents\\trust-acadstatus.ps1 -Folder <that folder> (README, Install)."
