# Retrace: notes for contributors and agents

Retrace is an AutoCAD 2026 plugin (one `Retrace.dll`) that records manual tests, replays them and builds docs from a customization folder.
The user-facing description is in [README.md](README.md). This file is about working on the code.

## Build, test, pack

```sh
dotnet build Retrace.sln -c Release          # plugin, tests, samples; builds on Mac, Linux and Windows; on Windows also installs the bundle
dotnet test tests/Retrace.Tests              # unit tests; synthetic fixtures only, no AutoCAD needed
scripts/pack.sh /some/out/dir                # builds Release and copies the bundle to /some/out/dir/Retrace.bundle; installs nothing
```

- `global.json` asks for the .NET 10 SDK or newer (the tests and `tools/dialog-capture` target net10.0). `nuget.config` restores from nuget.org only, so a feed configured elsewhere on a machine cannot break a fresh clone's build.
- **The bundle.** `bundle/Retrace.Bundle.targets`, imported at the end of `Retrace.csproj`, runs after every build of the plugin. It writes `src/Retrace/bin/<Configuration>/Retrace.bundle` (a fixed path, deleted and rewritten): `PackageContents.xml` from `bundle/PackageContents.template.xml` (`{Version}` = `<Version>`, `{Series}` = `<AutoCADSeries>`), and `Contents/` with the dll, pdb, `scripts/trust-retrace.ps1`, `LICENSE` and `THIRD-PARTY-NOTICES.txt`. On Windows outside CI, once the user has started that AutoCAD release (`HKCU\Software\Autodesk\AutoCAD\<series>` has `CurVer`), it then replaces `%APPDATA%\Autodesk\ApplicationPlugins\Retrace.bundle` with that folder (not while the installed dll is loaded) and runs `trust-retrace.ps1 -Series <AutoCADSeries>` on it (the script exits 2 while any AutoCAD runs). Switches: `RetraceDeploy` (`false`, or `true` to install without a profile), `RetraceTrust=false`, `RetraceDeployDir` (one folder ending in `.bundle`: it is deleted and rewritten). `Retrace.csproj` turns off Visual Studio's fast up-to-date check on Windows, because it would skip the bundle and the install.
- A new AutoCAD release touches: the `AutoCAD.NET` version in `src/Retrace` and `samples/Retrace.Samples`, `<AutoCADSeries>` in `Retrace.csproj` (the build passes it to the trust script), the DocumentFormat.OpenXml version AutoCAD ships in `src/Retrace` and `tests/Retrace.Tests` (below), `DxfRead.VerifiedMajor` and the `acdb<major>.dll` / `accore.dll` export names it calls (`src/Retrace/AutoCad/DxfRead.cs`: re-check `acdbGetAdsName` and `acdbEntGet` on the new release, then raise the version), and the "AutoCAD 2026" wording in the docs.

