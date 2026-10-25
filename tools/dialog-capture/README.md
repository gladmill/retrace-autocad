# dialog-capture

Captures how real Windows draws the Win32 dialogs that Retrace's docs engine draws as SVG wireframes, so
the wireframes can be calibrated (sizes, fonts, control rectangles, title bar and border). It is a developer tool, not part of the plugin.
DCL dialogs are captured inside AutoCAD by the plugin command `RETRACECAPTUREDCL`; both write the same file shape.

Two parts:

1. `build-res.sh` (macOS): compiles every `.rc` under a source root into a resource-only ARM64 DLL and
   writes `dialogs.json` (which dialogs each DLL holds, with source line and Retrace form id).
2. `DialogCapture` (Windows 11 ARM64): loads those DLLs, creates every dialog for real, screenshots it
   exactly as DWM draws it and writes a JSON file with the geometry of the window and every control.

The source root (e.g. `~/src/my-plugin`) is data. It is only read, never copied into the repo.

## Build (on macOS)

```sh
cd tools/dialog-capture

# 1. .rc -> DLLs + dialogs.json + build.log
./build-res.sh ~/src/my-plugin /tmp/capture-build/res

# 2. the capture exe (single file, self-contained, win-arm64)
dotnet publish DialogCapture -c Release -r win-arm64 --self-contained -p:PublishSingleFile=true -o /tmp/capture-build/bin
```

Needs `llvm-rc` (`/opt/homebrew/opt/llvm/bin`), `lld-link` (`/opt/homebrew/opt/lld/bin`), `python3` and
the .NET 10 SDK. Override the tool paths with `LLVM_RC=` / `LLD_LINK=`.

`build-res.sh` preprocesses each `.rc` with clang (inside llvm-rc). `stubs/` stands in for the headers that
don't exist on macOS (`windows.h`, `winres.h`, `afxres.h` with Win32 style constants, `afxres.rc` and
Autodesk's `adskSDKversioninfo.rc`, both empty). Project-local files that are missing (an include, a `.tlb`
named by a TYPELIB statement) are stubbed empty in a per-run temp dir, never in the input. A `.rc` that still
fails is logged in `build.log` and skipped. The DLLs contain only resources (no code), so they run on
nothing: they are loaded as data files.

`dialogs.json` lists for each DLL: the `.rc` path relative to the root, the project (derived like
`InputScanner` in `src/Retrace/Engine/Scan/`: nearest folder with a `.csproj/.vbproj/.vcxproj`, else the top folder), and per dialog
`resource` (numeric id or upper-cased string name inside the DLL), `name` (symbolic, via `#define`s in the
included headers), `line` and `formId` (`rc.<Project>.<name as written in the .rc>`).

## Run (on Windows 11 ARM64)

Copy `bin\DialogCapture.exe`, `res\*` (the DLLs and `dialogs.json`) and `run-capture.ps1` to the Windows machine, and be
logged in to the desktop (not only SSH: SSH is session 0 and has no desktop). From an elevated PowerShell:

```powershell
.\run-capture.ps1 -DllDir C:\capture\res -OutDir C:\capture\out
# only one dialog:  -Only ArxDbg:ARXDBG_DLG_SPLASH
```

The script creates a scheduled task `DialogCapture` that runs interactively as the logged-on user
(`schtasks /create ... /it /rl highest`), starts it, waits for `done.txt` and prints it. Or run the exe
directly from a desktop terminal:

```
DialogCapture.exe <dir-with-dlls> <out-dir> [--only <dllname>:<dialogname>] [--hide-console]
```

Don't touch the mouse or keyboard while it runs; every dialog is shown at (40,40) for about 300 ms.

## Output

In `<out-dir>`, per dialog `<dll>__<dialogname>.png` and `.json`, plus:

- `done.txt` - written last: ok/failed counts and every failure. `run-capture.ps1` waits for it.
- `progress.log` - one line per dialog (`START`, then `OK` or `FAIL`). If the run dies on a native crash the
  last `START` line is the culprit; skip it with `--only` runs for the others.

The JSON has: `visibleBounds` (DWM extended frame bounds, the same rect the PNG covers), `windowRect`,
`clientOrigin` (relative to the PNG), `clientSize`, `font` (WM_GETFONT face/height), `dialogBaseUnits`
(`MapDialogRect` of 4x8: `x4`, `y8` pixels), `dpi`, and `children` (class, text, id, style, ex-style, rect
relative to the client origin) for every child window. `modifications` and `stubbedClasses` record what the
tool changed to get the dialog on screen.

## What the tool does to a template

- Classes that aren't registered (MFC/ATL/AutoCAD custom controls) get a stand-in class that paints a gray,
  dashed box with the class name. Real system and common-control classes are never replaced.
- `WS_CHILD` and `DS_CONTROL` are cleared. If the dialog was a child (a property page) it also gets
  `WS_POPUP | WS_CAPTION` so it appears as a window.
- If `CreateDialogIndirectParam` fails, it retries once with no instance handle and `DS_NOFAILCREATE`.

## Status

Built on macOS only so far; not yet run on Windows.

The captures it writes (`<dll>__<dialogname>.png` + `.json`) go in the Retrace captures folder (setting `capturesFolder`) to be used by `RETRACEDOCS`,
and into `tools/compare_captures.py` to score the wireframes. Captures are data: do not commit them.
