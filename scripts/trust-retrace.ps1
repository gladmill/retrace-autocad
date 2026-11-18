# Adds the Retrace bundle folder to AutoCAD's Trusted Locations (the TRUSTEDPATHS variable) in every profile of the AutoCAD releases the
# bundle loads in (-Series, default R24.3,R25.1 = AutoCAD 2024 and 2026; one or more, comma separated) of this Windows user, so AutoCAD loads Retrace without stopping at "Security - Unsigned
# Executable File" at start-up and after updates. "Always Load" in that prompt trusts only the one file, so every update would ask again;
# a trusted folder does not. It never changes SECURELOAD and touches no other setting.
# On Windows the Retrace build runs it after installing the bundle (bundle/Retrace.Bundle.targets); it also works by hand.
# AutoCAD must be closed: it writes its profiles back to the registry when it exits.
#   trust-retrace.ps1 -Folder <...\Retrace.bundle> [-Series R24.3,R25.1] [-Remove | -Check]
# -Check changes nothing and reports whether every profile of those releases already trusts the folder. A release that has no profile on this machine
# is skipped (AutoCAD 2024 is often not installed next to 2026); it is an error (3) only when none of the releases has one. -Remove and -Check do not go together.
# Exit codes: 0 done / nothing to change / (-Check) trusted everywhere, 1 error, 2 AutoCAD is running (all modes: AutoCAD writes its
# profiles back when it exits, so neither a change nor a check is reliable while it runs), 3 no profile of any of the releases found (start
# AutoCAD once first), 4 (-Check) a profile does not trust the folder.
param([Parameter(Mandatory)][string]$Folder, [string[]]$Series = @('R24.3', 'R25.1'), [switch]$Remove, [switch]$Check)
$ErrorActionPreference = 'Stop'
# the build passes the list as one argument (-Series R24.3,R25.1): split it, whichever way it arrived
$Series = @($Series | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })

if ($Remove -and $Check) { Write-Output '-Remove and -Check do not go together.'; exit 1 }
if (Get-Process acad -ErrorAction SilentlyContinue) { Write-Output 'AutoCAD is running: close it, then run this again.'; exit 2 }

# a relative -Folder would be resolved against whatever directory the caller happens to be in: make it absolute first
# (-Remove may run after the folder was deleted, e.g. by an uninstaller: it only needs the absolute spelling of the entry.)
if (Test-Path -LiteralPath $Folder.Trim() -PathType Container) { $folder = (Resolve-Path -LiteralPath $Folder.Trim()).ProviderPath.TrimEnd('\') }
elseif ($Remove) { $folder = [System.IO.Path]::GetFullPath($Folder.Trim()).TrimEnd('\') }
else { Write-Output "Folder not found: $Folder"; exit 1 }
$entry = "$folder\..."   # "\..." = this folder and its subfolders (Contents\2026, Contents\2024; the same entry whether the bundle has the old layout with Contents\Retrace.dll or the new one)
# Compare whole entries (case-insensitive, trailing '\' ignored), keeping '\...': a plain "...\Retrace.bundle" entry trusts only that
# folder, not Contents\ where the dlls are, so it does not count, and -Remove leaves such a hand-made entry alone.
function Norm([string]$p) { $p.Trim().TrimEnd('\') }
$want = Norm $entry
# the TRUSTEDPATHS entries of a profile (trimmed, empty ones dropped) and whether the folder is among them
function Entries($vars) { @(("$((Get-ItemProperty -Path $vars -Name TRUSTEDPATHS -ErrorAction SilentlyContinue).TRUSTEDPATHS)" -split ';') | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
function Has($parts) { @($parts | Where-Object { (Norm $_) -ieq $want }).Count -gt 0 }

# HKCU\Software\Autodesk\AutoCAD\<Series>\<product, e.g. ACAD-9101:409>\Profiles\<profile>\Variables: only the release the bundle loads in
# (the PackageContents.xml entries: one release each, SeriesMin = SeriesMax, which the build passes as -Series). Other releases never load it, so
# their profiles are not touched.
$profiles = @()
foreach ($rel in Get-ChildItem 'HKCU:\Software\Autodesk\AutoCAD' -ErrorAction SilentlyContinue | Where-Object { $Series -contains $_.PSChildName }) {
  foreach ($prod in Get-ChildItem $rel.PSPath -ErrorAction SilentlyContinue) {
    foreach ($prof in Get-ChildItem (Join-Path $prod.PSPath 'Profiles') -ErrorAction SilentlyContinue) {
      $profiles += [pscustomobject]@{ Name = "$($rel.PSChildName) $($prod.PSChildName) $($prof.PSChildName)"; Vars = (Join-Path $prof.PSPath 'Variables') }
    }
  }
}
if (-not $profiles) { Write-Output "No AutoCAD $($Series -join ' / ') profile found for this Windows user. Start AutoCAD once, close it, then run this again."; exit 3 }

if ($Check) {
  $missing = @($profiles | Where-Object { -not (Has (Entries $_.Vars)) })
  foreach ($p in $missing) { Write-Output "  $($p.Name): not trusted" }
  if ($missing) { exit 4 }
  exit 0
}

foreach ($p in $profiles) {
  if (-not (Test-Path $p.Vars)) { if ($Remove) { continue }; New-Item -Path $p.Vars -Force | Out-Null }
  $parts = Entries $p.Vars
  $has = Has $parts
  if ($Remove) {
    if (-not $has) { Write-Output "  $($p.Name): not listed"; continue }
    $new = @($parts | Where-Object { (Norm $_) -ine $want }) -join ';'
    Set-ItemProperty -Path $p.Vars -Name TRUSTEDPATHS -Value $new
    Write-Output "  $($p.Name): removed"
  } else {
    if ($has) { Write-Output "  $($p.Name): already trusted"; continue }
    $new = (@($parts) + $entry) -join ';'
    Set-ItemProperty -Path $p.Vars -Name TRUSTEDPATHS -Value $new
    Write-Output "  $($p.Name): trusted"
  }
}
exit 0
