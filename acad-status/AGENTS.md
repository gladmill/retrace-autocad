# AcadStatus: notes for contributors and agents

AcadStatus is a small AutoCAD 2026 add-in (one `AcadStatus.dll`). It writes `acad-<pid>.json` to the shared status folder and shows a
status bar pane. The user-facing description is in [README.md](README.md); the file format, which other tools write to as well, is in
[CONTRACT.md](CONTRACT.md) (the contract is the source of truth; do not change it from here without the people who own the other writers).

## Build, test, pack

```sh
dotnet build AcadStatus.sln -c Release       # plugin and tests; builds on Mac, Linux and Windows; on Windows also installs the bundle
dotnet test tests/AcadStatus.Tests           # unit tests; synthetic data only, no AutoCAD needed
scripts/pack.sh /some/out/dir                # builds Release and copies the bundle to /some/out/dir/AcadStatus.bundle; installs nothing
```

The build setup is Retrace's, copied (see [../AGENTS.md](../AGENTS.md), "Build, test, pack"):

- `global.json` asks for the .NET 10 SDK or newer (the tests target net10.0). `nuget.config` restores from nuget.org only.
- `src/AcadStatus` targets `net8.0-windows`, x64, with `EnableWindowsTargeting`, so the Windows plugin builds on any OS. It references
  `AutoCAD.NET` 25.1.0 (the package for AutoCAD 2026) with `ExcludeAssets="runtime"`: no Autodesk dll is copied anywhere. It has no other
  package, so `AcadStatus.dll` is the only file of the plugin. `ImplicitUsings` is off (WinForms would add `System.Drawing` and
  `System.Windows.Forms` to every file and clash with the names in `Core/`): every file spells out its usings.
- **The bundle.** `bundle/AcadStatus.Bundle.targets`, imported at the end of `AcadStatus.csproj`, runs after every build. It writes
  `src/AcadStatus/bin/<Configuration>/AcadStatus.bundle` (`PackageContents.xml` from `bundle/PackageContents.template.xml`, and `Contents/`
  with the dll, pdb, `trust-acadstatus.ps1` and `LICENSE`). On Windows outside CI, once the user has started AutoCAD 2026 (`HKCU\Software\Autodesk\AutoCAD\R25.1`
  has `CurVer`), it replaces `%APPDATA%\Autodesk\ApplicationPlugins\AcadStatus.bundle` with that folder (not while the installed dll is
  loaded) and runs `trust-acadstatus.ps1` on it (exit 2 while any AutoCAD runs). Switches: `AcadStatusDeploy` (`false`, or `true` to install
  without a profile), `AcadStatusTrust=false`, `AcadStatusDeployDir` (one folder ending in `.bundle`: it is deleted and rewritten).
- `scripts/pack.sh` builds with `ContinuousIntegrationBuild=true` (no local folder names in the dll and pdb), checks that the dll holds the
  two command names and that the bundle holds no other dll or exe. It needs `python3`. Publish only bundles made by it.
- A new AutoCAD release touches: the `AutoCAD.NET` version, `<AutoCADSeries>` in `AcadStatus.csproj`, and the "AutoCAD 2026" wording in the docs.
- `tests/AcadStatus.Tests` targets `net10.0` (xunit). It has no ProjectReference: it compiles `src/AcadStatus/Core/**` directly (a glob in the
  csproj), so new files in `Core/` are picked up on their own.

## Architecture

