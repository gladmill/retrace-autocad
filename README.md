# Retrace

Retrace is an AutoCAD plugin (one `Retrace.dll`) for people who test and document AutoCAD customizations by hand.
You do a manual test once while Retrace records it: ribbon clicks, commands, prompt answers, dialogs, screenshots and
checkpoints. Retrace then writes the test as step-by-step instructions (Markdown, Word, Excel), replays it later and
tells you pass or fail. It can also read your customization folder (`.cuix`, C#, VB.NET, C++, `.rc`, XAML, `.dcl`) and
write Markdown docs: ribbons, commands, dialogs, a source map, and an editable user guide with how-tos built from your recordings.

Status: pre-1.0 (0.1.0). AutoCAD 2026 (R25.1), Windows x64. Written against the `AutoCAD.NET` 25.1.0 packages.
Verified in AutoCAD only in part: see [Limitations](#limitations).

## Install

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) or newer (`global.json` asks for it) on Windows, macOS or Linux; in Visual Studio,
version 2026. AutoCAD is not needed to build: the AutoCAD reference assemblies come from Autodesk's `AutoCAD.NET` NuGet package, and
`nuget.config` restores from nuget.org only.

1. **Build.** Close AutoCAD, then in this folder run `dotnet build Retrace.sln` (or build `Retrace.sln` in Visual Studio).
   Every build writes the autoloader bundle to `src/Retrace/bin/<Debug|Release>/Retrace.bundle`: `PackageContents.xml`, generated
   with the project's version, and `Contents\` with `Retrace.dll` (and its pdb), `trust-retrace.ps1`, `LICENSE` and `THIRD-PARTY-NOTICES.txt`.
   Retrace.dll is the only dll: Markdig and WebView2 are embedded in it. DocumentFormat.OpenXml (Word/Excel export) is the 2.16.0 copy that AutoCAD 2026 ships.

   On Windows, once you have started AutoCAD 2026 at least once (the build looks for your AutoCAD 2026 profile), the build then
   installs the bundle for your Windows user:
   - it replaces `%APPDATA%\Autodesk\ApplicationPlugins\Retrace.bundle` with the new build, so AutoCAD 2026 loads it on every start;
   - it runs `trust-retrace.ps1` on that folder, which adds `...\Retrace.bundle\...` to `TRUSTEDPATHS` in every AutoCAD 2026 profile.
     Retrace is not code-signed, and with `SECURELOAD` = 1 (the default) AutoCAD otherwise stops at "Security - Unsigned Executable File"
     before it loads a dll from a folder that is not a Trusted Location. Nothing else changes, not `SECURELOAD`.

   While AutoCAD has Retrace loaded, the build leaves the installed copy alone, and while any AutoCAD runs it skips the trust step;
   it says so in both cases (AutoCAD locks the dll, and writes its profiles back when it exits): close AutoCAD and build again. Before
   you have started AutoCAD 2026 once, the build only reports that it did not install. `-p:RetraceDeploy=false` builds the bundle
   without installing it, `-p:RetraceDeploy=true` installs even without a profile, and `-p:RetraceTrust=false` installs without
   touching `TRUSTEDPATHS`; all also work as environment variables. Builds on other operating systems and CI builds (`CI`, `TF_BUILD`,
   `JENKINS_URL`, `TEAMCITY_VERSION` or `ContinuousIntegrationBuild` set) never install.
2. Start AutoCAD 2026. A **Retrace** tab appears in the ribbon, right of Home. Run `RETRACESTATUS` to see the version and the root folder.

**Installing a copy by hand** (a machine without the SDK): `scripts/pack.sh <out-dir>` (bash and `python3`) writes
`<out-dir>/Retrace.bundle` and installs nothing. Copy that folder to `%APPDATA%\Autodesk\ApplicationPlugins\`, then trust it with AutoCAD closed:

```powershell
$b = "$env:APPDATA\Autodesk\ApplicationPlugins\Retrace.bundle"
powershell -NoProfile -ExecutionPolicy Bypass -File "$b\Contents\trust-retrace.ps1" -Folder $b
```

`trust-retrace.ps1` adds `<bundle>\...` to `TRUSTEDPATHS` in every AutoCAD 2026 profile of your Windows user (`-Series` picks another release,
default `R25.1`) and changes nothing else.
`-Check` only reports; `-Remove` takes the entry out again. Exit codes: 0 done, 1 error, 2 AutoCAD is running, 3 no AutoCAD 2026 profile
yet (start AutoCAD once), 4 (`-Check`) not trusted everywhere. "Always Load" in AutoCAD's prompt trusts only that one file, so every new
build would ask again. By hand: Options > Files > Trusted Locations > Add, the bundle folder with `\...` at the end.

The guide palette needs the Microsoft Edge WebView2 Runtime (part of Windows 11 and current Edge / Microsoft 365). Without it, everything else still works.

## Quick start

1. **Record.** Ribbon Retrace > Record (`RETRACERECORD`), give the test a name, then work in the drawing as the tester would.
2. **Checkpoint.** After each result worth checking, click Checkpoint (`RETRACEMARK`). Nothing to type: Retrace stores a snapshot of the drawing and a
   screenshot, and names the checkpoint after the last thing you did, as `checkpoint<n>` plus the dialog title or ribbon panel and the control
   (`checkpoint1_draw_grid_button_ok`, `checkpoint2_draw_button_grid`, `checkpoint3_cmd_drawgrid`).
3. **Stop** (`RETRACESTOP`). Retrace closes the recording and writes `steps.md` in the test folder (plus `steps.docx` / `steps.xlsx` if you turned those on).
   If the test has checkpoints, the Checkpoints window opens: each one with its screenshot, what it checks and its name. Type better names and Save;
   the steps, Word and Excel files are written again. Renaming never changes what is checked. Reopen it any time with Name checkpoints (`RETRACECHECKPOINTS`).
   Steps (`RETRACESTEPS`) opens the newest one.
4. **Run the test.** Run test (`RETRACERUN`) asks for a `recording.jsonl`, opens a new drawing, replays the test and shows a verdict.
   Dialogs are filled in from the recorded values when possible, otherwise Retrace waits for you.
5. **Build the docs.** Build docs (`RETRACEDOCS`) asks once for the folder with your code, CUIX and DCL files (setting `sourceFolder`), then runs in the background.
6. **Read the guide.** Guide (`RETRACEGUIDE`) opens a palette with the pages. Edit a page and write outside the generated blocks; your text is kept on every rebuild.

To practise, load `samples/Retrace.Samples` (see its README): a small plugin with `DRAWLINE`, `DRAWCIRCLE`, `DRAWRECT`, `DRAWLABEL` and `DRAWGRID`.

## Commands

All are on the Retrace ribbon tab (panels Record, Test, Guide, Open, Tools) except where noted.

| Command | What it does |
|---|---|
| `RETRACERECORD` | Start recording. Asks for a test name (spaces allowed; Enter alone gives `test`). Takes screenshots and records dialog values if those settings are on. |
| `RETRACEMARK` | Add a checkpoint at once, without a prompt: a snapshot of the drawing and a screenshot, named automatically (see Quick start). |
| `-RETRACEMARK` | The same, but asks for the name on the command line (Enter = the automatic name). Not on the ribbon. |
| `RETRACECHECKPOINTS` | The Checkpoints window for the newest test: rename its checkpoints. Also opens after `RETRACESTOP`, and from Open test > Checkpoints. |
| `RETRACESTOP` | Stop recording, take the screenshots still queued, write `steps.md` (and docx / xlsx) in the background. |
| `RETRACESTATUS` | Recording state, version, root, settings, docs and tests folders, Word / Excel export state. |
| `RETRACESTEPS` | Open the steps of the newest test. |
| `RETRACERUN` | Pick a recording, open a new drawing, replay it, show the verdict. Waits for a person at dialogs it cannot fill. |
| `RETRACESTEP` | Pick a recording and replay it one step at a time in the current drawing. |
| `RETRACEREPLAY` | Pick a recording (Enter = file dialog, `last` = newest) and a mode, Auto or Step. Auto stops with `INCOMPLETE` at a dialog. Not on the ribbon. |
| `RETRACESCR` | Write a `.scr` script next to the recording for its plain-command parts. Not on the ribbon. |
| `RETRACEOPEN` | The Open test window: your tests with their last verdict, and one button per file or folder of the selected test, plus Run this test. |
| `RETRACEDOCS` | Build the docs from the source folder, the captures and the recorded tests. AutoCAD stays usable. |
| `RETRACEGUIDE` | The guide palette: page tree, live preview, Edit / Save / Revert, Open in editor. |
| `RETRACESETTINGS`, `-RETRACESETTINGS` | Settings window; the second form asks on the command line. |
| `RETRACEFOLDER` | Open a folder in Explorer: `Root`, `Tests` (default), `Captures` or `Docs`. Ribbon buttons Tests, Captures and Docs call it. |
| `RETRACECAPTUREDCL` | Open each DCL dialog of a `.dcl` file or folder and save a picture and geometry (`<stem>__<dialog>.png` + `.json`) in the captures folder. |
| `RETRACECLOSEALL` | Close every open drawing without saving (asks once). Handy between test runs. |

Set the environment variable `RETRACE_AUTOSTART=1` to start recording (test name `auto`) at the first idle moment after AutoCAD starts.

## Folders on disk

Everything is under `C:\temp\retrace` (fixed; there is no setting or variable for the root). The tests, captures and docs folders can be moved with settings.

```
C:\temp\retrace\
  settings.json                       your settings
  retrace.log                         errors and warnings of the plugin
  tests\                              one folder per recorded test
    2026-10-10_0243_dtest\            start time, then the name; a clash gets _2, _3
      recording.jsonl                 the recording (see docs/recording-format.md)
      steps.md                        the test as steps with pictures; rewritten on every stop
      steps.docx, steps.xlsx          optional Word and Excel versions of steps.md
      screenshots\step-02-click.png   pictures named after the step that shows them
      screenshots\unused\             pictures the steps do not show
      results\                        one file per run, 2026-10-10_03-15-02_PASS.json, and latest.json
  captures\                           real dialog pictures, <Stem>__<Name>.png + .json
  docs\                               the generated docs (see below)
  cache\webview\                      WebView2 data of the guide palette
  cache\native\<version>\             the extracted native WebView2 loader
```

Recordings hold drawing names, typed text, coordinates and object handles. Treat them as your data.

## Settings

`settings.json` in the root. Missing or broken file = defaults. Empty folder values mean the default. Edit with `RETRACESETTINGS`.

| Key | Default | Meaning |
|---|---|---|
| `sourceFolder` | empty | The code, CUIX and DCL files the docs are built from. `RETRACEDOCS` asks if it is empty. |
| `docsFolder` | `C:\temp\retrace\docs` | Where the docs are written. |
| `testsFolder` | `C:\temp\retrace\tests` | Where recorded tests are kept. |
| `capturesFolder` | `C:\temp\retrace\captures` | Where real dialog pictures are kept. |
| `editorPath` | empty | Program for "Open in editor". Empty = VS Code if found, else `notepad.exe`. |
| `screenshots` | `true` | Take screenshots while recording. |
| `dialogValues` | `true` | Record which dialog fields you change. |
| `autoFillDialogs` | `true` | `RETRACERUN` fills dialogs from the recorded values and closes them. |
| `reviewCheckpoints` | `true` | Open the Checkpoints window after `RETRACESTOP` when the test has checkpoints. |
| `exportDocx` | `false` | Also write `steps.docx` when a recording stops. |
| `exportXlsx` | `false` | Also write `steps.xlsx` when a recording stops. |
| `ribbonTabPosition` | empty (= `afterHome`) | `afterHome`, `first`, `last` or `after:<tab id or title>`, for example `after:ID_TabInsert`. |

## What the docs folder contains

`RETRACEDOCS` writes `C:\temp\retrace\docs\` (details in [docs/output-layout.md](docs/output-layout.md)):

| Folder | What | Edit it? |
|---|---|---|
| `README.md` | Start here: counts, skipped files, warnings. | No |
| `guide\` | The user guide: `index.md`, a page per ribbon tab, how-tos from recordings, pictures. | Yes, outside the marker blocks |
| `reference\` | Ribbon tabs, commands with UI flows, dialogs with wireframes, projects. | No, rewritten every run |
| `test-plans\` | Manual test scripts written from the source. | No |
| `recordings\` | A page per recorded test, most-used buttons, ribbon coverage. | No |
| `data\` | `sourcemap.json`, `recordings.json`, `model.json`. | No |

## Limitations

- Pre-1.0. Run in AutoCAD Electrical 2026 (R25.1) on 2026-10-22: the bundle loads, the Retrace tab appears, and record / checkpoint / stop, `steps.md` with Word and Excel export, `RETRACEREPLAY` (PASS), `RETRACEDOCS` and the guide palette work. Run it in your AutoCAD before you rely on it.
- The engine that builds the docs runs inside the AutoCAD process (on a background thread). A bug in it is caught and reported, but it shares memory with AutoCAD. Screenshots are queued and taken a moment later, never inside an AutoCAD event handler.
- Replay is not a pixel-exact replay: a ribbon click with recorded answers is replayed through its macro. Handles refer to the drawing the test was recorded in. The pick point on an object is not replayed. Points are typed absolute, angles in degrees (assumes `AUNITS` 0). Text answers with spaces break at the first space.
- Not recorded: palettes and modeless windows, grips, right-click menus, mouse-only actions (pan, wheel zoom). Only the active drawing is driven.
- Checkpoints compare geometry, not appearance: model space only, counts per type and layer, layer names, and a hash. Colours, linetypes and paper space are not part of it. A checkpoint without a recorded state is "not verified" and makes the verdict `INCOMPLETE`.
- Dialog values are read from outside through child windows (Win32, WinForms, DCL) or UI Automation (WPF). Owner-drawn controls, list views, trees and password fields are not read. A dialog that opens a nested dialog still needs a person.
- AutoCAD's security prompt is never answered by a replay: the run stops with `INCOMPLETE` and says what to do. A recording does not replay a security prompt either.
- Docs scanners are heuristic: they find less rather than something wrong, and report what they missed under Warnings in `docs\README.md`. Wireframes are drawn from the source, not captured from AutoCAD.
- Not tested in AutoCAD yet: a full `RETRACERUN` (only up to its file picker), the Open test window, the settings dialog, `RETRACECAPTUREDCL`, and plain AutoCAD (only AutoCAD Electrical was used).

## Build and test

```sh
dotnet build Retrace.sln -c Release      # on Windows also installs and trusts the bundle (see Install)
dotnet test tests/Retrace.Tests
scripts/pack.sh /some/out/dir            # a copy of the bundle to hand out; installs nothing
```

More in [AGENTS.md](AGENTS.md). The recording format is in [docs/recording-format.md](docs/recording-format.md).

## License

MIT. See [LICENSE](LICENSE).
