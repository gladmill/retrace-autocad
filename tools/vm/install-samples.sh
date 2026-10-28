#!/usr/bin/env bash
# Runs on your Mac or Linux box: builds the test add-ins (samples/Retrace.Samples, samples/Retrace.PartsFinder) and installs them on the VM
# as their own bundle, %APPDATA%\Autodesk\ApplicationPlugins\Retrace.Samples.bundle, which AutoCAD loads at every start (so their ribbon
# tabs "Retrace Samples" and "Parts" are always there). A separate bundle because a Retrace build or install rewrites Retrace.bundle.
# AutoCAD must be closed (it locks loaded dlls). Until the folder is trusted AutoCAD asks at each start whether to load the unsigned dlls:
# the script prints the trust command (trust-retrace.ps1 of the installed Retrace bundle); a person runs it on the VM with AutoCAD closed,
# since it changes AutoCAD's security settings. Host: RETRACE_VM_HOST. Remove with: install-samples.sh --remove
set -euo pipefail
HOST="${RETRACE_VM_HOST:-retrace-vm}"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PLUGINS='$env:APPDATA\Autodesk\ApplicationPlugins'

if [[ "${1:-}" == "--remove" ]]; then
  ssh -o BatchMode=yes "$HOST" "Remove-Item -Recurse -Force \"$PLUGINS\\Retrace.Samples.bundle\" -EA SilentlyContinue; 'removed'"
  echo "If you trusted it, also remove it from TRUSTEDPATHS on the VM (AutoCAD closed):"
  echo "  powershell -NoProfile -ExecutionPolicy Bypass -File \"%APPDATA%\\Autodesk\\ApplicationPlugins\\Retrace.bundle\\Contents\\trust-retrace.ps1\" -Folder \"%APPDATA%\\Autodesk\\ApplicationPlugins\\Retrace.Samples.bundle\" -Remove"
  exit 0
fi

out="$(mktemp -d)"; b="$out/Retrace.Samples.bundle"; mkdir -p "$b/Contents"
for p in Retrace.Samples Retrace.PartsFinder; do
  dotnet build "$ROOT/samples/$p" -c Release -o "$out/build/$p" -nologo -v q >/dev/null
  cp "$out/build/$p/$p.dll" "$b/Contents/"
done
entry() { printf '    <ComponentEntry AppName="%s" Version="0.1.0" ModuleName="./Contents/%s.dll" AppDescription="Retrace test add-in" LoadOnAutoCADStartup="True" LoadOnCommandInvocation="False" />\n' "$1" "$1"; }
cat > "$b/PackageContents.xml" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<ApplicationPackage SchemaVersion="1.0" ProductType="Application" Name="Retrace test add-ins" AppVersion="0.1.0"
                    Description="Retrace.Samples and Retrace.PartsFinder, test add-ins for Retrace. Not a product."
                    Author="Retrace contributors" ProductCode="{6E1C2B57-3F0A-4C1D-9B8E-2A7D5C4F1E90}" UpgradeCode="{0B8F4D21-7C3E-4A5B-8E6F-1D2C3B4A5F60}">
  <CompanyDetails Name="Retrace contributors" />
  <Components>
    <RuntimeRequirements OS="Win64" Platform="AutoCAD*" SeriesMin="R25.1" SeriesMax="R25.1" />
$(entry Retrace.Samples)
$(entry Retrace.PartsFinder)
  </Components>
</ApplicationPackage>
EOF

# AutoCAD keeps a loaded dll locked: replacing the bundle needs AutoCAD closed
ssh -o BatchMode=yes "$HOST" "if (Get-Process acad -EA SilentlyContinue) { 'AutoCAD is running: close it, then run this again.'; exit 2 }
Remove-Item -Recurse -Force \"$PLUGINS\\Retrace.Samples.bundle\" -EA SilentlyContinue
# copies NETLOADed from the Retrace bundle by hand earlier: one place only
Remove-Item \"$PLUGINS\\Retrace.bundle\\Contents\\Retrace.Samples.dll\", \"$PLUGINS\\Retrace.bundle\\Contents\\Retrace.PartsFinder.dll\" -EA SilentlyContinue
New-Item -ItemType Directory -Force C:\\retrace-vm\\upload | Out-Null; Remove-Item -Recurse -Force C:\\retrace-vm\\upload\\* -EA SilentlyContinue"
scp -q -r "$b" "$HOST:C:/retrace-vm/upload/"
ssh -o BatchMode=yes "$HOST" "Move-Item C:\\retrace-vm\\upload\\Retrace.Samples.bundle \"$PLUGINS\\\"
Get-ChildItem \"$PLUGINS\\Retrace.Samples.bundle\\Contents\" | % { \"installed \$(\$_.Name)\" }"
rm -rf "$out"
echo "To load them without the unsigned-dll prompt, trust the folder once on the VM (in a terminal there, AutoCAD closed):"
echo "  powershell -NoProfile -ExecutionPolicy Bypass -File \"%APPDATA%\\Autodesk\\ApplicationPlugins\\Retrace.bundle\\Contents\\trust-retrace.ps1\" -Folder \"%APPDATA%\\Autodesk\\ApplicationPlugins\\Retrace.Samples.bundle\""