```
src/AcadStatus/
  Core/      pure .NET, no AutoCAD, no WinForms: everything the tests reach
    States.cs         the states and their display rank
    StatusEntry.cs    the records a parsed file becomes (entry, acad detail, dialog)
    StatusParser.cs   one file -> entry, tolerant (unknown fields and states fine; bad JSON, missing required field, v not an int >= 1: skipped)
    StatusFiles.cs    the folder: ACAD_STATUS_DIR / ProgramData path, which names a reader reads, reading with a cache, the clean-up
    StatusMerge.cs    ranking: priority, stale handling (busy while the writer's process lives, ignored when gone, stopped never busy), unknown states
    Liveness.cs       ProcessIdentity (pid + pidStart rule), LivenessCache (a lookup per (pid, pidStart) per 5 s), CleanupSchedule (every 10 min)
    CompactText.cs    pane text (<= 18 chars), colour, tooltip lines, ages and clocks
    TextUtil.cs       the one text normalizer (OneLine / Clean / Cap) and the one colour table (TonePalette)
    StateRules.cs     Snapshot -> state; DialogClock (when a dialog was first seen)
    Prompts.cs        PromptTracker (the prompts of one frame) and PromptStack (one drawing: a frame per command / LISP function / transparent command)
    DialogPick.cs     which top-level window is the dialog (pure: modal vs message box rule, the real modal first)
    WindowRules.cs    Placement.Above (where the details window goes) and ClickGuard (the click that closed it is not a new click)
    AcadStatusFile.cs Snapshot -> the entry, the content key (from fields) and, when asked, the acad-<pid>.json text; summary and short texts
    AtomicFile.cs     tmp + replace write
    WritePolicy.cs    write on change and 1.5 s after the last write; PaneSetting (the pane on/off file)
    StatusDump.cs     the plain-text dump (details window, ACADSTATUS, "Copy for Claude"); a block is a lead, a timing and static lines
  AutoCad/   everything that touches AutoCAD or Win32
    AcadStatusApp.cs  IExtensionApplication and the two [CommandMethod]s
    StatusService.cs  the 1 s System.Windows.Forms.Timer: look at AutoCAD, write the file, merge the folder, update pane and details window
    DialogScan.cs     EnumThreadWindows / EnumChildWindows describe the windows to DialogPick; reads the chosen one. Read only
    PromptWatch.cs    Editor PromptingFor* / PromptedFor* and Command / LISP events -> one PromptStack per drawing
    ProcessProbe.cs   is this pid (with this start time) running; the add-in's own start time for `pidStart`
    MessageClock.cs   the time stamp of the Windows message being handled (identity for ClickGuard)
    StatusPane.cs     the dot pane and the text pane on Application.StatusBar, dot icons drawn once per colour
    Log.cs            %LocalAppData%\Gladmill\AcadStatus\acadstatus.log, each distinct message once
  Ui/
    DetailsForm.cs    the small floating details window (WinForms), shown with Application.ShowModelessDialog
    ClipboardText.cs  plain text to the clipboard with Win32 calls ("Copy for Claude")
```

### The layer rule

`Core/` never uses `Autodesk.*`, `System.Windows*`, `System.Drawing`, P/Invoke, `Console.`, `Environment.Exit`, `Process.Start`, or `Regex`.
`tests/AcadStatus.Tests/LayerRulesTests.cs` enforces this by scanning the source files. It also checks that `DialogScan.cs` contains no
call that could press or close something, and that nothing in the plugin opens a database transaction.

### How the add-in behaves

