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
- A new AutoCAD release touches: the `AutoCAD.NET` version in `src/Retrace` and `samples/Retrace.Samples`, `<AutoCADSeries>` in `Retrace.csproj` (the build passes it to the trust script), the DocumentFormat.OpenXml version AutoCAD ships in `src/Retrace` and `tests/Retrace.Tests` (below), and the "AutoCAD 2026" wording in the docs.

- `src/Retrace` targets `net8.0-windows`, x64, with `EnableWindowsTargeting`, so the Windows plugin builds on any OS. It references `AutoCAD.NET` 25.1.0 (the package for AutoCAD 2026) with `ExcludeAssets="runtime"`: no Autodesk dll is copied anywhere. 25.1.1 is a net10 build and 25.0.x / 26.x are other AutoCAD releases.
- `tests/Retrace.Tests` targets `net10.0` (xunit). It has no ProjectReference: it compiles `src/Retrace/Engine/**` and `src/Retrace/Core/**` directly (globs in the csproj), plus Markdig and DocumentFormat.OpenXml as package references. New files in those folders are picked up on their own.
- Nothing in `AutoCad/` or `Ui/` can run in the tests. Anything worth testing goes in `Core/` or `Engine/`.
- `scripts/pack.sh` checks that `Retrace.dll` contains every embedded dependency and the private loader name, and fails if the bundle holds any other dll or exe. It needs `python3`. Publish only bundles made by `scripts/pack.sh`: it builds with `ContinuousIntegrationBuild=true`, so the dll and pdb hold no local folder paths.
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
    Recordings/    recording.jsonl reader, steps.md / docx / xlsx writers, picture naming, recordings analysis and pages
    Util/          CodeText (comments, braces, arguments), Rx (regex with a timeout), font metrics, SVG helpers
    EngineRunner.cs  the entry points
  Core/          plugin logic with no AutoCAD, WPF or WinForms: Paths, RetraceSettings, SessionWriter, Plan,
                 ReplayResult, DrawingDigest, ShotPolicy, DialogWait, DialogFillCore, SecurityPrompt, RibbonTabPosition, GuideModel, EngineJobs,
                 MapFolders (every folder given to the Map window, newest first), EditorLaunch (the arguments that open a file at a line in the editor)
  AutoCad/       everything that touches AutoCAD: RetraceApp (IExtensionApplication and every [CommandMethod]), Commands (partial: Commands.Map is RETRACEMAP),
                 RetraceTab (ribbon), Recorder, Replayer, DialogWatch / DialogFill (dialog values), ShotService, DrawingState, DclCapture, Native / Win32, Log,
                 LiveRibbonReader (copies the live ribbon into the engine's LiveTab records)
  Ui/            WPF windows and the guide palette: SettingsWindow, ReplayWindow, OpenTestWindow, MapWindow (with MapRowView, its display text), GuidePalette, GuideControl, MarkdownPage, ScriptViewer, WebPreview
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
(`tools/vm/run-test.sh <test folder> [--new-drawing] [--run]`). They are a developer aid, not part of the plugin. The ssh host comes from `RETRACE_VM_HOST` (default `retrace-vm`, an example alias for `~/.ssh/config`).
The scripts live in `C:\retrace-vm\` on the VM. See `tools/vm/README.md` for each script and the lessons learned (session 0 has no desktop, COM calls hang at a prompt, simulated mouse clicks may not reach a VM).
`tools/dialog-capture` (Win32 `.rc` dialog captures, run on Windows) is documented in its own README.

## Tests worth knowing

| File | What it protects |
|---|---|
| `LayerRulesTests` | The layer rule and the in-process safety rules above. |
| `DocsLinkTests` | Every relative link and image in a generated docs folder resolves; running again changes nothing. Its list of old top-level names checks that none of them is written any more. |
| `GuideTests`, `HowToTests` | Guide merge rules and how-to pages. |
| `TestFolderTests`, `RecordingTests`, `RecordingShotTests` | Test folder layout, reading recordings, picture naming. |
| `ScriptTests`, `ExportTests` | `steps.md`, docx and xlsx. |
| `SettingsTests` | Settings keys and defaults. |
| `LispScannerTests`, `MacroTextTests` | LISP commands and functions found in `.lsp` / `.mnl` (comments and strings skipped, line numbers right); what a ribbon macro runs (commands, LISP calls, loads, DIESEL). |
| `RibbonMapperTests` | The ribbon map end to end on synthetic folders: CUIX, CUI and code ribbons, the live ribbon and loaded customization, statuses, the three files. |
| `MapFoldersTests` | The Map window's folder list: newest first, ticks, remove, the options, a broken file. |
| `SecurityPromptTests`, `DialogFillTests`, `DialogValuesTests` | Security prompts, dialog auto-fill and dialog values. |