- `src/Retrace` targets `net8.0-windows`, x64, with `EnableWindowsTargeting`, so the Windows plugin builds on any OS. It references `AutoCAD.NET` 25.1.0 (the package for AutoCAD 2026) with `ExcludeAssets="runtime"`: no Autodesk dll is copied anywhere. 25.1.1 is a net10 build and 25.0.x / 26.x are other AutoCAD releases.
- `tests/Retrace.Tests` targets `net10.0` (xunit). It has no ProjectReference: it compiles `src/Retrace/Engine/**` and `src/Retrace/Core/**` directly (globs in the csproj), plus Markdig and DocumentFormat.OpenXml as package references. New files in those folders are picked up on their own.
- Nothing in `AutoCad/` or `Ui/` can run in the tests. Anything worth testing goes in `Core/` or `Engine/`.
- `scripts/pack.sh` checks that `Retrace.dll` contains every embedded dependency and the private loader name, and fails if the bundle holds any other dll or exe. It needs `python3`. Publish only bundles made by `scripts/pack.sh`: it builds with `ContinuousIntegrationBuild=true`, so the dll and pdb hold no local folder paths.
- **The release zip** holds two folders side by side: `Retrace.bundle` (this `scripts/pack.sh`) and `AcadStatus.bundle` (`acad-status/scripts/pack.sh`, see [acad-status/AGENTS.md](acad-status/AGENTS.md)). Each is made by its own script; zip the two folders, nothing else, and test the zip, not the build output.
- `samples/Retrace.Samples` is a demo plugin (own dll, not in the bundle) for practising the flow and for VM tests.
- AutoCAD only runs on Windows. There a build installs the plugin (above); from macOS or Linux, `scripts/pack.sh` and copy `Retrace.bundle` to `%APPDATA%\Autodesk\ApplicationPlugins\` (see "VM test scripts").

## Architecture

```
src/Retrace/
  Engine/        pure .NET: scans a source folder, writes the docs, reads recordings, writes steps. No AutoCAD, no UI.
    Scan/          InputScanner, Cuix/, CodeRibbon/, Commands/ (commands + UI flow; LispScanner reads .lsp / .mnl only when a scan asks), Forms/ (WinForms, .rc, XAML, DCL, palettes)
    Model.cs       the contract between scanners and writers (EngineModel); Linker.cs links ribbon items, commands and forms
    Pipeline.cs    scan -> analyze -> link -> emit, with a CancellationToken
    Map/           the ribbon map: RibbonMapper (every ribbon item -> the code it runs), RibbonMapWriter (md / csv / json), MacroText (what a macro runs),
                   LiveRibbon (plain copy of the live ribbon), RibbonMapModel (options, rows, result)
    Emit/          markdown, SVG wireframes, sourcemap.json, guide, manual test plans; DocsLayout.cs names every path in the docs folder
    Recordings/    recording.jsonl reader, steps.md / docx / xlsx writers, picture naming, recordings analysis and pages;
                   TrainingManifest (tdoc.json) and TrainingDocx (the Tdoc Word file)
    Util/          CodeText (comments, braces, arguments), Rx (regex with a timeout), font metrics, SVG helpers
    EngineRunner.cs  the entry points
  Core/          plugin logic with no AutoCAD, WPF or WinForms: Paths, RetraceSettings, SessionWriter, Plan,
                 ReplayResult, DrawingDigest, ShotPolicy, DialogWait, DialogFillCore, SecurityPrompt, RibbonTabPosition, GuideModel, EngineJobs,
                 MapFolders (every folder given to the Map window, newest first), EditorLaunch (the arguments that open a file at a line in the editor),
                 Suite: SuiteResult (the summary of a suite run: json and md, no AutoCAD), SuiteTestList (the -RETRACESUITE list: all, names, @file),
                 Tdoc: TdocPlan (instruction sentences and numbered legend lines per step), TdocNaming (folder, docx and picture names), TdocTestList (the -RETRACETDOC list),
                 TdocGeometry (move a dialog onto the screen), Annotate (green numbered boxes on BGRA pixels),
                 Status board: StatusBoard (the entry for what Retrace is doing, the JSON of the contract, short-text caps, the folder, the atomic write), StatusWriter (skips repeats, `since`, logs a failure once, never throws), StartOrigin (a click on a Retrace button makes the run it starts the user's), StepTally (the steps of a recording as it is made, "Rec 12")
  AutoCad/       everything that touches AutoCAD: RetraceApp (IExtensionApplication and every [CommandMethod]), Commands (partial: Commands.Map is RETRACEMAP),
                 RetraceTab (ribbon), Recorder, Replayer, ViewControl (reads, shows and puts back the active viewport's view: Editor.GetCurrentView / SetCurrentView; pure parts in Core/ViewSpec, Core/PickReplay), PickChecker (does a recorded pick point still hit its object; read-only), BehaviorCapture (behavior snapshot of the drawing, read-only; pure parts in Core/BehaviorKeys), DialogWatch / DialogFill (dialog values), ShotService, DrawingState, DclCapture, Native / Win32, Log,
                 LiveRibbonReader (copies the live ribbon into the engine's LiveTab records),
                 TestQueue (the queue of tests behind Tdoc and the suite; one at a time through RETRACERUN, start watchdog), SuiteRun (a suite run: unattended, closes each test's drawings, summary; Commands.Suite),
                 Tdoc: TdocRun (a Tdoc run on the queue), TdocCapture (the pictures of one test, called by the Replayer), TdocTargets (where a box goes), Commands.Tdoc,
                 StatusBoardHost (the status board file: a UI-thread timer and one-line hooks that collect the facts for Core/StatusBoard)
  Ui/            WPF windows and the guide palette: SettingsWindow (with SysvarWindow), ReplayWindow, OpenTestWindow, TdocWindow, MapWindow (with MapRowView, its display text), GuidePalette, GuideControl, MarkdownPage, ScriptViewer, WebPreview
  Embedded/      Dependencies.cs
```

### The layer rule

`Engine/` and `Core/` never use `Autodesk.*`, `System.Windows*` or `Microsoft.Web.*`. `AutoCad/` and `Ui/` may use anything and call down into `Core/` and `Engine/`; the engine never calls up.
`tests/Retrace.Tests/LayerRulesTests.cs` enforces this by scanning the source files, and also that `Engine/` and `Core/` run safely in the AutoCAD process:
no `Console.`, no `Environment.Exit`, `Process.Start` or `AppDomain.SetData`, and no `new Regex(` or static `Regex.` calls (use `Rx.Create` / `Rx.Replace`, which set a 5 s match timeout).

Namespaces follow folders (`Retrace.Engine.Scan`, `Retrace.Core`, `Retrace.AutoCad`, `Retrace.Ui`). A file is named after its main type.

### Engine API

`Retrace.Engine.EngineRunner` is the in-process entry point. Every method is synchronous, never throws, never writes to the console and returns an `EngineResult` (`Messages`, `Warnings`, `Error`, `Cancelled`, `Output`, `Script`, `Docx`, `Xlsx`, `RibbonMap`; `Lines()` for a log).

| Method | What it does |
|---|---|
| `BuildDocs(EngineOptions, CancellationToken)` | Scan the source folder and write the docs folder: reference, guide, test plans, and with `Sessions` the recordings pages and how-tos. |
| `WriteSteps(recording, docx, xlsx, sourceMap)` | `recording.jsonl` -> `steps.md` beside it, pictures renamed after the steps, optional Word and Excel. |
| `Export(path, docx, xlsx)` | Word / Excel from an existing `steps.md` (a recording or test folder is accepted). |
| `AnalyzeRecordings(inputs, output, sourceMap)` | The recordings report alone: `recordings/` and `data/recordings.json`. |
| `WriteTrainingDoc(manifestPath, docxPath)` | The Tdoc Word file from a `tdoc.json` (pictures beside it). `EngineResult.Docx` is the file written. See [docs/tdoc.md](docs/tdoc.md). |
| `MapRibbon(RibbonMapOptions, CancellationToken)` | Read the chosen folders (CUIX / CUI files, ribbon code, LISP, C++, C#, VB.NET) and map every ribbon item to the code it runs, also for the live ribbon the plugin copied (`LiveRibbonReader`). `EngineResult.RibbonMap` holds the rows; with `Output`, `ribbon-map.md`, `.csv` and `.json` are written there. Format: [docs/ribbon-map.md](docs/ribbon-map.md). |

`Retrace.Core.EngineJobs` runs engine jobs one at a time, first in first out (a job started while another runs waits; a second docs build, or a second ribbon map, is refused while one runs or waits), on a thread-pool thread so AutoCAD stays usable. `Cancel()` cancels the running job and drops the waiting ones (their `onDone` gets a cancelled result); `Cancel("map")` does that for the jobs of one kind only (the Map window's Cancel). Every accepted job gets `onDone` exactly once, on that thread. Callers marshal to the UI thread themselves (`Dispatcher.BeginInvoke`); `Commands.StartMap` does it for the Map window and `-RETRACEMAP`. `EngineJobs.DocsOptions(settings)` builds the options from the settings.
Only `BuildDocs` and `MapRibbon` are long enough to check the token: `BuildDocs` between pipeline stages, `MapRibbon` between folders and stages.

### One dll: embedded dependencies

`Retrace.dll` is the only file of the plugin.

DocumentFormat.OpenXml is the exception: AutoCAD 2026 ships 2.16.0 in its program folder and the runtime binds every load of that name to it before any resolver runs (a 3.x build failed in AutoCAD with MissingMethodException). So the csproj references exactly 2.16.0 with `ExcludeAssets="runtime"` and uses AutoCAD's copy. Keep it on the version AutoCAD ships when moving to a new AutoCAD release. Markdig, Microsoft.Web.WebView2.Core / .Wpf and the native `WebView2Loader.dll` are PackageReferences that the csproj target `EmbedDependencies` embeds as resources named `Retrace.Deps.<file>` and `Retrace.Deps.native.<file>`.
AutoCAD does not probe the bundle folder, so `Embedded/Dependencies.cs` hooks `AssemblyLoadContext.Default.Resolving` from a module initializer (before any other type of the dll runs) and loads those assemblies from the resources. If AutoCAD or another plugin already loaded an assembly of that name it uses theirs.

The native WebView2 loader is extracted to `C:\temp\retrace\cache\native\<Retrace version>\Retrace.WebView2Loader.dll` and loaded explicitly through `NativeLibrary.SetDllImportResolver`. It has a private name on purpose:
AutoCAD makes the folders of its plugins visible to the processes it starts, and Autodesk's licensing agent, which outlives AutoCAD, once loaded a plain `WebView2Loader.dll` from there and locked it against updates. A folder per version means a locked old copy never blocks an update.
`pack.sh` fails if the private name is missing from the dll or if the bundle contains another dll. Do not add a plain `WebView2Loader.dll` anywhere in the bundle.

Do not add a dependency without embedding it, adding its name to the list in `pack.sh`, and adding its licence to `THIRD-PARTY-NOTICES.txt` (the bundle ships that file).

## Conventions

- **Scanners are heuristic.** The scanners are not compilers. Prefer finding less over reporting something wrong, and say what was missed: `EngineModel.Warnings` (shown in the docs `README.md`). Shared text helpers (comments, braces, arguments, method finding) are in `Engine/Util/CodeText.cs`. `Model.cs` is the contract between parts; scanners do not add fields to it, extras go in the `Extra` dictionaries.
- **Guide merge.** Pages in `docs\guide\` hold engine blocks between `<!-- engine:begin KEY -->` and `<!-- engine:end KEY -->` and owner text outside them. `Emit/GuideMerge.cs` is the pure merge, `Emit/GuideWriter.cs` and `Emit/GuideHowTo.cs` build the blocks. Rules (also in [docs/output-layout.md](docs/output-layout.md)):
  never touch text outside a block; a second run on unchanged input changes nothing; a block whose source disappeared becomes a removed note; broken markers leave the page alone and write `<name>.engine-new.md`; keys must not depend on a step's position (`step:<kind>:<what>:<n>`). Tests: `GuideTests.cs`, `HowToTests.cs`.
- **Reference and generated pages are rewritten every run.** Only `guide\` is owner-edited.
- **Paths.** Build file paths with `Path.Combine` and the other Path APIs. Use `/` only inside markdown links and in the `file` of `shot` events. Every path in the docs folder is named in `Engine/Emit/DocsLayout.cs`; on-disk test folder names and discovery are in `Engine/Recordings/TestFolders.cs` (the one implementation, Core uses it too).
- **`Paths.Root` is a constant** (`C:\temp\retrace`). No environment variable, no setting. Every function takes the root as a parameter so tests can use a temp folder. Do not read `Paths.Root` directly in new code that tests need to reach; pass the root in.
- **No work in AutoCAD event handlers.** Handlers only record data. Screenshots are queued and taken later from a timer or `Application.Idle`; drawing digests are read only in a command or timer context. Do not touch AutoCAD objects inside a `PromptingFor*` handler (reading `PromptOptions.Keywords` there made AutoCAD loop on mouse picks).
- **Errors never reach AutoCAD.** Command bodies go through `RetraceApp.Run`, which logs and prints. Log with `AutoCad/Log.cs` (writes `C:\temp\retrace\retrace.log`). Window handlers do the same: catch, log, and show one line in the window.
- **The live ribbon is copied, not shared.** The engine never sees an `Autodesk.Windows` object: `AutoCad/LiveRibbonReader.cs` copies the ribbon on the UI thread into `LiveTab` / `LivePanel` / `LiveItem` records (`Engine/Map/LiveRibbon.cs`), which the engine reads on its own thread. `Commands.StartMap` takes the copy, and the list of loaded customization files, before it starts the job.
- **Writing files.** Write to a temp file and move it over the target when a reader could see half a file (results, steps.docx, recording.jsonl rewrite).
- **Wireframe fidelity.** Win32 `.rc` and WinForms wireframes are drawn at 100 % / 96 DPI (`Engine/Util/FontMetrics.cs`; `FontMetricsData.cs` is generated by `tools/gen_font_metrics.py`, numbers only, no font files). Compare against real captures with `tools/compare_captures.py <captures> <out>` (needs Pillow and rsvg-convert).
- **Writing style for docs and messages:** short sentences, concrete, no marketing.

## Safety

- The scanner never opens files whose names look like secrets (`InputScanner.LooksSecret`). It lists them as skipped. Keep it that way.
- Test fixtures are synthetic. Do not copy client code, client names or Autodesk SDK files into the repo.
- Recordings and captures are user data: they hold drawing names, typed text, coordinates and object handles. Never commit real ones; tests use synthetic JSONL only.
- Map files are user data too: `ribbon-map.md`, `.csv`, `.json` and `map-folders.json` hold full local paths and the names of the user's buttons, commands and code. Never commit real ones; tests build synthetic folders.
- No secrets, no keys, no personal or company names in code, docs or fixtures.

## VM test scripts (`tools/vm`)

Scripts for a Windows machine or VM that you can reach over ssh, with AutoCAD 2026 and the Retrace bundle installed. They start AutoCAD in the interactive session, send commands over COM, take screenshots and replay a recorded test unattended
(`tools/vm/run-test.sh <test folder> [--new-drawing] [--run]`). A script that records stops with `-RETRACESTOP`, never `RETRACESTOP` (that one opens the Checkpoints window). They are a developer aid, not part of the plugin. The ssh host comes from `RETRACE_VM_HOST` (default `retrace-vm`, an example alias for `~/.ssh/config`).
The scripts live in `C:\retrace-vm\` on the VM. See `tools/vm/README.md` for each script and the lessons learned (session 0 has no desktop, COM calls hang at a prompt, simulated mouse clicks may not reach a VM).
`tools/vm/fixtures.sh` builds fixture drawings (`fx-*.dwg`: layers, layouts, viewports, xdata, extension dictionaries, symbol tables, sysvars, named views, attributes), one-change mutation scripts and a dump query on the VM; see "Fixtures" in the README.
`tools/dialog-capture` (Win32 `.rc` dialog captures, run on Windows) is documented in its own README.

### Commands for scripts

A `-` command is the form a script, LISP or COM automation may run (COM `SendCommand` is not detected by `Ask.ScriptKnown()`: CMDACTIVE is 1 there): command line only, no window, no message box (`-RETRACEMARK`, `-RETRACESTOP`, `-RETRACESETTINGS`, `-RETRACETDOC`, `-RETRACEMAP`, `-RETRACESUITE`). When a stop-path or other command gets a new window, give the `-` form a way around it (`Core/StopReview.cs` decides the Checkpoints window). File pickers are skipped when `Ask.ScriptRuns()` (CMDACTIVE 4 / 32).

### Suite hook in the Replayer

A suite run passes a `SuiteHook` to `Replayer.Start`; it is null for every other replay and then nothing changes. With it: the step timeout (`CheckStepTimeout`: ^C^C, step `timed-out`, verdict ERROR), a dialog or palette step Retrace cannot do ends the test as needs-person instead of waiting, an "open" step for a drawing that was open before the suite opens a temp copy of the file, and Stop ends the suite. `TestQueue` asks its owner (`Gate`) before each test; the suite closes the previous test's drawings there. See [docs/suite.md](docs/suite.md).

### Tdoc hooks in the Replayer

A Tdoc run (`AutoCad/TdocRun.cs`) is the RETRACERUN path with a `TdocCapture` handed to `Replayer.Start`. Every hook is a no-op while that field is null, so a plain RETRACERUN / RETRACEREPLAY behaves as before; keep it so.
`Replayer`: `BeforeStep` (a gate in `Tick`, before the step runs), `AfterStep` (a checkpoint's result), `DialogReady` (the `DialogAutoFiller.BeforeClose` gate: the picture of the filled dialog is taken before OK is pressed; the UI Automation fill stops before the click when it is set) and the `finished` callback at the end of `Finish`.
Gates return false until ready and are asked again on the next 200 ms tick; nothing blocks the UI thread, and UI Automation runs on a pool thread only. See [docs/tdoc.md](docs/tdoc.md).

### Status board hooks

Retrace writes `retrace-<acad pid>.json` to the AutoCAD status board (format: [acad-status/CONTRACT.md](acad-status/CONTRACT.md); user note: README "Status board").
`AutoCad/StatusBoardHost.cs` is the only place that touches the file; everything else is a one-line call into it, and every call is a no-op when the setting `statusBoard` is off.
- `StatusBoardHost.Start()` / `Shutdown()` from `RetraceApp.Initialize` / `Terminate`. The 3 s timer is a `DispatcherTimer` on the UI thread (the project does not reference WinForms on purpose): a hung UI thread stops it and the file goes stale, which is the design.
- `Changed()` from the Replayer's 200 ms tick, the end of `Recorder.Stop` / `Detach` and `TestQueue.Next`; it writes only when the entry differs from the file. `ReplayStarted` / `ReplayEnded` from `Replayer.Begin` / `Finish` (the end is reported before the verdict box, which blocks), `RecordingStarted` from `Recorder.Start`, `RunStarted` / `SuiteEnded` / `TdocEnded` from the suite and Tdoc runs, `UserClicked` from the ribbon handler and the Open test window.
- Never call it from an AutoCAD event handler (handlers only record data; `Recorder.StepCount` is a plain counter the timer reads, bumped in `NoteContext` through `StepTally` / `StatusBoard.CountsAsStep`, the replay plan's rule for what is a step). If working out the entry throws, the host logs it once (re-armed by the next good one) and writes `StatusBoard.ErrorEntry` (`error`, "status board error: <exception type>"), so the file never just stops. A status write must never throw into, or wait in, a replay or recording: `StatusWriter.Publish` catches everything, logs the first failure once and tries a failed write again at the next call (a reader that holds the file for a moment must not make the heartbeat slip past the ttl of 15 s); only after 3 failures in a row does it wait (3 s, 6 s, 9 s, at most 10 s; forced writes too; only the last word at Terminate ignores it), so an unwritable `ACAD_STATUS_DIR` costs a replay a few failed writes per 10 s. The folder is created for the first write and again only after a failure.
- `owner` is `user` (a Retrace button was clicked), `suite`, `tdoc`, or left out. Retrace cannot tell a typed command from a COM `SendCommand` (CMDACTIVE is 1 for both), so it never writes `COM`. The owner is decided once per command invocation: `RetraceApp.Run` calls `CommandStarted(name)` / `CommandEnded(name)`; for RETRACERECORD / RETRACEREPLAY / RETRACERUN / RETRACESTEP `CommandStarted` asks `StartOrigin.Take` (whole command word, the click is used up, 15 s window) and the run takes the answer when it begins. A click therefore never reaches a later typed or COM invocation. The answer is dropped when RETRACERECORD / RETRACEREPLAY / RETRACESTEP return (their run starts inside the command, however long the person takes at its prompt); RETRACERUN's is kept for 65 s from the moment the command returns (its file picker may take any time), because its replay starts when the new drawing is ready. A recording and a replay keep their owners apart.
- A run that stopped because a step needs a person calls `Finish(..., needsPerson: true)` in the Replayer; only then (and only while a dialog or security prompt is open) is the idle entry needs-person (`StatusBoard.EndOfReplay`). The same for a suite stopped by its gate (`EndOfSuite`); a replay, suite or Tdoc run with an ERROR ends in state `error`.
- `statusBoard` is read with `RetraceSettings.TryLoad` when `settings.json` changes; a file that cannot be parsed keeps the previous value and is tried again at every check (`Load` would give the defaults, which turn the board on). Turning it off writes `stopped`, retried at every tick until it is written.
- The contract is versioned (`v` 1): fields are only added. Change a text in `Core/StatusBoard.cs` and its test in `StatusBoardTests` together; the add-in reads `state`, `short`, `summary` and the `detail` of the contract.

## Tests worth knowing

| File | What it protects |
|---|---|
| `LayerRulesTests` | The layer rule and the in-process safety rules above. |
| `DocsLinkTests` | Every relative link and image in a generated docs folder resolves; running again changes nothing. Its list of old top-level names checks that none of them is written any more. |
| `GuideTests`, `HowToTests` | Guide merge rules and how-to pages. |
| `TestFolderTests`, `RecordingTests`, `RecordingShotTests` | Test folder layout, reading recordings, picture naming. |
| `ScriptTests`, `ExportTests` | `steps.md`, docx and xlsx. |
| `SelectionReplayTests` | How a selection replays: typed window text, the Enter that ends it, the echo of older point answers, the handle fallback with missing handles left out; the typed-key characters. |
| `ViewPickTests` | The recorded view (read, round trip, malformed), what a replay window of another shape shows, pick points typed instead of `(handent)` and every fallback to the handle, TRIM / EXTEND single clicks, clicks that ask for their view. |
| `SettingsTests` | Settings keys and defaults. |
| `BehaviorChecksTests`, `BehaviorKeysTests` | Behavior checks: canonical values, snapshots, picking, compare; entity ids, viewport order, xdata groups, NOD filter. |
| `LispScannerTests`, `MacroTextTests` | LISP commands and functions found in `.lsp` / `.mnl` (comments and strings skipped, line numbers right); what a ribbon macro runs (commands, LISP calls, loads, DIESEL). |
| `RibbonMapperTests` | The ribbon map end to end on synthetic folders: CUIX, CUI and code ribbons, the live ribbon and loaded customization, statuses, the three files. |
| `MapFoldersTests` | The Map window's folder list: newest first, ticks, remove, the options, a broken file. |
| `SecurityPromptTests`, `DialogFillTests`, `DialogValuesTests` | Security prompts, dialog auto-fill and dialog values. |
| `TdocPlanTests`, `TdocNamingTests`, `TdocTestListTests` | Tdoc: instruction sentences and callout numbering, safe title / stamp / picture names and the geometry that moves a dialog onto the screen, the `-RETRACETDOC` list (names, `@file`, paths). |
| `SuiteTests` | Suite: the test list (all, names, @file), a test's summary line (failing checkpoints, NEEDS-PERSON), the json / md summary and its file names, the step timeout setting; `SuiteBookkeepingTests`: tests missing from a stopped suite (NOT-RUN / INCOMPLETE), the latest of same-stamp suites, `UniquePath`, `StepClock` (which step a timeout blames). |
| `StatusBoardTests` | Status board: the entry per mode (record, replay, suite, Tdoc, idle, needs-person, error), the 200 / 18 character caps, the JSON fields of the contract, the folder (`ACAD_STATUS_DIR`), the atomic write, the writer (no repeat writes, `since`, one log line per failure, the retry at the next call and the wait after 3 failures in a row, `stopped`), how a replay, suite or Tdoc run ended (needs-person, error, the counts in one order), what Rec counts, who started a run (whole command word, one click one run), the `statusBoard` setting and `TryLoad`. |
| `AnnotateTests`, `TrainingDocTests` | Tdoc: the green numbered boxes on pixels; `tdoc.json` and the Word file. |