- **One timer.** A `System.Windows.Forms.Timer` (1 s) on AutoCAD's UI thread. It keeps firing inside modal loops such as a LISP `(alert ...)`,
  which is when the file matters most. Each tick: sample AutoCAD (active document name, document count, `CMDACTIVE`, `CMDNAMES`, the pending
  prompt, the dialog), decide the state, write if the content changed or 1.5 s passed since the last write, then (only when the pane is shown
  or the details window is open) read the other files, merge, update the pane and the window; every 10 minutes also the clean-up. Each step
  is in `Log.Safe`; a failing step never stops the next one, and nothing is thrown into AutoCAD. **Re-entry:** updating the pane or the window
  can open a nested modal loop (AutoCAD's ".NET Assertion Failed" dialog, a message box from `StatusBar.Update`), and the ticks that run
  inside it must still scan and write, because a dialog is open then. So the scan and the write are not guarded; only the merge / pane /
  window part is (`_inUi`): a nested tick skips it.
- **No database work.** The tick reads system variables and window text only; no transaction, no document lock.
- **Prompts** come from the editor events (`PromptingFor*` pushes `Options.Message`, `PromptedFor*` pops), the way Retrace's recorder learns
  them. Handlers read only the message: reading `Options.Keywords` there made AutoCAD loop on mouse picks (Retrace's AGENTS.md).
  The state is **per drawing** and a **stack of frames** (`PromptStack`): `CommandWillStart` / `LispWillStart` push a frame, the matching end /
  cancel / failure pops the innermost frame of its own kind and no other (a LISP function that started a command with `(command "_.LINE")`
  and returned leaves that command's frame waiting; a frame that really is dead is cleaned up by the `CMDACTIVE` rule below), prompts belong
  to the innermost frame. So a transparent `'ZOOM`, our own
  `ACADSTATUS` or a LISP expression typed at a pending prompt has a frame of its own and the outer prompt is back when it ends (AutoCAD asks
  it again: the stack treats that as the same prompt, not a nested one). A drawing closing or a command ending in it never touches another
  drawing's prompts. The snapshot uses the active drawing's stack; when the active drawing's `CMDACTIVE` is 0 (no command, no transparent
  command, no script, no dialog, no AutoLISP: bit 32) nothing can be waiting, and every frame, a left-over LISP one included, is dropped.
- **Dialogs.** `DialogPick` (pure) decides. While AutoCAD's main window is disabled, a captioned `#32770`, WinForms or WPF top-level window that is
  not a tool window can be the (modal) dialog, and the one Windows names wins: `GetLastActivePopup(main frame)`, then the thread's active
  window; only if neither is a candidate, the front-most candidate owned by the main frame, then the front-most. While the main window is
  enabled, only a message box counts: a `#32770` whose visible children are all texts and buttons, with a button. So a modeless palette or
  dialog that stays open for hours is never "blocked". Texts are the visible `Static` and `WindowsForms10.STATIC.*` children, buttons the
  visible `Button` and `WindowsForms10.BUTTON.*` push buttons (`&` removed; `DialogClasses`). A WPF window has no child windows and a Windows
  task dialog (DirectUI) draws its own: title only. UI Automation is not used (called from the UI thread that owns the window it can
  deadlock). `since` is when the window handle was first seen at a tick (a caption that changes, such as a progress counter, does not restart
  it, or such a dialog would never become blocked). If the main window is disabled and no window can be picked (a modal window of a kind the
  scan does not know) the entry is a dialog titled `(unknown modal window)` with no class, texts or buttons, keyed on a constant handle so the
  same 15 s clock runs: AutoCAD cannot take input then, and "idle" would be wrong.
- **Text.** Every text goes through `TextUtil` (one line, replaced lone surrogates, cut without splitting a surrogate pair) and every string of
  the JSON through `TextUtil.Sanitize`: `Utf8JsonWriter` throws on a lone surrogate, the file would stop being written and every reader
  would see the writer as busy. Window text can hold anything.
- **State rules** (`StateRules.Decide`): dialog open 15 s or more: `blocked`; any other dialog or a pending prompt: `waiting-input`;
  `CMDACTIVE != 0`: `running`; else `idle`. `since` of `blocked` and of a dialog's `waiting-input` is the moment the dialog was first seen.
- **Merge** (`StatusMerge`) follows CONTRACT.md. Readers read only `^[a-z0-9][a-z0-9-]{0,39}\.json$`; a file that lacks a required field or
  has a `v` that is not an integer >= 1 is skipped (listed as "skipped" in the dump). A stale entry whose pid lives ranks as `busy`
  ("No reply" in the pane), one whose pid is gone is ignored; a `stopped` entry is never `busy`. Unknown sources are shown like any other.
  "Alive" means `ProcessProbe`: a process runs under the pid and, when the file has `pidStart`, started within 2 s of it (Windows reuses
  pids). It opens the process with `PROCESS_QUERY_LIMITED_INFORMATION` and reads the creation time with `GetProcessTimes`, which works for
  most processes `Process.StartTime` cannot open (SYSTEM, other sessions). If even that is denied the state is `Unknown`: alive, except
  for a file that states `pidStart` and has been stale for over an hour (a writer silent that long is not the protected process that now
  holds its pid). Lookups go through a `LivenessCache` (one per (pid, pidStart) per 5 s) and only for stale entries.
  Our own entry comes from memory (`AcadStatusDoc.Entry`), not from the file, so the pane still works when the folder cannot be written and
  the tick never parses what it just built. The file text is rendered only when it is written or dumped.
- **The file.** `ttlSec` 10, `pidStart` written; written when the content changed or 1.5 s after the last write (with a 1 s timer that is
  every second tick, so about 2 s apart, never 3). `updated` is whole seconds, so a reader sees it up to a second old; the ttl leaves
  room for several missed ticks. **Clean-up** (on start and every 10 minutes): any status file whose pid is gone or reused and whose
  `updated` is over an hour old is deleted.
- **Bounds.** A file whose `updated` is before 2000 or more than a day ahead, or whose `ttlSec` is outside 1..86400, is skipped (an extreme
  date would make `updated + ttl` throw); `StatusMerge` also guards each entry, so one that cannot be ranked is listed as ignored and the
  others are still merged. The writer writes at once when the clock went backward. `prompt` is written as `""` for a pending prompt of
  unknown text (absent only when none is pending).
- **No work on the UI thread that can wait.** The folder is listed and read by a background task (`LatestRead<FolderRead>`, one at a
  time, `StatusFiles.Read` is thread-safe); a tick merges what the last task found, at most one tick old, and kicks the next. The dump reads
  in place. The folder is created once at start (`EnsureFolder`, with its permissions) and again only after a `DirectoryNotFoundException`;
  `AtomicFile` makes one try and never sleeps (the retry is `WriteRetry`, at the next tick), and "Copy for Claude" makes one try at the
  clipboard and says "clipboard busy, try again".
- **Sharing.** The reader opens a status file with `FileShare.ReadWrite | FileShare.Delete`, reads it in one go and closes it at once
  (`StatusFiles.ParseFile`), so a writer's replace never fails because the pane is reading. A failed write of our own file is caught inside
  `StatusService.Write`: the key and time of the last good write stay as they were, so the next tick tries again, and `WriteRetry` backs off
  (2 s after the third failure in a row, then 4, 8 and at most 10 s); one success starts over. A failing write never fails the tick.
- **The pane** is two adjacent `Autodesk.AutoCAD.Windows.Pane`s on `Application.StatusBar.Panes`: a dot (a 16x16 icon drawn once per colour)
  and a text pane (at most 18 characters, `Visible = false` while idle). Facts learned on AutoCAD 2026 in the VM test, each guarded by
  `LayerRulesTests.ThePaneKeepsToWhatAutoCad2026AcceptsOnAStatusBarPane` or a comment in `StatusPane.cs`:
  - A pane shows its icon **or** its text, never both: setting `Text` hides the `Icon`. Hence two panes, which the bar draws as one cell.
  - `MinimumWidth` / `MaximumWidth` must not be set: AutoCAD's managed layer raises a modal **".NET Assertion Failed"** dialog ("Unknown
    Property 4 / 5") that blocks AutoCAD, and, because it is raised inside our tick, our own timer with it. Panes size themselves.
    Set only `Icon`, `Text`, `ToolTipText` and `Visible`.
  - `MouseDown` is raised only for a pane of style `PaneStyles.Command` (a `Normal` pane never raises it).
  - `PointToScreen` is relative to the status bar (`(0,0)` is the bar's top left, so `.Y` is the bar's top), and the event's `X`, `Y` are relative
    to the pane. The click's screen position is `Cursor.Position`.
  - `Panes.Contains` / `Panes.Remove` throw a `NullReferenceException` while AutoCAD shuts down (logged once, harmless).
  - A click on the status bar while another window (our details window) is active only activates AutoCAD: no `MouseDown` arrives. The
    details window therefore also closes on `Deactivate`.
  The first tick that finds the status bar adds the panes. `ACADSTATUSPANE` removes and re-adds them and keeps the choice in
  `%LocalAppData%\Gladmill\AcadStatus\settings.json`.
- **The details window** is a WinForms form shown with `Application.ShowModelessDialog` (not a palette). It closes on Esc, on `Deactivate`, and
  on a second pane click. `ClickGuard` tells a `MouseDown` that arrives after a `Deactivate` close that it belongs to the same click by
  the identity of the Windows message (`GetMessageTime`), not by elapsed time. The text is rebuilt only when what it says changes: the
  ages (`EntryBlock.Timing`) are replaced in place, the two fonts are made once, and the window is placed by `Placement.Above` inside the
  working area of the screen the click is on (also when it grows).
  "Copy for Claude" uses Win32 `OpenClipboard` / `SetClipboardData` (`Ui/ClipboardText.cs`): `Clipboard.SetDataObject` failed inside AutoCAD
  on the VM ("Requested Clipboard operation did not succeed") while it worked in another process; it stays as the fallback.
- **Terminate** writes `stopped`, removes the pane, closes the window and unhooks the editor events.

## Decisions to know about

- A stale `stopped` entry whose pid lives stays `stopped` (never `busy`, CONTRACT.md); its pid being gone makes it ignored like any stale entry.
- The folder is created with a rule that lets all users of the machine write to it (so two Windows users can both run AutoCAD). Not tested
  with two users.
- The clean-up deletes any status file (not only `acad-*` and `retrace-*`): the contract says so. A file whose writer does not write
  `pidStart` can still look alive when another process took its pid (seen on the VM before `pidStart` existed); for a stale `stopped` entry
  that is harmless, for another stale entry it would show a false "no reply" until the file is an hour old and the pid is free.
- The state of our own entry comes from the in-memory snapshot, not from reading the file back.

## VM tests

The VM tests of the pane and the dialog file ran on a Windows VM with AutoCAD 2026 that can be reached over ssh, using Retrace's VM scripts
(`tools/vm` in the Retrace project) and a small tool that sends keystrokes. Results are in the pull request. To repeat them: if several people
share the VM's one AutoCAD, take turns, close AutoCAD, copy the bundle from `scripts/pack.sh` to `%APPDATA%\Autodesk\ApplicationPlugins\`,
run the trust script, start AutoCAD, then send `(alert "status board test")` with the keyboard (not COM `SendCommand`, which blocks while a
dialog is open), read `%ProgramData%\Gladmill\acad-status\acad-<pid>.json` and take screenshots.

## Safety

- Status files hold drawing names, command and prompt text and dialog text. Never commit real ones; tests use synthetic data only.
- The add-in never closes, presses or sends a message to any window (`LayerRulesTests.TheWriterNeverTouchesADialog`).
- No secrets, no keys, no personal or company names in code, docs or fixtures.
