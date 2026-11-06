# Retrace

Retrace is an AutoCAD plugin (one `Retrace.dll`) for people who test and document AutoCAD customizations by hand.
You do a manual test once while Retrace records it: ribbon clicks, commands, prompt answers, dialogs, screenshots and
checkpoints. Retrace then writes the test as step-by-step instructions (Markdown, Word, Excel), replays it later and
tells you pass or fail. It can also read your customization folder (`.cuix`, C#, VB.NET, C++, `.rc`, XAML, `.dcl`) and
write Markdown docs: ribbons, commands, dialogs, a source map, and an editable user guide with how-tos built from your recordings.
And it maps each ribbon button to the LISP, C++, C# or VB.NET code it runs.

Status: pre-1.0 (0.5.0). AutoCAD 2026 (R25.1), Windows x64. Written against the `AutoCAD.NET` 25.1.0 packages.
Verified in AutoCAD only in part: see [Limitations](#limitations).

## Install

**Without building:** download `Retrace-<version>.zip` from the [Releases](https://github.com/gladmill/retrace-autocad/releases) page, unzip it,
copy the `Retrace.bundle` folder to `%APPDATA%\Autodesk\ApplicationPlugins\` and trust it as shown under "Installing a copy by hand" below.
The zip also holds `AcadStatus.bundle`, a small separate add-in that shows on AutoCAD's status bar what AutoCAD and Retrace are doing: see [AcadStatus](#acadstatus-add-in).

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
2. **Checkpoint.** After each result worth checking, click Checkpoint (`RETRACEMARK`) or press Ctrl+Shift+K. The key does not cancel the command you are in: pressed during one, the checkpoint is taken when it ends. Nothing to type: Retrace stores a snapshot of the drawing and a
   screenshot, and names the checkpoint after the last thing you did, as `checkpoint<n>` plus the dialog title, ribbon panel or palette and the control
   (`checkpoint1_draw_grid_button_ok`, `checkpoint2_draw_button_grid`, `checkpoint3_box_button_draw_box`, `checkpoint4_cmd_drawgrid`).
   With the key, point at what the checkpoint is about first: with the cursor on a palette control or a ribbon item, the checkpoint is named
   after it (`checkpoint5_find_results`) and the steps say what was there ("At the cursor: Results (list: "iPhone 5s") in the Find palette.
   Near it: Insert (button), ...").
3. **Stop** (`RETRACESTOP`). Retrace closes the recording and writes `steps.md` in the test folder (plus `steps.docx` / `steps.xlsx` if you turned those on).
   If the test has checkpoints, the Checkpoints window opens: each one with its screenshot, what it checks and its name. Type better names and Save;
   the steps, Word and Excel files are written again. Renaming never changes what is checked. Reopen it any time with Name checkpoints (`RETRACECHECKPOINTS`).
   Steps (`RETRACESTEPS`) opens the newest one.
4. **Run the test.** Run test (`RETRACERUN`) asks for a `recording.jsonl`, opens a new drawing, replays the test and shows a verdict.
   Dialogs are filled in from the recorded values when possible, otherwise Retrace waits for you. Palette steps likewise: Retrace sets the values
   and presses the button in WinForms and WPF palettes, waiting for results of async calls; when it cannot, you do the step and click Next.
5. **Make a training document.** Training doc (`RETRACETDOC`): tick tests, give a title, Start. See [Training documents (Tdoc)](#training-documents-tdoc).
6. **Run many tests.** Tick tests in Open test and press Run suite (`RETRACESUITE`), or use `-RETRACESUITE`. One summary lists every test's verdict. See [Test suites](#test-suites).
7. **Build the docs.** Build docs (`RETRACEDOCS`) asks once for the folder with your code, CUIX and DCL files (setting `sourceFolder`), then runs in the background.
8. **Read the guide.** Guide (`RETRACEGUIDE`) opens a palette with the pages. Edit a page and write outside the generated blocks; your text is kept on every rebuild.

To practise, load `samples/Retrace.Samples` (see its README): a small plugin with `DRAWLINE`, `DRAWCIRCLE`, `DRAWRECT`, `DRAWLABEL` and `DRAWGRID`.

## Commands

All are on the Retrace ribbon tab (panels Record, Test, Guide, Open, Tools) except where noted.

| Command | What it does |
|---|---|
| `RETRACERECORD` | Start recording. Asks for a test name (spaces allowed; Enter alone gives `test`). Takes screenshots and records dialog values if those settings are on. |
| `RETRACEMARK` | Add a checkpoint at once, without a prompt: a snapshot of the drawing and a screenshot, named automatically (see Quick start). The ribbon button cancels the current command first. |
| `-RETRACEMARK` | The same, but asks for the name on the command line (Enter = the automatic name). Not on the ribbon. |
| `RETRACECHECKPOINTS` | The Checkpoints window for the newest test: rename its checkpoints. Also opens after `RETRACESTOP`, and from Open test > Checkpoints. |
| `RETRACESTOP` | Stop recording, take the screenshots still queued, write `steps.md` (and docx / xlsx) in the background. |
| `-RETRACESTOP` | The same as `RETRACESTOP` for scripts and automation: the explicit form for scripts. It never opens the Checkpoints window (whatever `reviewCheckpoints` says) and shows no window or message box; everything it says goes to the command line. Not on the ribbon. |
| `RETRACESTATUS` | Recording state, version, root, settings, docs and tests folders, Word / Excel export state. |
| `RETRACESTEPS` | Open the steps of the newest test. |
| `RETRACERUN` | Pick a recording, open a new drawing, replay it, show the verdict. Waits for a person at dialogs it cannot fill. |
| `RETRACETDOC` | The Training doc window: tick the tests, order them, give a title, Start. Replays them one after another and writes a Word training document with pictures (see [Training documents (Tdoc)](#training-documents-tdoc)). While a run is active it says so; between two tests it ends the run. |
| `-RETRACETDOC` | The same on the command line: asks for the title, then the tests (names or paths separated by `;`, `last`, or `@file` with one per line). Starts nothing if a name finds no test. Not on the ribbon. |
| `RETRACESUITE` | Run the tests ticked in the Open test window as one suite: unattended, past failures, one summary (`results\<stamp>_suite.json` and `.md`). With nothing ticked it opens that window. While a suite runs it says so; between two tests it ends the suite. See [Test suites](#test-suites). Not on the ribbon. |
| `-RETRACESUITE` | The same on the command line, no window: asks for the tests (names or paths separated by `;`, `last`, `@file` with one per line, or Enter / `all` for every test). Starts nothing if a name finds no test. Scriptable like `-RETRACESTOP`. Not on the ribbon. |
| `RETRACESTEP` | Pick a recording and replay it one step at a time in the current drawing. |
| `RETRACEREPLAY` | Pick a recording (Enter = file dialog, `last` = newest) and a mode, Auto or Step. Auto stops with `INCOMPLETE` at a dialog. Run by a SCRIPT file, LISP or a menu macro it asks on the command line and never opens the file dialog; an empty answer gives "No recording chosen." With `FILEDIA` 0 and a person at the keyboard the dialog still opens on Enter. Over COM (not detected as a script) an empty answer opens the dialog, so always send a path or `last`. It still shows the small Retrace replay panel. Not on the ribbon. |
| `RETRACESCR` | Write a `.scr` script next to the recording for its plain-command parts. Not on the ribbon. |
| `RETRACEOPEN` | The Open test window: your tests with a tick box and their last verdict, and one button per file or folder of the selected test, plus Run this test, Run suite for the ticked tests and a button for the last suite's summary. |
| `RETRACEDOCS` | Build the docs from the source folder, the captures and the recorded tests. AutoCAD stays usable. |
| `RETRACEMAP` | The Map window: tick the folders with your CUI / CUIX files and code, press Map, and see which code each ribbon button runs. Writes `ribbon-map.md`, `.csv` and `.json` (see [Ribbon map](#ribbon-map)). AutoCAD stays usable. |
| `-RETRACEMAP` | The same on the command line, for scripts: asks for the folders (separated by `;`; Enter keeps the ones ticked last time), then whether to map the ribbon AutoCAD shows now. Prints a summary when it is done. Not on the ribbon. |
| `RETRACEGUIDE` | The guide palette: page tree, live preview, Edit / Save / Revert, Open in editor. |
| `RETRACESETTINGS`, `-RETRACESETTINGS` | Settings window; the second form asks on the command line (including the checkpoint shortcut). |
| `RETRACEFOLDER` | Open a folder in Explorer: `Root`, `Tests` (default), `Captures` or `Docs`. Ribbon buttons Tests, Captures and Docs call it. |
| `RETRACECAPTUREDCL` | Open each DCL dialog of a `.dcl` file or folder and save a picture and geometry (`<stem>__<dialog>.png` + `.json`) in the captures folder. |
| `RETRACECAPTURERIBBON` | Show each ribbon tab (All: also the hidden and contextual ones, shown for a moment each; Shown; or Current) and save what AutoCAD drew in the captures folder (`ribbon__<tab>.png`, one picture per panel, and a `.json` with where every panel and button is). The next docs build shows these instead of the drawn wireframes. |
| `RETRACECLOSEALL` | Close every open drawing without saving (asks once). Handy between test runs. |

Set the environment variable `RETRACE_AUTOSTART=1` to start recording (test name `auto`) at the first idle moment after AutoCAD starts.

## Training documents (Tdoc)

Tdoc replays a list of recorded tests and writes a Word document that teaches the steps: for each test a chapter, for each step the sentences to follow and a picture of AutoCAD, and for each checkpoint a picture of the drawing with its result.
Details, file layout and limits: [docs/tdoc.md](docs/tdoc.md).

1. Ribbon Retrace > Test > Training doc (`RETRACETDOC`). Tick the tests (newest first; Move up / Move down set the order), give the document a title, press Start. Or use `-RETRACETDOC` on the command line.
2. Retrace runs each test the way Run test does (a new drawing, dialogs filled in from the recording) and takes the pictures. Leave AutoCAD alone until it is done: it brings windows to the front, and a click would end up in a picture. Stop in the replay window ends the whole run; the document is still written, with the tests done so far, and its title ends in "(incomplete)".
3. The document is `C:\temp\retrace\docs\training\<stamp>_<title>.docx`; beside it a folder of the same name holds `tdoc.json` and `pictures\`.

Three settings (Settings window, group "Training documents (Tdoc)"; `-RETRACESETTINGS` asks for them last): `tdocBringToFront`, `tdocAnnotate`, `tdocStepDelayMs` (see Settings).

## Test suites

`RETRACESUITE` replays many tests unattended and writes one summary. Details: [docs/suite.md](docs/suite.md).

1. Open test (`RETRACEOPEN`), tick the tests, press Run suite. Or `-RETRACESUITE` on the command line (names, `@file`, or `all`).
2. Each test runs like Run test, in a new drawing, and its drawings are closed without saving afterwards. A drawing you had open is never closed or saved. FAIL, INCOMPLETE, ERROR and tests that need a person end that test only, not the suite. A dialog Retrace cannot fill stays open: if it is still open 30 s later the suite stops (STOPPED, the rest NOT-RUN).
3. A step that runs longer than `suiteStepTimeoutSeconds` (default 120) is cancelled and the test is `ERROR`, "step N timed out". A security prompt or dialog that stays open stops the suite with a summary of what ran.
   A test that opens a drawing you already have open gets a copy of the file, which loses relative xrefs, images and AutoCAD Electrical project membership: close project drawings before running a suite that opens them.
4. The summary is `C:\temp\retrace\results\<stamp>_suite.json` and `.md`: one line per test with its verdict, duration, the checkpoints that differed and the path to its own result. The Open test window's Last suite button opens it.

## Status board

Retrace writes what it is doing to one small JSON file, so a person or a tool outside AutoCAD can see it: is a test recording or replaying, at which step, is a suite
on test 3 of 7, does a step need a person, how did the last run end.

- **The file.** `retrace-<acad pid>.json` (for example `retrace-4812.json`) in `%ProgramData%\Gladmill\acad-status\`, or in the folder the environment variable
  `ACAD_STATUS_DIR` names. One file per AutoCAD session; it is written while Retrace is loaded and set to `state: "stopped"` when AutoCAD closes Retrace.
  The format is the status board file contract: [acad-status/CONTRACT.md](acad-status/CONTRACT.md) (`source` `retrace`, `detail` described there). `pid` is AutoCAD's process id and `pidStart`
  the time AutoCAD started (UTC): Windows reuses process ids, and a reader that finds the pid alive with another start time knows the file is a dead session's.
- **How to see it.** Open the file in any editor, or use the AcadStatus add-in (in the release zip, see [AcadStatus add-in](#acadstatus-add-in)), which shows the files of all tools on AutoCAD's status bar and in a details window:
  [acad-status](acad-status/).
- **What it says.**

  | Retrace is | `state` | `short` | `summary` (example) |
  |---|---|---|---|
  | doing nothing | `idle` | | `Idle`; after a run `Last: smoke032 PASS (2/2 checkpoints)` |
  | recording | `running` | `Rec 12` (about the steps a replay will show: ribbon clicks and tab switches, commands typed at the top level, LISP calls, checkpoints, palette steps, clicks in native palettes, drawings opened outside a command; not a drawing's start-up LISP, commands nested in another, or dialogs; an Auto replay leaves out the tab switches) | `Recording smoke040: 12 steps` |
  | replaying (RETRACERUN, RETRACEREPLAY, RETRACESTEP) | `running` | `Step 4/5` | `Replay smoke040: step 4/5 (BREAK)`; at a dialog it fills itself: `..., filling dialog Plot` |
  | running a suite or Tdoc | `running` | `Suite 3/7`, `Tdoc 3/7` | `Suite 3/7: smoke040, step 4/5 (BREAK)` |
  | stuck on a step only a person can do (a dialog it cannot fill, a palette it cannot drive, a dialog that stays open between suite tests) | `needs-person` | `Needs person` | `Replay smoke040: step 4/5 (PLOT) needs a person: a dialog Retrace cannot fill: Plot` (also `detail.needsPerson`) |
  | stopped at a dialog that is still open (RETRACEREPLAY Auto) | `needs-person` until the dialog closes, then `idle` | `Needs person` | `Last: smoke040 INCOMPLETE: needs a person: dialog Plot` |
  | done with a test that ended in ERROR, or a suite or Tdoc run with an ERROR test | `error` until the next run starts | `Test ERROR` | `Last: smoke040 ERROR: step 3 timed out`, `Last suite FAIL: 1 ERROR, 2 PASS (3 tests)` |
  | done with a suite or Tdoc run | `idle` | | `Last suite PASS: 2 PASS (2 tests)`, `Last Tdoc: 3 tests (1 FAIL, 2 PASS), 24 pictures` (the counts are in the suite summary's order: by verdict name; a test the run skipped or never reached counts as `NOT-RUN`, as in the suite) |
  | Retrace could not work out what to write | `error` | `Board error` | `status board error: InvalidOperationException` (the exception's type only; the file keeps being written, and the details are in `retrace.log`) |
  | a suite stopped because a dialog or security prompt stayed open between two tests | `needs-person` until the dialog closes, then `idle` | `Needs person` | `Last suite STOPPED: 1 NEEDS-PERSON, 1 PASS (2 tests). Stopped: AutoCAD did not answer: a dialog stayed open after st-pass1` |
  | unloaded | `stopped` | | `Retrace unloaded` |

  `owner` is set only when Retrace knows how the work started: `user` (a click on a Retrace ribbon button, or on a button in the Open test window, typed the command that started
  the recording or replay; the click counts for that one command only, and only if the command starts within 15 s), `suite` (a suite run), `tdoc` (a Tdoc run).
  A command typed at the command line cannot be told from one sent over COM, so those runs have no `owner`.
- **How often.** Immediately when what the board shows changes, and at least every 3 seconds (`ttlSec` is 15). The timer runs on AutoCAD's UI thread, so when AutoCAD hangs
  the file goes stale; the status board then shows AutoCAD as busy. That is intended.
- **Cost and safety.** A write is one small file (a temporary file, then a move over the old one). A failed write is logged once in `retrace.log` and never reaches the
  test or the recording. Turn the whole thing off with the setting `statusBoard`.

## AcadStatus add-in

The release zip holds a second, independent add-in next to Retrace: **AcadStatus**. It answers one question at a glance: what is AutoCAD doing right now? It shows a coloured dot
and a few words on AutoCAD's status bar (idle, a command running, waiting at a prompt, blocked by a dialog nobody answered, not responding) and merges the status board files of
every tool, so a Retrace test run that needs a person shows up there too. Click the pane for a small details window; **Copy for Claude** copies a plain-text dump you can paste into a chat.
The commands are `ACADSTATUS` (prints the dump) and `ACADSTATUSPANE` (shows or hides the pane). AcadStatus only looks: it never touches your drawing and never presses anything in a dialog.

Retrace does not need AcadStatus, and AcadStatus does not need Retrace. To use both:

1. With AutoCAD 2026 closed, unzip `Retrace-<version>.zip` and copy **both** folders, `Retrace.bundle` and `AcadStatus.bundle`, to `%APPDATA%\Autodesk\ApplicationPlugins\`.
2. Trust each bundle once (each is a separate unsigned dll and a separate Trusted Location):

   ```powershell
   $b = "$env:APPDATA\Autodesk\ApplicationPlugins\Retrace.bundle"
   powershell -NoProfile -ExecutionPolicy Bypass -File "$b\Contents\trust-retrace.ps1" -Folder $b
   $b = "$env:APPDATA\Autodesk\ApplicationPlugins\AcadStatus.bundle"
   powershell -NoProfile -ExecutionPolicy Bypass -File "$b\Contents\trust-acadstatus.ps1" -Folder $b
   ```
3. Start AutoCAD. The pane appears at the right end of the status bar a second later.

Details, the pane's colours, what it cannot see and the file format: [acad-status/README.md](acad-status/README.md).

## Ribbon map

Map (`RETRACEMAP`, ribbon Retrace > Guide > Map) answers "which code runs when I click this ribbon button?". It writes one row per ribbon item: its tab, panel and label, what a click runs (a command, a LISP function, a method, a class), and where your code defines that, in LISP, C++, C# or VB.NET.

**What it reads.** Everything comes from the folders you pick, and from AutoCAD if you let it:

- Ribbon items from `.cuix` and `.cui` files, and from ribbons that code builds (C# and VB.NET with `RibbonTab`, `RibbonPanelSource`, `RibbonButton`...). With the live ribbon on, also the ribbon AutoCAD shows now: the tabs that plugins build when AutoCAD runs, and the `.cuix` files AutoCAD has loaded.
- Code in the same folders: LISP (`.lsp`, `.mnl`), C++, C# and VB.NET. A command is found from `(defun c:NAME ...)` and `(vlax-add-cmd "NAME" ...)` in LISP, `[CommandMethod]` / `<CommandMethod>` in C# and VB.NET, and `addCommand` / `ACED_ARXCOMMAND_ENTRY_AUTO` in C++. LISP functions a button's macro calls are followed; `(load "file")` is matched to the file. A code ribbon's button is followed to its macro (`CommandParameter`), its handler method or its handler class.

**The window.**

1. **Folders.** Add folders... (several at once) or drop folders, or files from them, from Explorer. Every folder you ever add stays in the list, newest first, ticked or not, in `map-folders.json`; tick the ones for this map. A folder that is gone shows "(not found)" and stays for when it is back. Remove from list only forgets a folder. Space ticks the selected folder, Delete removes it.
2. **Options.** "Also map the ribbon AutoCAD shows now" adds the live ribbon. "List every live tab, also AutoCAD's own" lists the tabs of AutoCAD itself; off, a tab that only the live ribbon has is listed only when something in it maps to your code. Both are remembered.
3. **Map.** Runs in the background; AutoCAD stays usable and Cancel stops it. The result is a table you can filter (type part of a tab, button, command, file or method; or show only mapped, not found or no action items) and sort. Double-click a row, or Enter, opens the code in your editor (Settings > Editor) at its line. "Open where defined" opens the ribbon item itself: the code that builds it, or its `.cuix` in Explorer (a `.cuix` is a zip, so there is no line to jump to). Hover a cell for details.

**What it writes.** `C:\temp\retrace\map\ribbon-map.md` (the report), `ribbon-map.csv` (for Excel) and `ribbon-map.json` (for tools), rewritten at every map; the columns, Origin, Found by and Status are explained in [docs/ribbon-map.md](docs/ribbon-map.md). They hold full paths of your local folders and the names of your buttons and code: treat them as your data. The Report, Excel and Map folder buttons open them.

**It is heuristic.** It finds less rather than something wrong. A LISP command is found only from `(defun c:...)` and `(vlax-add-cmd ...)`; compiled `.fas` / `.vlx` files are not read. A macro's LISP calls are followed, a DIESEL macro (`$M=...`) is not. A code ribbon is read from the source and from the live ribbon; a button built through helper methods or from data may not be understood. A row that runs something no chosen folder defines is "Not found", and one with nothing to look up (a drop-down, an AutoCAD button the live ribbon tells nothing about) is "No action". What could not be read is under Warnings.

## Folders on disk

Everything is under `C:\temp\retrace` (fixed; there is no setting or variable for the root). The tests, captures and docs folders can be moved with settings.

```
C:\temp\retrace\
  settings.json                       your settings
  retrace.log                         errors and warnings of the plugin
  map-folders.json                    the Map window's folders, newest first, and which are ticked
  map\                                the last ribbon map: ribbon-map.md, ribbon-map.csv, ribbon-map.json
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
    training\                         Tdoc output: <stamp>_<title>.docx, and a folder <stamp>_<title>\ with tdoc.json and pictures\
  cache\webview\                      WebView2 data of the guide palette
  cache\native\<version>\             the extracted native WebView2 loader
```

The one file outside this folder is the status board file, `%ProgramData%\Gladmill\acad-status\retrace-<acad pid>.json` (see [Status board](#status-board)).

Recordings hold drawing names, typed text, coordinates and object handles. Treat them as your data. The map files hold full local paths and the names of your buttons and code.

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
| `dialogValues` | `true` | Record which fields you change in dialogs and in palette sets, and the buttons you click in palette sets. |
| `autoFillDialogs` | `true` | `RETRACERUN` fills dialogs from the recorded values and closes them. An edit box gets the notifications a person's typing gives (so a DCL `action_tile` runs); a message box closed with OK is pressed when it has no input fields; a dialog its command shows right after the previous one closed (a LISP alert after a DCL dialog) is handled as its own step. |
| `reviewCheckpoints` | `true` | Open the Checkpoints window after `RETRACESTOP` when the test has checkpoints. `-RETRACESTOP` never does, and neither does `RETRACESTOP` run by a SCRIPT file, LISP or a menu macro (CMDACTIVE bit 4 / 32). A command sent over COM (`SendCommand`, as `tools/vm` does) is NOT detected as a script (CMDACTIVE is 1, checked on the VM, 2026-11-02): from COM or any other automation use `-RETRACESTOP`. |
| `showDrivenUi` | `true` | Run test opens a rolled-up or auto-hidden palette before it drives it, by resting the mouse on it as a person would (and putting the mouse back after), so the run can be watched. Off: the palette is filled while it stays hidden. Recording never moves the mouse. |
| `tdocBringToFront` | `true` | Tdoc run (training document): before every picture, bring the UI of the step to the front (AutoCAD's window, the dialog being filled, the palette), move a dialog that is off screen onto it and keep Retrace's own replay window out of the picture. |
| `tdocAnnotate` | `true` | Tdoc run: draw green numbered boxes on the pictures where the reader gives input (the ribbon button, the command line for typed answers, each dialog field that is filled, the button that closes the dialog, palette controls). The legend under the picture says what each number is. |
| `suiteStepTimeoutSeconds` | `120` | Suite run (`RETRACESUITE`): a test step that takes longer than this many seconds is cancelled with `^C^C` and the test is recorded as `ERROR`, "step N timed out". 5 to 3600; a value outside that range is clamped when the settings are read. |
| `statusBoard` | `true` | Write what Retrace is doing to the AutoCAD status board, `retrace-<acad pid>.json` in `%ProgramData%\Gladmill\acad-status` (see [Status board](#status-board)). Off: nothing is written, and a file written earlier in this session is set to `stopped`. Set it in the Settings window or in `settings.json`; the setting is read again when the file changes (a `settings.json` that cannot be parsed keeps the value from before). `-RETRACESETTINGS` shows it but does not ask for it. |
| `tdocStepDelayMs` | `1500` | Tdoc run: milliseconds to wait before each picture and between steps, so the screen has repainted (tool tips gone, dialogs drawn). 0 to 10000; a value outside that range is clamped when the settings are read. |
| `autoDetectBehavior` | `true` | Checkpoints verify what changed in the drawing since the previous checkpoint (the first: since recording started), besides the model-space entities. Off: the behaviors in `checkBehaviors` are verified whole. See [recording format](docs/recording-format.md), "Behavior checks". |
| `checkBehaviors` | `["layers","layouts","symbols"]` | Behavior ids verified when `autoDetectBehavior` is off. Ids: `layers`, `appearance`, `layouts`, `paperspace`, `viewports`, `xdata`, `xdicts`, `symbols`, `sysvars`, `views`, `psattributes`. In manual mode `appearance` and `paperspace` listed here are recorded as well. |
| `recordBehaviors` | `[]` | Opt-in behaviors to store in every checkpoint while recording, in either mode: `appearance`, `paperspace`. Off by default because they hold one item per entity. A settings file without it reads as empty. |
| `sysvarsAdd`, `sysvarsRemove` | `[]` | System variable names added to / removed from the built-in list the `sysvars` behavior reads (without case). Names AutoCAD does not know are skipped. |
| `exportDocx` | `false` | Also write `steps.docx` when a recording stops. |
| `exportXlsx` | `false` | Also write `steps.xlsx` when a recording stops. |
| `checkpointKey` | `Ctrl+Shift+K` | Shortcut for a checkpoint while recording, e.g. `Ctrl+Shift+J` or `F13`. Ctrl and Shift plus A-Z, 0-9 or F1-F24; a letter or digit needs Ctrl. No Alt (letting it go opens the ribbon's key tips). While recording Retrace takes the key from AutoCAD, so pick one AutoCAD does not use: F1-F12, `Ctrl+1` (Properties) and `Ctrl+C`/`V`/`Z`/`S` are AutoCAD's. Pressed during a command, the checkpoint is taken when that command ends. `None` or empty = off. An invalid value falls back to the default and is logged. While a dialog is open the key goes to the dialog. Set it in Settings (click the box, press the keys) or with `-RETRACESETTINGS`. |
| `promptStyle` | `commandLine` | How Retrace's commands ask for a test name, a recording, a file or folder, or a choice. `ui`: in a window on top of AutoCAD, with Browse... (the Windows file or folder dialog) and the places recently chosen for that question; choices are buttons. `commandLine`: on the command line. A command run from a script or LISP, or with `FILEDIA` 0, always asks on the command line. Set it in Settings or with `-RETRACESETTINGS`. Recent places are kept in `recent.json` in the root folder. |
| `loadedCustomization` | `true` | `RETRACEDOCS` also documents the customization files AutoCAD has loaded at that moment: each menu group's file (the main `.cuix` and its partials) and the enterprise file (`ENTERPRISEMENU`), besides the `.cuix` / `.cui` files in the source folder. Partial files that a read file names in its header are read too (relative to that file); one that is missing is a warning. A file is read once; two files with the same name are told apart by their full path. Legacy `.cui` files (one XML file) are read like `.cuix`. |
| `captureRibbon` | `true` | `RETRACEDOCS` (Build docs) first captures every ribbon tab, as `RETRACECAPTURERIBBON` All does, and then builds, so the docs show the ribbon as AutoCAD draws it, not wireframes. Hidden tabs and contextual tabs (Text Editor, Array...) are shown for a moment each, then the first tab is active again. About 1-2 minutes for AutoCAD Electrical's ~70 tabs; leave AutoCAD alone meanwhile. Tabs AutoCAD cannot show (in no workspace, a workspace this product does not have, or empty) keep their wireframe and are listed under Warnings. Off: the last captures in the captures folder are used. |
| `captureAllWorkspaces` | `false` | With `captureRibbon`: also switch to each other workspace of the main customization file (WSCURRENT) for the tabs only it loads (3D Modeling's Solid, Mesh...), then back. Off by default because going back reloads your workspace as saved: unsaved palette and toolbar changes are lost. Never done while `WSAUTOSAVE` is on (switching would save into your customization file). Not done while a test is being recorded (no capture then). |
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

- Pre-1.0. Run in AutoCAD Electrical 2026 (R25.1) on 2026-10-22: the bundle loads, the Retrace tab appears, and record / checkpoint / stop, `steps.md` with Word and Excel export, `RETRACEREPLAY` (PASS), `RETRACEDOCS` and the guide palette work. The ribbon map was run in plain AutoCAD 2026 on 2026-10-28: `-RETRACEMAP` and the Map window (ribbon button, Map, filter, Open code, Add folders) on test folders with CUIX, CUI, LISP, C++, C#, VB.NET and the live ribbon, including a tab a plugin builds at run time. Run it in your AutoCAD before you rely on it.
- The status board was run in AutoCAD 2026 on 2026-11-05: idle, recording (`owner` `user` after a click on the Record button, none after a refused click and a COM command), `RETRACEREPLAY` Auto (also one that stopped at a LISP `alert` left open: `needs-person` until the alert was closed), `-RETRACESUITE`, the `statusBoard` setting turned off and on (and a `settings.json` that cannot be parsed keeping it off), and AutoCAD closing (`stopped`). The `error` state, a suite stopped by an open dialog, a Tdoc run and a palette step that needs a person were checked in unit tests only.
- The engine that builds the docs runs inside the AutoCAD process (on a background thread). A bug in it is caught and reported, but it shares memory with AutoCAD. Screenshots are queued and taken a moment later, never inside an AutoCAD event handler.
- Replay is not a pixel-exact replay: a ribbon click with no recorded answers runs the live button's handler; one with answers, one whose button is gone, and one whose handler starts no command within 2 s are replayed through the macro (the log says so, with the button's type and handler). Handles refer to the drawing the test was recorded in. A pick on an object (BREAK, FILLET, TRIM, EXTEND, nested picks) is replayed at the recorded point, in the view it was made in, when the point still hits that object; otherwise by its handle (the replay says so). The view is set before and put back after, so a test needs no `ZOOM` step and no particular window size for those picks and for mouse clicks (a window typed on the keyboard still selects what is visible: start such a test with `ZOOM E`). Points are typed absolute, angles in degrees (assumes `AUNITS` 0). Text answers with spaces break at the first space.
- Palette sets (your plugin's `PaletteSet`, WinForms or WPF content): the fields you change and the push buttons you click are recorded, as one step per
  button (or per palette you leave). Field values are read when you start working in the palette, every ~300 ms, and when you click a button or move on.
  AutoCAD's own native palettes (Properties, Tool Palettes) and the ribbon are not. Palette tabs you switch, list views, trees and drag-and-drop are not
  recorded. Replay does WinForms and WPF palette steps itself; with `showDrivenUi` it first opens a rolled-up or auto-hidden palette so you see
  it being filled (the replay window moves aside when it would cover the palette or a dialog). A palette step's pictures include a close-up of
  the palette taken while it was open, so a palette that rolled up right after still shows in the steps. Give fields stable names (`Name`,
  `x:Name`) and, next to text that changes, an `AccessibleName` / `AutomationProperties.Name`.
- Not recorded: other modeless windows, grips, right-click menus, mouse-only actions (pan, wheel zoom). Only the active drawing is driven.
- The checkpoint's own comparison (the digest) is geometry only: model space, counts per type and layer, layer names, and a hash. Appearance and paper space are verified by two more behaviors: `Entity appearance` (layer, color, linetype, linetype scale, lineweight, transparency and plot style of each entity, in model space and on the layouts) and `Paper-space entities` (each entity drawn on a layout, one by one). They cost one item per entity (about 250 bytes each in every checkpoint's stored snapshot), so they are recorded only when you tick **Record** for them in Settings (Behavior checks), in either mode (in manual mode, ticking them under `checkBehaviors` records them too): **tick Record before recording to be able to check them**. Then, in auto mode, Retrace picks `Paper-space entities` when the entities on a layout changed, and `Entity appearance` for entities that are new or whose own look changed (a layer property change is left to `Layer state`, and removed entities to the digest). An auto checkpoint that would store more than 2000 `Entity appearance` items (a mass edit) leaves it out and says so in `retrace.log`. Not covered: the contents of block definitions (only their fingerprint) and viewports (their own behavior). Recordings made without Record pass unchanged; the Checkpoints window can add them to a checkpoint only if the recording stored them.
- Dialog values are read from outside through child windows (Win32, WinForms, DCL) or UI Automation (WPF); WinForms check boxes and radio buttons
  are asked through WinForms itself. Other owner-drawn controls, list views, trees and password fields are not read. A dialog that opens a nested dialog still needs a person.
- AutoCAD's security prompt is never answered by a replay: the run stops with `INCOMPLETE` and says what to do. A recording does not replay a security prompt either.
- Docs scanners are heuristic: they find less rather than something wrong, and report what they missed under Warnings in `docs\README.md`. Wireframes are drawn from the source, not captured from AutoCAD.
- Not tested in AutoCAD yet: the Open test window, the Training doc window (`RETRACETDOC` was run from `-RETRACETDOC`), `RETRACECAPTUREDCL`, and Tdoc with palettes, WPF dialogs, several monitors or display scaling above 100 %. `RETRACERUN` and Tdoc ran in plain AutoCAD 2026 (October 2026: a LISP command with a DCL dialog and its alert, a ribbon button, typed prompts, checkpoints).

## Build and test

```sh
dotnet build Retrace.sln -c Release      # on Windows also installs and trusts the bundle (see Install)
dotnet test tests/Retrace.Tests
scripts/pack.sh /some/out/dir            # a copy of the bundle to hand out; installs nothing
```

More in [AGENTS.md](AGENTS.md). The recording format is in [docs/recording-format.md](docs/recording-format.md).

## License

MIT. See [LICENSE](LICENSE).
