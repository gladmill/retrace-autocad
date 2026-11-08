# Builds Retrace.MfcSample.dll (x64, shared MFC, Unicode) with the Visual Studio 2022 Build Tools (C++ with ATL/MFC and a Windows SDK).
# Run it on the Windows machine that runs AutoCAD (an ARM64 VM too: it uses the arm64_amd64 cross tools, whose x64 output AutoCAD loads).
#   powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 [-Out C:\retrace-vm\mfcsample-out] [-VsRoot <Build Tools folder>]
# The dll is not in git (*.dll is ignored). It is a regular MFC DLL: the machine needs the Visual C++ runtime of the build (the Build Tools install it).
param(
    [string]$Out = (Join-Path $PSScriptRoot 'bin'),
    [string]$VsRoot = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools'
)
$ErrorActionPreference = 'Stop'
$vcvars = Join-Path $VsRoot 'VC\Auxiliary\Build\vcvarsall.bat'
if (-not (Test-Path $vcvars)) { throw "vcvarsall.bat not found under $VsRoot (install the Build Tools with C++ and ATL/MFC)" }
$arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64_amd64' } else { 'amd64' }
New-Item -ItemType Directory -Force $Out | Out-Null
$obj = Join-Path $Out 'obj'
New-Item -ItemType Directory -Force $obj | Out-Null
$src = $PSScriptRoot
$bat = Join-Path $Out 'build.cmd'
@"
@echo off
call "$vcvars" $arch >nul || exit /b 1
cd /d "$src"
rc /nologo /fo "$obj\MfcSample.res" MfcSample.rc || exit /b 2
cl /nologo /W3 /EHsc /MD /LD /D_AFXDLL /D_UNICODE /DUNICODE /DWIN32 /D_WINDOWS /D_USRDLL /D_WINDLL /Fo"$obj\\" /Fe"$Out\Retrace.MfcSample.dll" MfcSample.cpp "$obj\MfcSample.res" /link /MACHINE:X64 /SUBSYSTEM:WINDOWS || exit /b 3
"@ | Set-Content -Encoding ASCII $bat
& cmd.exe /c $bat
if ($LASTEXITCODE -ne 0) { throw "build failed (step exit code $LASTEXITCODE)" }
Write-Host "built $(Join-Path $Out 'Retrace.MfcSample.dll')"
