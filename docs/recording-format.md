# Recording format, schema version 2

A recording is one JSON Lines file, `recording.jsonl`, in its own test folder. UTF-8, one JSON object per line, flushed after every line:
a crash loses at most the line being written, and readers skip an unparsable last line. Fields with no value are left out, never `null`.
Retrace writes it (`Core/SessionWriter.cs`, `AutoCad/Recorder.cs`), replays it (`Core/Plan.cs`, `AutoCad/Replayer.cs`) and turns it into `steps.md` and the recordings docs (`Engine/Recordings/`).

## Test folder layout

The **tests folder** (setting `testsFolder`, default `C:\temp\retrace\tests`) holds one folder per recording. The folder is created when recording starts.

```
<tests folder>\
  <yyyy-MM-dd_HHmm>_<name>\             e.g. 2026-10-10_0243_dtest
    recording.jsonl                     the recording (this schema); RETRACERUN and RETRACEREPLAY replay it
    steps.md                            the test as steps for a person; written at RETRACESTOP, rewritten from scratch each time, do not edit
    steps.docx                          optional (setting exportDocx): Word version of steps.md; replaced each time
    steps.xlsx                          optional (setting exportXlsx): one row per step with a PASS / FAIL column, plus an Info sheet
    .steps.docx.retrace, .steps.xlsx.retrace   hidden: the size and time of the file as Retrace wrote it, or "kept" (below)
    screenshots\                        PNGs; every `shot` event's `file` is relative to recording.jsonl ("screenshots/step-02-click.png")
      unused\                           pictures steps.md does not show (kept, out of the way)
    snapshots\                          what the drawing held for each behavior at the start and at each checkpoint (schema 3, see "Behavior checks")
      start.json, mark-<n>.json         <n> = the `n` of the `mark` event
    results\
      <yyyy-MM-dd_HH-mm-ss>_<VERDICT>.json   one per replay run (see "Result file")
      latest.json                       copy of the newest run, for scripts
```

- The folder name is the header `started` (local clock), then `_`, then the name. The name is the header `name` (letters, digits, `-` and `_`; every other character becomes `-`). A folder that exists already gets `_2`, `_3`. Folders sort by date.
- Readers look for `<tests folder>\*\recording.jsonl` one level down. Folders whose name starts with `.` are skipped. "Newest" means the latest header `started`, else the file time. Any other file name is not a recording.
- If steps.docx or steps.xlsx is open in Word or Excel when it is rewritten, the new file is called `steps (new).docx` / `steps (new).xlsx` and a note says so.
- Saving checkpoint names rewrites steps.md and the Word / Excel files, except a Word or Excel file changed since Retrace wrote it (a tester may have filled in results): that one is kept, marked `kept` in its `.retrace` file so later saves keep it too, and named in a warning. Without a `.retrace` file (written by an older version), a file more than 2 minutes newer than steps.md counts as changed.

## What writing the steps does to a test folder

`RETRACESTOP` calls the engine (`EngineRunner.WriteSteps`, `Engine/Recordings/ScriptWriter.cs`, `ShotNamer.cs`) on a background thread:

- It writes `steps.md`, with a short "Files in this folder" table after the introduction (what each file is for, and how to run the test automatically).
  It ends with a `## Result` section (one `[ ] pass  [ ] fail` line per checkpoint and an `Overall:` line). There is no sign-off line.
- With `exportDocx` / `exportXlsx` (or `EngineRunner.Export`, used by the Open test window) it also writes `steps.docx` / `steps.xlsx` from the written `steps.md`
  (parsed with Markdig; `StepsDoc.cs`, `ExportDocx.cs`, `ExportXlsx.cs`, `Exporter.cs`; DocumentFormat.OpenXml). In the docx: headings 1-3, paragraphs with bold / italic / inline code,
  nested bullet and numbered lists (the numbers are text), tables, pictures embedded from the relative `screenshots/...png` links (scaled down to at most 16 cm wide, aspect kept; a missing picture
  becomes the text `[picture not found: ...]`), `[ ]` / `[x]` as a ballot box. In the xlsx, sheet `Steps`: `Step | Action | Details | Check | Screenshot | Result | Notes` (Details = the step's sub-items, one per line; Check = the expected text of a
  checkpoint step; Screenshot = the file name of every picture of the step, one per line, in script order (a cell holds one hyperlink: it opens the first); Result = list PASS,FAIL; header bold and frozen, text wrapped), sheet `Info`: test name, recorded line, start-with line, step count, folder.
  Both are written to a temp file and moved over the target.
- It renames each picture the script shows to `step-NN-<what>.png` (NN = the script's step number, two digits, three from 100 steps; a clash gets `-2`, `-3`):
  `click` (ribbon click), `command` (command start), `dialog-before`, `dialog-after`, `pick-K` / `enter-K` / `answer-K` (the K-th answer picture of the step, by how the
  answer was given: mouse pick, typed, unknown), `end` (command end), `cancelled` (command cancelled), `check` (checkpoint).
- It moves pictures in `screenshots\` that the script does not show to `screenshots\unused\`.
- It rewrites `file` in the matching `shot` lines of `recording.jsonl` (temp file, then replace; every other line and byte stays) and only renames when the name differs,
  so running it again changes nothing. If a line cannot be rewritten nothing is renamed and a warning is printed.

## Schema versions

| Version | Change |
|---|---|
| 1 | The original format. |
| 2 | Drawing digests carry `layerState` and `currentLayer` (see "Drawing digest"). Nothing else changed: a schema 1 recording is valid schema 2 with digests that lack these fields. |
| 3 | Behavior checks: `mark.checks`, the `snapshots` folder (see "Behavior checks"). Written by the recorder since it records behavior checks (the header says 3); the readers understand `checks` in any recording. In a mark that has `checks`, the digest's `layerState` is not compared (the `layers` behavior owns it). |

Schema 1 and 2 recordings replay as before, with one exception: the current layer (`currentLayer`) is no longer compared in any recording; a test fails on it only if it checks `CLAYER` in the `sysvars` behavior. The field is still read.

Nothing in the plugin rejects a schema value; readers ignore fields they do not know and tolerate missing ones.

## Header (first line)

| Field | Meaning |
|---|---|
| `type` | `"header"` |
| `schema` | `3` (recordings made before layer state was added say `1`; readers accept 1, 2, 3 and a missing value) |
| `name` | test name given to `RETRACERECORD`, with every character other than letters, digits, `-` and `_` replaced by `-` (`test` if empty) |
| `started` | local start time, ISO 8601 |
| `autocad` | `Application.Version`, e.g. `25.1.0.0` |
| `product` | `PRODUCT` system variable, e.g. `AutoCAD 2026` |
| `locale` | `LOCALE` system variable |
| `workspace` | `WSCURRENT` system variable |
| `plugin` | `Retrace` and its informational version |

## Events (all other lines)

Every event has `t` (ms since the recording started), `n` (the event's number: 1, 2, 3, ... per recording, in file order; the header has none), `type`, and `doc` (file name of the active drawing, no folder) when a drawing is active.

| `type` | Extra fields | Source event |
|---|---|---|
| `ribbon` | `id`, `text`, `name`, `macro` (CommandParameter), `itemType` (class name, e.g. `RibbonButton`, `RibbonSplitButton`), `tabId`, `tab`, `panelId`, `panel`, `path` (position inside the panel, split-button submenus as `Panel > Button`), `parent` (split button / menu the item was in), `activeTab` | `ComponentManager.ItemExecuted` |
| `tab` | `id`, `title` | `RibbonTab.Activated` / `RibbonControl.ActiveTab` change |
| `cmd_start` | `command` (global name), `origin`, `via` | `Document.CommandWillStart` |
| `cmd_end`, `cmd_cancel`, `cmd_fail` | `command` | `CommandEnded`, `CommandCancelled`, `CommandFailed` |
| `cmd_unknown` | `command` | `UnknownCommand` |
| `lisp_start` | `firstLine`, `startup` (true: LISP AutoCAD runs while a drawing opens, such as acaddoc.lsp or S::STARTUP, with no click or key since it opened; not a step) | `LispWillStart` |
| `lisp_end`, `lisp_cancel` | | `LispEnded`, `LispCancelled` |
| `prompt` | `kind`, `seq`, `input`, `by` (`mouse` or `keys`, with `input: true`), `message`, `keywords` (parsed from the message), `status`, `value`, `keyword` | `Editor.PromptingFor*` / `PromptedFor*` |
| `view_stale` | `for` (the number of a `prompt` event) | recorder timer: the view stored with that pick or click may have changed before it; readers drop its `view` (see "View and pick points") |
| `modal_enter` | `title`, `windowClass` of the foreground window at that instant | `Application.EnterModal` |
| `modal_title` | `title`, `windowClass`, sent 300 ms after `modal_enter` if the foreground window changed (the dialog often is not foreground yet at `EnterModal`) | |
| `dialog_values` | `for` (`n` of the dialog's `modal_title` event, else of its `modal_enter`), `title`, `source` (`win32` or `uia`), `fields` (only the fields that changed: `key`, `label`, `kind`, `before`, `after`), `read` (how many fields were read), `closedWith` (`ok` or `cancel`; left out when the dialog was closed some other way) | written when the dialog closes, just before its `modal_leave`; see below. Only with setting `dialogValues` on (default). `read: 0` = nothing could be read |
| `modal_leave` | | `Application.LeaveModal` |
| `palette_values` | `palette` (the palette's name: its tab in the set), `set` (the palette set's title), `source` (`win32` or `wpf`), `fields` (as in `dialog_values`), `read` | the user changed fields in a palette set and then clicked a push button in it (written just before that `palette_click`), moved to another palette, or did something outside it; also before a checkpoint and at Stop. Only with setting `dialogValues` on. See "Palette sets" |
| `palette_click` | `palette`, `set`, `button` (the push button's text), `index` (only when several enabled buttons of the palette have that text: which one, 1 = the first) | a click on a push button in a palette set, or Space on a focused WinForms push button (read before the button acts) |
| `native_click` | `palette`, `set`, `control` (`tree` or `button`), `action` (tree: `click`, `dblclick`, `rclick`; button: `click`), `path` (tree: the item's texts from the top level down), `index` (which control of that kind in the palette, 1 = the first), `id` (the control's id), `label` (button: its tool tip, when it has one) | a click in one of AutoCAD's native palettes (no .NET content, such as AutoCAD Electrical's Project Manager) on a tree item (not its +/- button) or an enabled push button. A double-click is a `click`, then a `dblclick` on the same item. See "Native palettes" |
| `native_select` | `palette`, `set`, `control` (`combo`), `index`, `id`, `value` (the entry now shown) | the person clicked a drop-down list in a native palette, and its list has closed showing another entry |
| `doc_active`, `doc_created`, `doc_closing` | `doc_created`: `path` (full path of a drawing opened from a file) | `DocumentActivated`, `DocumentCreated`, `DocumentToBeDestroyed` |
| `state` | `state` (a drawing digest) | `RETRACERECORD`: written right after the header, the drawing as it was when recording started. Readers treat it as session metadata, not a step |
| `mark` | `note` (the checkpoint's name: the automatic one, or what the person typed in the Checkpoints window or at `-RETRACEMARK`; empty in older recordings), `autoName` (the automatic name, `checkpoint<n>` plus where the last action happened: `_<dialog title>_button_<ok\|cancel>`, `_<ribbon panel>_button_<button text>`, `_<palette>_button_<button text>` (`_<palette>_palette` when only values changed) or `_cmd_<command>` (`_open_<file name>` after a drawing opened outside a command), in lowercase words joined by `_`; with the checkpoint shortcut and the cursor on a palette control or a ribbon item, `_<palette or panel>_<control>` instead; missing in older recordings), `state` (a drawing digest, left out if the drawing could not be read), `checks` (schema 3: what the checkpoint verifies besides the digest, see "Behavior checks"; left out when none were captured), `near` (the shortcut only: what was at the cursor, in a sentence: the control under it or nearest it, its value, and up to three controls near it; left out when the cursor was not on a palette or the ribbon) | `RETRACEMARK`, `-RETRACEMARK`, the checkpoint shortcut (setting `checkpointKey`) |
| `shot` | `for` (the `n` of the event the picture belongs to), `file` (path of the PNG relative to the recording file, forward slashes: `screenshots/012-ribbon.png` as recorded, `screenshots/step-02-click.png` after the steps are written, `screenshots/unused/004-prompt.png` for a picture the steps do not show), `what` (`window` = AutoCAD's main window, `dialog` = the dialog in front, shortly after it opened; `dialog-after` = the same dialog at the moment OK or Enter was pressed, file `NNN-dialog-after.png`, one per dialog, only while dialog values are on) | a screenshot taken after the event, see below. Written some 100 to 300 ms after the event it belongs to, so it sits later in the file |
| `session_end` | | `RETRACESTOP` |

### Screenshots (`shot`)

With the setting `screenshots` on (default; `RETRACESETTINGS`, `settings.json`) the recorder saves PNGs into `screenshots\` next to `recording.jsonl`,
named `<NNN>-<kind>.png` where NNN is the `n` of the event zero-padded to three digits and `kind` is:

| Event | kind | picture of |
|---|---|---|
| `ribbon` | `ribbon` | main window after the click |
| `cmd_start` | `cmd-start` | main window |
| `prompt` with `input: true` | `prompt` | main window after the real click or key press. Scripted input has no `true` flags and gets no prompt pictures; its command's `cmd_end` picture covers it |
| `cmd_end`, `cmd_cancel`, `cmd_fail` | `cmd-end`, `cmd-cancel`, `cmd-fail` | main window after the command finished: the result |
| `modal_title` (else `modal_enter` if no title arrives) | `dialog` | the dialog: the foreground window of AutoCAD's process at its DWM extended frame bounds |
| `mark` | `mark` | main window at the checkpoint |

Pictures are queued by the event handler and taken later (timer / `Application.Idle`), never inside an AutoCAD event handler. At most one picture per target (main window, dialog) per 250 ms; a request
of the same kind inside that gap is dropped as a duplicate, one of another kind waits; a request older than 1.5 s is dropped. So an event can lack a picture. A window picture is scaled
down to at most 1400 px on its longest side. A dialog that closed before the picture was due is not shot. Failures go to `retrace.log` in the root folder and never stop the recording.
The throttle state is reset when a recording starts (each recording's clock starts at 0). `RETRACESTOP` takes the pictures still queued before it writes `session_end`.

The `dialog-after` picture is taken when OK is clicked or Enter is pressed. In a WPF dialog (no child windows) it is taken on Enter only; a mouse click on OK is not recognised yet.
The script shows the after picture when values changed, also if the "before" picture is missing.

### Dialog values (`dialog_values`)

The recorder finds the dialog window (the foreground window of AutoCAD's process once it is not the main window), reads its fields when it is found
(the initial values), again every ~150 ms, and again when a click or Enter reaches a window inside it. On close it compares the first value it saw per field with the last.
`fields` holds only the changed ones; an event with empty `fields` and `read > 0` means "looked, nothing changed". Nested dialogs each get their own event.

- `kind`: `edit`, `check` (`on`/`off`/`mixed`), `radio` (`before`/`after` = text of the checked radio of the group; `label` lists the group's radios as `A / B`), `combo`, `list`, `slider`.
- `key`: `name:<control name>` for a WinForms control with a unique `Name` (stable between runs; WinForms window ids are not), else `id:<control id>` if the id is non-zero and unique in the dialog, else `label:<label>#<occurrence>`. `label` is the control's own text (check, radio), else the nearest static text left of it on the same row or just above it, else `#<control id>`.
- `source` `win32`: child windows (stock AutoCAD dialogs, DCL, WinForms, MFC). `uia`: no readable child windows (WPF), read through UI Automation on a pool thread (at most 200 elements, 1 s); a radio is then one field per button with `on`/`off`.
- Values are cut at 200 characters. Password edits and read-only edits are skipped.
- Readers: events without a matching `for`, or recordings without the event, change nothing.

### Palette sets (`palette_values`, `palette_click`)

A palette set is not a dialog: there is no modal phase to watch. The recorder looks at every click and key press (`PreTranslateMessage`): if it lands
in a palette with .NET content (WinForms or WPF windows under AutoCAD's palette windows: palette, palette set, control bar), it reads that palette's
fields (the initial values), again every ~300 ms while the user works in it, and once more at a push-button click or when input goes elsewhere. It
then writes the changed fields (`palette_values`, nothing if none changed) and the click (`palette_click`). Readers make one step of a
`palette_values` followed directly by a `palette_click` of the same palette.

- `source` `win32`: the fields are child windows (WinForms), read like a dialog; WinForms check boxes and radio buttons are asked through WinForms.
  `wpf`: read in process from the WPF visual tree (text boxes, check boxes, radio buttons, combo boxes, sliders); the label is the control's
  automation name, its own text, a `Label` that targets it, the `Label` or `TextBlock` before it in its panel, else its `x:Name`.
- Not read: AutoCAD's native palettes (no .NET content), the ribbon (hosted the same way, ruled out by its window), and Retrace's guide palette.
- Pictures: besides the window picture (`<n>-palette.png`), a palette step gets a `shot` with `what` `"palette"` (file
  `<n>-palette-closeup.png`, named `step-NN-palette.png` in steps.md): the palette's control bar as it looked while open, taken when the button
  was pressed, else the last one taken while the user worked in it (taken again when its values change). Only with setting `screenshots`. The
  recorder never opens a palette to take it.
- Labels: a WinForms control's `AccessibleName` is its label when set; else the static text left of or above it. Give fields next to text that
  changes (a status line such as "21 parts") an `AccessibleName`, or the steps name them after that text.
- Keys: `name:<Name>` for a WinForms control's `Name` or a WPF element's `x:Name` when unique; else as for dialogs. Labels: a WinForms control's
  `AccessibleName`, a WPF element's `AutomationProperties.Name`, else the text next to it.
- Replay (`RETRACERUN`): finds the palette by `set` and `palette` (also while it is auto-hidden), sets the recorded values the palette does not
  show yet (matched by name, else like dialog values), checks them, and presses the button (the `index`-th enabled one with that text).
  WinForms: values through the controls (a NumericUpDown through its `Value`), a posted click. WPF: in process through the visual tree; a
  TextBox's binding is pushed to its source at once, a list or combo item is chosen by the text it shows (its data template too), the button is
  pressed through its automation peer (Click handlers and its Command run). A press that runs code at once is queued, never run inside the
  replay's own tick. A step that cannot be done yet (a list still empty because an async call has not answered, a button still disabled) is
  tried again every 0.5 s for up to 15 s. If the button started a command, the recorded answers are typed into it once it runs (a command
  without prompts may already be done; it may start up to 30 s after the press). Check boxes are toggled the way a click does (their Click
  handlers and commands run). A field that already shows the recorded value is not set again: its change handler does not run, so a palette
  left open from an earlier run keeps what that run did. A field that rewrites what is typed (upper case, a format) never shows the
  recorded text and needs a person. A missing palette, field or button after that: a person does the step and clicks Next; unattended
  (`RETRACEREPLAY ... Auto`) the run stops as `INCOMPLETE`.

### Native palettes (`native_click`, `native_select`)

AutoCAD's own palettes have no .NET content, so `palette_values` / `palette_click` never see them; standard Windows controls in them are
recorded instead (`AutoCad/NativePalette.cs`): tree items (`SysTreeView32`) by their path of texts, push buttons by id and position (named
by their tool tip), drop-down lists (`ComboBox`) by the entry picked. Readers make each a palette step (`Engine/Recordings/NativeAction.cs`);
a `click` then a `dblclick` on the same item within 2 s is one double-click step, and a drawing opened right after a double-click (a
`doc_created` with `path`) belongs to that step instead of being an "Open drawing" step. A command that starts after the click (origin
`palette`) is part of the step; after a right-click it is the command picked from the context menu. A modal window that opens within 3 s
and closes by itself (its `dialog_values` read nothing and saw no OK or Cancel, such as AutoCAD Electrical's "Updating...") is not a step;
one the person answers is. A drop-down list is watched from the person's click in it while it has the focus; a pick is written at the
click on the entry in its list (`entry`), and an owner-drawn list that holds no text (Electrical's project list) gets the entry's name
in a later `native_value` (`for`, `value`), read from the palette tree's bold item once the switch is done.

Replay finds the palette by name and set, the control by id (when unique) or position, then acts in process, from the dispatcher right
after its tick (what the action opens then runs while the replay goes on; an unattended replay waits up to two minutes for a dialog with no
push button, a progress window): a tree item is found by its
path (expanding its parents), selected, and given the mouse messages sent to the tree at the item, with the mouse moved there (AutoCAD
Electrical opens a drawing only so; posted messages did not open it); a right-click only selects the item and then types the command
picked from the menu; a button gets `BM_CLICK` (a disabled one is retried, as palette buttons are); a drop-down list is opened and the entry clicked in its list
(by text, or by position for a list without text, then checked against the palette's bold item; Electrical ignores `CB_SETCURSEL` with
change notifications). A command the action starts with nothing to answer is not waited for (Electrical's `AEMTRELOAD`). After a double-click that opened a drawing the replay waits up to 20 s for it, goes on in it, and opens it itself
if the click did not (a warning in the result). Not recorded: keys in a native palette, the context menu itself, other control kinds, and
native palettes that draw their own controls.

### Drawings opened outside a command (`doc_created` with `path`)

AutoCAD Electrical's Project Manager (a native palette, so its tree clicks are not recorded), Explorer or a plugin can open a drawing
without a command. Readers make an "Open drawing" step of a `doc_created` that has a `path` and came while no command or LISP ran;
inside a command (OPEN, NEW) the command's step covers it. A replay opens that file (an already open copy only if it has no unsaved
changes) and goes on in it, once its start-up has been quiet for a second. The LISP AutoCAD runs while a drawing opens is not a step:
`lisp_start` with `startup: true`, everything up to its `lisp_end`. Recordings made before `startup` existed count LISP within 10 s of
the open, before the person acted, as start-up (`Engine/Recordings/DrawingOpen.cs`). Switching between open drawings (`doc_active`) is
not replayed.

### `origin` on `cmd_start`

`"ribbon"` if a `ribbon` event happened at most 2000 ms earlier, was not yet matched to another command, and its `macro`
contains the command name (ignoring `^C`, `_`, `.`, `-` prefixes); `via` is then that item's `id`. Else `"palette"` if a
`palette_click` happened at most 30 s earlier, was not yet matched, and no other click or key press came in between (a palette button that
runs a command with `SendStringToExecute`, also after an awaited call); `via` is then the button's text, and readers make the command and its answers part of that palette step.
Otherwise `"typed/other"` (typed, a toolbar, a LISP call, a script...). Commands that start while another one runs (transparent, nested) are
recorded the same way; readers treat a `cmd_start` before the matching `cmd_end` as nested.

### `prompt`

`seq` numbers the `PromptingFor*` event the result belongs to. `input` is true when a mouse-button or key press reached AutoCAD shortly
before the result (and no earlier result claimed it), false otherwise: in AutoCAD Electrical mouse movement alone produces a stream of
`input: false` results, which replay and the steps drop in steps that also have `input: true` results. `keywords` are parsed from the bracketed part of `message`.

`kind` is one of `point`, `corner`, `distance`, `angle`, `double`, `integer`, `string`, `keyword`, `entity`, `nestedEntity`, `selection`.
`message` is the prompt text from the options (what `Editor.PromptingFor*` saw). `status` is the `PromptStatus` name:
`OK`, `Keyword`, `None` (Enter), `Cancel`, `Error`, `Other`, `Modeless`. `value` is set only when `status` is `OK`:

| `kind` | `value` |
|---|---|
| `point`, `corner` | `[x, y, z]` in WCS (the UCS result transformed by `CurrentUserCoordinateSystem`) |
| `distance`, `double`, `integer` | a number |
| `angle` | a number, radians |
| `string` | the text |
| `entity`, `nestedEntity` | `{"handle":"2F","dxf":"LINE","pick":[x,y,z],"view":{...}}` (`pick`: the point where the object was picked, in the current UCS, as it is typed; `view`: the view it was picked in, see "View and pick points" below; `view` is missing in recordings from before it was kept) |
| `selection` | `{"count":3,"handles":["1A",...],"dxf":["LINE",...],"method":"Window","typed":"w\n0,0\n9,9\n\n"}` (handles and dxf names capped at 200; `method` is the first object's `SelectionMethod`; `typed`, only when text was typed for it, `clicks` and `view` when the mouse was used: see "Selections" below) |

When `status` is `Keyword`, `keyword` is the keyword text the user chose.

### System variables typed by name, and strings with spaces

**A system variable typed at the command line** (`CTAB`, then `A-101 Plan`) runs as `SETVAR`. The recording has `cmd_start` `SETVAR` and one `prompt` with the value prompt only, whose `message` names the variable (`Enter new value for CTAB <"Model">:`); the "Enter variable name" prompt raises no event. The replay reads the name from that message and types it before the value (`_SETVAR`, `CTAB`, `A-101 Plan`). Nothing new is recorded, so older recordings replay the same way. Only the English message is recognised. A step with a recorded name prompt (any language: two or more answers, the name first) replays exactly as recorded, and a ribbon macro that already types the name (`^C^Cctab;`, `^C^C_setvar ctab;`) gets no second one. When a SETVAR step has a single answer that is not an English value prompt, nothing says which variable was set: the replay does not type the value (it would go where the name goes), says so on the command line, records the step as `needs-person` and ends the run `INCOMPLETE`; the `.scr` export writes `; MANUAL: ...`. A variable that has its own command (`LTSCALE`) is recorded and replayed as that command. The how-to and guide text says "At the prompt 'Enter variable name', type CTAB." exactly when the replay types the name.

**A string answer with a space** (a layout name such as `A-101 Plan` in `LAYOUT Set` or `CTAB`). Typed by a person, the prompt takes the space as text and the `prompt` event holds the whole string. Sent with `SendStringToExecute` (and typed by a script), AutoCAD takes the space as Enter, also at a prompt that takes text, and a LISP expression sent to such a prompt is taken as text (checked in AutoCAD 2026: `A-101` was set, then `Plan` was left over; `(identity "A-101 Plan")` was refused as a layout name). What works is a LISP `(command ...)` call, whose strings keep their spaces. A step of a built-in command (kind `command`, or a ribbon item) that has a `string` / `keyword` answer with a space is sent as one form: `(command "_LAYOUT" "Set" "A-103 Details")`. An Enter is `""`; `"` and `\` are escaped; every string and keyword answer is a quoted literal (so `(Old) Ground Floor` stays text); only the `(handent "..")` made for an entity or a selection goes in as an expression. The leading `^C^C` of a ribbon macro is sent in front of the form, and its words are the first arguments. The same line goes into the `.scr` export. The form is not used for a LISP step, a command recorded as opening a dialog, the answers after a dialog (`InputAroundDialog`), or a macro with a quote, a parenthesis, a backslash or an Esc inside. Such a step is not typed: `Plan.Problem` says why, the replay stops there as `needs-person` (never PASS), and the script writes `; MANUAL: ...`.

### Selections

The rule: **a selection replays the way it was made. Handles are the last resort.** Handles differ between the recorded drawing and a replay drawing, and the
attributes that `-ATTEDIT` selects are reported as their insert (the same handle seven times), which `(handent)` cannot turn back into attributes.

What AutoCAD 2026 raises for the selection of a built-in command (checked with `ERASE` and `-ATTEDIT`, typed input): **one `selection` result only**, with handles and `method` `Unavailable`.
Its window corners, `w` / `c` keywords and picks raise no event that is recorded (the point events exist but arrive nested inside the selection and are dropped, like every nested prompt).
So the recorder keeps the text typed since the last answer (`Core/TypedInput.cs`: Enter, Backspace and the characters of the keys on a US layout; Esc is ignored) and stores it in the selection's value as `typed`:

```json
{"kind":"selection","status":"OK","value":{"count":2,"handles":["8E","8A"],"dxf":["LINE","LINE"],"method":"Unavailable","typed":"w\n-1,5\n30,30\n\n"}}
```

The replay types the lines of `typed` (`w`, `-1,5`, `30,30`, Enter), so the same window is made again. The Enter that ends a selection may come as its own `selection` result with status `Error` or `None` and `input: true`; the replay types it as Enter, once (`Core/SelectionReplay.cs`).
`typed` is stored **only when the typed text alone makes the whole selection** (`Core/TypedSelection.cs`): the recorder also counts the points that arrived and the text must hold a point line for each, and something other than Enters. `typed` is text the person typed and nothing else.
**Mouse clicks go to their own field, `clicks`.** The point and corner prompts of a selection do raise their results (nested, so no event of their own), and `PromptPointResult.Value` is the exact click. A point is typed exactly when the keys since the last point end with a completed point line; otherwise it is a click if a left button went down or up in the drawing window (not the ribbon, a palette or the command line) since the last click and the newest input was the mouse; a point that is neither spoils the text. GetCorner's inner point is skipped by the prompt nesting (not by value), so two clicks at the same spot count twice.
`clicks` is the keys and the clicks in order, the clicks as point lines `x,y[,z]` (current UCS, fixed notation rounded to 1e-8, never an exponent). **`view` is stored with it**: the view the clicks were made in ("View and pick points" below). It is left out when a click was made with Shift or Ctrl (deselect, sub-object, cycling pick), when a half-typed line came before a click, or when a transparent or own command ran inside the prompt. A selection with a click has no `typed`.
**Handles stay the first choice for a mouse selection** (they do not depend on the view, the pickbox or the window size). The replay types `clicks` instead only where handles cannot say what was selected (`SelectionReplay.UseClicks`), or where the click says which part of the object is meant (TRIM and EXTEND, below): a handle repeats (the insert of each attribute that `-ATTEDIT` selects), the list holds an attribute (`ATTRIB` / `ATTDEF`), the list was cut short (`count` above the handles kept), or there are no handles. Typing a point at `Select objects:` picks the object under it, and two points make a window or crossing by their direction, as with the mouse. When the last click completes the selection (`-ATTEDIT`), the Enter that ends it comes later as its own `selection` result, which the replay types as Enter. The guide (`steps.md`, Tdoc) says "select the objects and press Enter" for such a selection and never prints coordinates.
Space counts as Enter (AutoCAD takes it so at selection and point prompts). Text after the last Enter is not replayed. The text is also unusable, and left out, when a transparent command, one of our own commands or a LISP expression ran inside the selection prompt.
A LISP expression typed at the command line (`(ssget)`) starts the text over, a command start does too. Any answer that is not selection input ends the text: a string, a number, a point at a prompt whose text is not a selection prompt ("Specify base point:"); a point at "Select objects:" or a corner does not.
The how-to text and the guide (`steps.md`, Tdoc) describe the same answers: the echo result is left out, the Enter that ended the selection reads "Press Enter", and a selection with `typed` reads "Type `w`, `-1,5`, `30,30` and press Enter".
A `selection` result with status `Keyword` is typed as its keyword. The Enter is not typed twice when the typed text already ends with the closing Enter, and a second `selection` result after an echo is kept.

| How the selection was made | Recorded | Replayed |
|---|---|---|
| typed window / crossing (`w`, `c`, implied window), polygons, fence, `all`, `previous`, picks by typed point | `typed` | the same lines |
| mouse window, crossing or pick (also mixed with typed keys) | `handles`, `clicks` and `view` (no `typed`) | handles; `clicks` (the recorded view shown first) only where handles cannot express the selection (attributes), and for the single click of TRIM / EXTEND |
| mouse selection with a Shift / Ctrl click, or whose clicks cannot be told | `handles` only | handles, last resort |
| older recordings, or a result with no text | `handles` | handles, last resort |
| `point` / `corner` results before the `selection` result (older recordings; AutoCAD raised them after a Navigation Wheel interrupt) | answers of their own | typed as points; the `selection` result after them is dropped as their echo |

Handles: `(handent "..")`, one line per handle, then Enter. Before typing, the replay leaves out a handle that does not exist in the replay drawing or holds another kind of object than recorded (`dxf`),
and repeated handles. If none is left, nothing is typed for it. An empty selection (`status` `Error` without a key press) is dropped and does not stop the typing after it.
A typed window selects what is visible in the view, in the recording and in the replay: start the test with the view the window needs (a `ZOOM E` step) or the replay finds nothing (seen: objects off screen, "0 found"). That holds for **typed** text only. Clicks and pick points bring their own view (next section), so a test that uses them needs no view step and no particular window size.

### View and pick points

A clicked point or a pick is a world point, and AutoCAD picks within a pickbox, a size on the screen. The replay therefore shows the view the click was made in before it types the point, and puts the person's view back afterwards.

**What is recorded.** The view of the active viewport, with every `entity` / `nestedEntity` answer and with a `selection` that has `clicks`:

```json
"view":{"space":"model","layout":"Model","vp":2,"center":[250,0],"height":210.5,"width":502.3,"dir":[0,0,1],"target":[0,0,0],"twist":0}
```

`space` is `model` (the Model tab), `paper` (a layout, paper space) or `viewport` (inside a floating viewport of a layout; `vp` is its number, CVPORT); `layout` is the tab. `center` (DCS), `height`, `width`, `dir` (WCS), `target` (WCS) and `twist` (radians) are the fields of the Editor's current view (`Editor.GetCurrentView`). A perspective view is not recorded. The recorder reads the view of the command's document when a command starts and by its timer (every 120 ms) while a prompt is open, never through the Editor or Document inside a prompt handler (that made mouse picks loop). The result handler calls nothing in AutoCAD: it writes the answer with the view read last and notes the event number and the key of that read. On the next timer tick the key (the system variables `VIEWSIZE`, `VIEWCTR`, `VIEWDIR`, `TARGET`, `VIEWTWIST`, `SCREENSIZE`, `CTAB`, `CVPORT`) is read again, and when it differs, the view may have changed before the pick (a wheel zoom just before the click) and the recorder writes `{"type":"view_stale","for":<n>}`. The readers (`Plan.Build`) drop `view` from the answer of event `n`, so the replay uses handles for it. The Editor is asked again only when the key changed. A recording without `view` (older ones, or a view that could not be read) replays as it did before.

**What the replay does** (`Core/ViewSpec.cs`, `Core/PickReplay.cs`, `AutoCad/ViewControl.cs`, `AutoCad/PickChecker.cs`, `Core/ViewMath.cs`). It decides while it builds the text of a step. When a typed point needs a view, it sets that view with `Editor.SetCurrentView` before it sends the text (nothing is typed to change the view) and puts the view the person had back once the step has settled, or the run has ended. When the replay window has another shape than the recorded one, the view is chosen so that the recorded area is fully visible: the recorded height when the window is wider, the recorded width when it is narrower, the same centre. The view is shown only in the same space and layout, and the document is locked while it is set. **Never inside a floating viewport** (`space` `viewport`): setting the view there changes the Viewport object (its scale, DBMOD, an undo entry), so such picks use handles and such clicks are typed in the current view, as before. If the view cannot be set, the whole step is built again without picks (handles). One step uses one view: an answer that needs another view falls back. The person's view is put back once the step has settled; while a command is still running it is kept until the command ends, and when the run ends it is handed to a restorer that waits until nothing runs, no input is queued and no dialog is open. Only Stop and an error cancel a running command (Esc Esc, never activating another drawing); a "needs a person" ending or a security prompt leaves the command and its dialogs to the person, and the view is put back after they finish. The restorer waits as long as the person needs (the drawing is busy while the command or dialog is open) and sets the view once the drawing is quiet. It gives up when another space or layout is current, when the view changed since the hand-over (the person's own zoom wins), when the drawing was quiet for 10 seconds without the view being set, or after 10 minutes at the latest, and stops with the plugin. A restore that fails while the run goes on is tried again after the next step.

**Pick points.** An `entity` / `nestedEntity` answer is the pick at an object prompt (`BREAK` "Select object:", `FILLET` first and second object, `-REFEDIT`, ...). At the command line a pick is always point-sensitive (where the object was picked decides the break point, the part that is filleted, ...), so the replay types the recorded `pick` point (UCS, as typed) with the recorded view shown, **instead of `(handent ..)`**, when all of this holds:

- the recording has `pick` and `view`, the view can be shown here (not inside a viewport), it is the plan view of the current UCS (a typed 2D point means one place only then; a twisted view, as after `PLAN`, is fine), and the point is inside the area the view shows (the view's own axes are the plane axes turned by the twist, one sign: a point at (x, y) in the plane is at (x cos t - y sin t, x sin t + y cos t) in them, and the view centre is in these axes relative to the target. Checked in AutoCAD 2026 with `PLAN` in a UCS rotated 30 degrees: twist 330 degrees, and the clicked points are inside the recorded window);
- the object is in the replay drawing, in the space shown, is the recorded kind (`dxf`), is not invisible, and its layer is on, thawed and unlocked;
- the point still hits it: its distance to the object, measured in the screen plane (so objects at another height than the point count), is within the pickbox (`PICKBOX` pixels, at the scale of the view as it is shown). Only **curves** (lines, arcs, circles, polylines, splines) are checked: an INSERT, HATCH, MTEXT or TEXT cannot be told from its extents, so those use `(handent ..)`. For a nested pick, the object as it stands in each chain of block references that ends in the space shown (an object that sits directly in the space is a direct pick), and every block reference of that chain must be visible, on a layer that is on, thawed and unlocked;
- no other visible object of the space is within the pickbox of the point (the point would pick it as well). For a nested pick, the blocks that hold the object do not count, and the other objects inside those blocks, and the attributes of every block reference of the chain, seen through the chain, are checked the same way (layer 0 inside a block takes the layer of the insert that holds it, for the on / frozen test). More than one chain that brings the object under the point means handles. Curves are measured, also an XLINE or RAY (they have no extents); an object of another kind is judged by its extents (conservative); an empty text and the overall paper-space viewport are never picked. More than 50000 objects (5000 in a block), or any other object without extents, means "cannot check" and is named in the log.
- the drawing is checked as it is before the step, so within one step only the first pick that can change it is typed as a point. Every answer that can change the drawing counts, whether it was typed or fell back to its handle: a second pick at the same prompt (`OFFSET`, `FILLET` Multiple, ...) and every `EXTEND` click after the first use handles. In `TRIM` / `EXTEND`, any other selection answer (handles, a window, a fence, typed text, a Shift-click that extends, boundary edges) also blocks the typed clicks after it. `TRIM` only removes geometry, and only on the object clicked, so a later `TRIM` click may still be typed when its object differs from every earlier click target of the step (a second click on the same object uses its handle) and all the checks above pass. `BREAK`'s two points on one object and `FILLET`'s two objects are picked before the edit and stay points.

Every check must have run and passed: a check that cannot be made means handles. The current view, UCS and pickbox are read once per step and the objects of the space are listed once per step.

Otherwise the replay types `(handent ..)` as before and says why, in the replay messages and in `retrace.log` (`pick of 7C: no view recorded, used (handent)`). **When unsure it uses handles.**

TRIM and EXTEND (checked in AutoCAD 2026: their "Select object to trim" is a selection prompt, not an entity prompt) record each pick as a `selection` with one handle and one click. For these two commands such a selection replays as the click, under the same conditions (view, hit, nothing else in the pickbox); else as the handle. The clicked selections of other commands keep their handles.

`clicks` that must replay as clicks (the attributes of `-ATTEDIT`) ask for their `view` the same way. They are typed in any case, as before: when the view cannot be shown (another layout, an old recording without a view) they are typed in the current view and a note says so.

Verified in AutoCAD 2026 with real mouse input: `-ATTEDIT` window over a title block (REV D to Q, from `clicks`), `ERASE` crossing (from handles, also after another `ZOOM`), `ERASE` crossing plus a Shift-click deselect (from handles). A drag window (PICKDRAG) could not be reproduced with injected input on the test VM; it raises the same prompts, but is not verified.
Verified with the view and pick points (real mouse input, replayed after another zoom and in another window size): `-ATTEDIT` window (also with the title block out of view), `BREAK` at two picked points, `TRIM` of one side, `FILLET` of two picked lines (also in a UCS rotated 30 and 90 degrees about Z, and in a view twisted by `PLAN` in the 30 degree UCS, twist 330 degrees: the point is checked against the twisted window), `BREAK` with an XLINE far away in the drawing (it does not block the pick), `OFFSET` with the target layer off (handle). A `TRIM` step with two clicks on two lines types both as points (7E and 7F trimmed on the clicked sides). Stopping a replay in the middle of `BREAK` cancels the command and puts the person's view back. Not verified: `EXTEND`, `CHAMFER`, nested picks (`-REFEDIT`), picks in paper space, a stale view (`view_stale`). They use the same code.
Not covered: the Shift / `R` deselect mode, non-US keyboard layouts for punctuation (digits and letters are fine), a UCS other than the WCS (typed text is replayed as typed, so it is right in the same UCS), and `PICKFIRST` (noun-verb) selections made before the command.

## Drawing digest (`state`, `mark.state`, result `expected` / `actual`)

A fingerprint of model space and the layer table, computed read-only (`StartOpenCloseTransaction`) only in command or timer context
(`RETRACERECORD`, `RETRACEMARK`, the replay's checkpoints), never inside an AutoCAD event handler.

```json
{"count":3,"byType":{"CIRCLE":1,"LINE":2},"byLayer":{"0":3},"layers":["0","DIM"],"hash":"3fa9c2b01d4e7a55","v":2}
```

| Field | Meaning |
|---|---|
| `count` | entities in model space |
| `byType` | entity count per DXF name (`LINE`, `CIRCLE`, `INSERT`...) |
| `byLayer` | entity count per layer name |
| `layers` | every layer in the layer table, sorted |
| `hash` | first 16 hex chars of SHA-256 over the sorted entity keys |
| `v` | key rules: `2` (text, blocks and dimensions, below); missing = `1` (every entity keyed on its extents). Says nothing about layer state |
| `layerState` | optional (schema 2): object keyed by layer name (sorted, ordinal) of the layer's properties, each optional, left out when it could not be read: `on` (bool, false = turned off), `frozen` (bool), `locked` (bool), `plot` (bool, plottable), `color` (string: ACI number `"7"`, `"rgb:r,g,b"` for true colour, `"Book$Name"` for a colour book colour), `linetype` (name), `lineweight` (string: millimetres with up to two decimals, `"0.25"`, or `"ByLayer"` / `"ByBlock"` / `"Default"`) |
| `currentLayer` | optional (schema 2): the current layer (`CLAYER`) |

```json
{"count":3,"byType":{"CIRCLE":1,"LINE":2},"byLayer":{"0":3},"layers":["0","DIM"],"hash":"3fa9c2b01d4e7a55","v":2,"currentLayer":"0","layerState":{"0":{"on":true,"frozen":false,"locked":false,"plot":true,"color":"7","linetype":"Continuous","lineweight":"Default"},"DIM":{"on":true,"frozen":false,"locked":true,"plot":true,"color":"1","linetype":"DASHED","lineweight":"0.25"}}}
```

Layers that come from an external reference (names like `XREF|WALLS`) are listed in `layers` but left out of `layerState`: they change when the xref reloads. A malformed `layerState` or `currentLayer` (wrong type, a layer that is not an object) is ignored piece by piece and never invalidates the rest of the digest.
Layer state is not part of `hash` and does not change `v`: a reader detects it by the field being present. Digests without it (every schema 1
recording) are valid and compare as before.

An entity key is `dxf|layer|min x,y,z-max x,y,z|extra`: the geometric extents rounded to 1e-4 (`noext` if reading them throws), and
`extra` where cheap: `len=<rounded length>` for any `Curve` (`block=<name>` for a table).
Entities whose extents include rendered text are keyed `dxf|layer|<placement>|<extra>` instead (v2, `PlacedKey`): extents depend on how
AutoCAD renders the font and changed between two days on one PC. Points are `x,y,z` rounded like extents; angles in radians.

| Entity | placement | extra |
|---|---|---|
| TEXT (left) | `at=<Position> h= wf=` | `text=` |
| TEXT (Aligned / Fit) | `at=<Position> to=<AlignmentPoint>` + `wf=` (Aligned) or `h=` (Fit): the other one comes from the font | `text=` |
| TEXT (other justifications) | `at=<AlignmentPoint> h= wf=` | `text=` |
| all TEXT, ATTDEF, attributes | + ` rot= just=<AttachmentPoint> obl= mirror=<x/y> style=<text style>` (ATTDEF, attributes: ` invisible` when hidden) | ATTDEF: `tag= prompt= default=` |
| MTEXT, multiline ATTDEF / attribute | `at=<Location> h= rot= attach=<AttachmentPoint> w=<width> style=` | `text=<Contents>` (ATTDEF: tag/prompt/default) |
| INSERT | `at=<Position> scale=x,y,z rot=`; MINSERT also `array=<rows>x<cols> spacing=<row>,<col>` | `block=<name>` (dynamic blocks: the definition's name) ` def=<hash of the definition's entity keys>` (dynamic: its current anonymous definition; `xref` for external references) ` dyn=<property=value;...>` (dynamic blocks) ` attrs=<TAG=value@placement;...>` sorted |
| DIMENSION and subtypes | defining points per type (rotated/aligned `p1 p2 line`, radial `center chord`, jogged `center chord override jog`, diametric `chord far`, angular `l1 l2 arc` or `center p1 p2 arc`, arc length `center p1 p2 arc`, ordinate `origin def leader x`), `textat=` when the text was moved by hand, then `measure= style=` | `text=<override>` |

Tables, multileaders and other text-bearing entities still use their extents. A property that throws falls back to the extents key.
A v1 expected digest containing any of these types that no longer matches reports
`geometry hash differs (checkpoint recorded with digest rules v1, ...: check the drawing, then record the test again)`.
Order of creation, handles and colors do not matter. The comparison (`DrawingDigest.Compare`, no AutoCAD references) reports
`LINE: expected 2, got 3`, `layer DIM missing`, `layer TMP unexpected`, `layer 0: expected 2 entities, got 3`, and
`geometry hash differs` when counts and layers agree but the hash does not.
Layer state is compared only for a mark without `checks` (`DrawingDigest.Compare(..., compareLayerState: step.Checks == null)`); a mark with `checks` verifies layers through the `layers` behavior ("Behavior checks"). The current layer (`currentLayer`) is never compared: a test fails on it only through an explicit `sysvars` check of `CLAYER`. The field is still read.

A recording without `state` events or `mark.state` still replays; its checkpoints are reported as not verified.

## Behavior checks (schema 3: `mark.checks`, `snapshots\`)

Besides the drawing digest (model-space entities: always verified, as before) a checkpoint can verify **behaviors**: parts of the drawing that are not model-space entities.
Setting `autoDetectBehavior` (default on) chooses how:

- **auto**: at each checkpoint Retrace works out what changed since the previous checkpoint (the first one: since recording started) and verifies exactly those items.
- **manual** (setting off): the behaviors in setting `checkBehaviors` (default `layers`, `layouts`, `symbols`) are verified whole. The Checkpoints window can change the list for one checkpoint.

### Behaviors

Ids are stable. An item is a key and a value; keys are opaque text. An entity id is the entity's digest key (`DrawingDigest.Key` / `PlacedKey`), with `#2`, `#3`... appended for duplicates. It is never a handle (handles differ between a recording and a replay).

| id | label | item key -> value |
|---|---|---|
| `layers` | Layer state | layer name -> `on`, `frozen`, `locked`, `plot`, `color`, `linetype`, `lineweight` (layers that depend on an xref are left out) |
| `appearance` | Entity appearance | `<entity id without layer>` -> `layer`, `color`, `linetype`, `ltscale`, `lineweight`, `transparency`, `plotstyle` (named only), and the layer's value for each property that is ByLayer |
| `layouts` | Layouts | layout name -> tab order, page setup, and one fingerprint of the layout's entities (`count`, `types`, `hash`) |
| `paperspace` | Paper-space entities | `<entity id>` of each entity on a layout (not viewports) -> empty |
| `viewports` | Viewports | `<layout>\|<n>` -> paper-space viewport: center, size, view center / target / direction, scale, on, locked, frozen layers |
| `xdata` | XData | `<entity id>\|<regapp>` -> canonical xdata |
| `xdicts` | Extension dictionaries | `<owner id>\|<entry path>` -> entry type and canonical data (Xrecords); the owner is `NOD` for the named object dictionary |
| `symbols` | Symbol tables | `<TABLE>:<name>` -> canonical record (linetypes, text styles, dimension styles, block definitions with their entity hash, UCS, registered applications). Layers and views have their own behaviors |
| `sysvars` | System variables | variable name -> value (`CLAYER` -> `DIM`) |
| `views` | Named views | view name -> center, height, width, direction, target, paper-space flag, layout |
| `psattributes` | Attributes on paper-space inserts | `<layout>\|<insert id>\|<TAG>` -> text |

### Canonical values

A value with several parts is a list of `name=value` fields in a fixed order, joined by `;`: `on=1;frozen=0;color=7`. In names and values `\` is written `\\`, `;` is written `\;` and `=` is written `\=`. A compare names the fields that differ. `sysvars` and `psattributes` values are plain text.

### `mark.checks`

```json
{"type":"mark","n":13,"note":"layer dim frozen","state":{},"checks":{"mode":"auto","behaviors":{"layers":{"source":"auto","scope":"items","items":{"DIM":"on=1;frozen=1;locked=0;plot=1;color=1;linetype=Continuous;lineweight=Default","TMP":null}}}}}
```

| Field | Meaning |
|---|---|
| `mode` | `auto` or `manual`: the setting when the checkpoint was made |
| `behaviors` | by behavior id (ordinal order); only the verified behaviors |
| `source` | `auto` (picked from what changed) or `manual` (picked from the list) |
| `scope` | `items`: only the listed items must match; a `null` value means "must be absent". `all`: the behavior's whole item map must equal `items` exactly: a missing, an unexpected and a changed item all fail |
| `items` | item key -> value (or `null`), ordinal order |

Auto picks every behavior with a change, `scope items`: added and changed items with their value now, removed items with `null`. Manual picks the chosen behaviors with `scope all` and every item. A behavior that could not be read at recording time is not picked. Marks without `checks` verify only the digest.

### Snapshots

`snapshots\start.json` (taken when recording starts) and `snapshots\mark-<n>.json` (`n` = the mark event's `n`) hold the full snapshot, so the Checkpoints window can add a behavior after recording:

```json
{"v":1,"behaviors":{"layers":{"0":"on=1;frozen=0;...","DIM":"on=1;frozen=1;..."},"sysvars":{"CLAYER":"DIM","LTSCALE":"1"}}}
```

Behaviors by id, items by key, both in ordinal order. A behavior missing from a snapshot was not captured (it could not be read); that is not the same as no items. Files are written to a temp file and moved.

### Capture details

`AutoCad/BehaviorCapture*.cs` reads the snapshot read-only (one `StartOpenCloseTransaction`) in command / timer context, never in an event handler. Each behavior has its own try/catch: one that throws is left out of the snapshot (= could not be read) and logged once; one that takes more than 200 ms is logged with its time.
Numbers are `DrawingDigest.Num` (4 decimals), points `x,y,z`, booleans `1` / `0`, colours as in the layer state (`7`, `rgb:r,g,b`, `Book$Name`). Field order below is fixed. The pure parts (ids, ordering, groups) are `Core/BehaviorKeys.cs`.

| behavior | item key | value fields, in order |
|---|---|---|
| `layers` | layer name (xref-dependent layers skipped) | `on frozen locked plot color linetype lineweight transparency description` (transparency: alpha 0-90, `ByLayer` or `ByBlock`) |
| `appearance` | `MS:` or `PS:<layout>:` + the entity's key without its layer + `#n` (see "Appearance and paper-space entities") | `layer color linetype ltscale lineweight transparency [plotstyle] [rcolor] [rlinetype] [rlineweight] [rtransparency]` |
| `paperspace` | `PS:<layout>:` + the entity's digest key + `#n` | empty (the key is the geometry) |
| `layouts` | layout name (not Model) | `tab count types hash device media scale psltscale` (`types` = `LINE:2,TEXT:1` sorted; `hash` = digest hash of the layout's entity keys; `scale` = standard scale name or `num:den`; `psltscale` = the layout's own PSLTSCALE, 0 or 1, see "Session independence") |
| `viewports` | `<layout>\|<n>`; n from 1 by (centre Y descending, centre X ascending); the layout's overall viewport is not an item (see "Overall viewport") | `center width height viewcenter viewtarget viewdirection customscale off locked frozenlayers` (sorted names joined by `,`; `off` is 1 when the viewport is turned off, from the saved flags, see "Session independence") |
| `xdata` | `<entity id>\|<regapp>` | fields `0`, `1`... one per group, each `code:value` (1005 handles and object ids are `*`, binary chunks `bin:<length>`) |
| `xdicts` | `<owner id>\|<entry path>`; owner = entity id, `LAYER:<name>`, `BLOCK:<name>` or `NOD` | `type` (DXF name: `DICTIONARY`, `XRECORD`, or the object's), then for an Xrecord fields `0`, `1`... as `code:value`. Nested dictionaries: path `A/B/C`, every entry is an item; `\` and `/` inside an entry name are written `\\` and `\/` |
| `symbols` | `LTYPE:<name>`, `STYLE:<name>`, `DIMSTYLE:<name>`, `BLOCK:<name>`, `UCS:<name>`, `APPID:<name>` | LTYPE `description length dashes`; STYLE `font bigfont typeface height width oblique`; DIMSTYLE `dimscale dimlfac dimtxt dimasz dimexe dimexo dimgap dimdec dimlunit dimaunit dimtad dimclrd dimclre dimclrt dimtxsty`; BLOCK `origin count hash xref path`; UCS `origin xaxis yaxis`; APPID empty |
| `sysvars` | variable name, upper case | text (`x,y,z` for points, numbers rounded); a name AutoCAD does not know is skipped; at replay only the recorded names are compared (see "Compare at replay") |
| `views` | view name | `center height width direction target ps layout category` |
| `psattributes` | `<layout>\|<insert id>\|<TAG>` (`#2` for a second attribute with the tag) | the attribute text (multiline attributes: their contents; invisible attributes included) |

**Entity ids.** `MS:` or `PS:<layout>:` plus the entity's digest key, with `#2`, `#3`... for repeats in the same space. Repeats are numbered in creation order (the order of the owner's block table record), so one edit does not renumber the others and data swapped between identical entities is detected. Limit: if a replay creates identical entities in another order than the recording, numbering follows creation order. Only entities that have the data are keyed, so numbering counts those only (xdata / xdicts: entities with xdata or an extension dictionary; psattributes: inserts with attributes). An INSERT is keyed without its attribute values (otherwise editing an attribute would rename the insert and show as one removal and one addition). Handles are never used.
**Appearance and paper-space entities.** `layouts` carries only one hash of a layout's entity keys, so it says that something on the layout changed but not which entity; `paperspace` lists each entity (not the viewports, which are the `viewports` behavior; the overall viewport is left out as everywhere) under its id, so a moved line is `paperspace PS:A-101 Plan:LINE|DIM|... missing` plus `... unexpected` with the old and new geometry in the two keys. An item is the same key the digest uses for model space (rounded extents; text, blocks and dimensions by what places them and their text). The value is empty. `appearance` covers model space and every layout. Its id is the digest key **without the layer**, so changing an entity's layer is `layer: expected A, got B` and not one entity removed and another added; the layer is a field. Color is `ByLayer`, `ByBlock` or the layer-state notation (`7`, `rgb:r,g,b`, `Book$Name`); linetype is the name (`ByLayer`, `ByBlock`, `Continuous`...); lineweight as for layers (`0.25`, `ByLayer`, `ByBlock`, `Default`); transparency is the alpha 0-255, `ByLayer` or `ByBlock`; `plotstyle` is present only when the entity has a named plot style (not `ByLayer` / `ByColor`). For each of color, linetype, lineweight and transparency that is `ByLayer` the item also holds the layer's value as `rcolor`, `rlinetype`, `rlineweight`, `rtransparency` (ByBlock is not resolved: it depends on the insert), so changing a layer's color also shows in the entities that follow it. An INSERT is keyed without its attribute values, as for xdata. Example line: `appearance PS:A-101 Plan:LINE|0,0,0-5,0,0|#3 color: expected 1, got 3`. **Opt-in.** Both behaviors hold one item per entity, so a recording stores them only when asked: setting `recordBehaviors` (either mode) or, in manual mode, `checkBehaviors` lists them (`RetraceSettings.BehaviorsToRecord`, `BehaviorCatalog.ForRecording`). Otherwise they are left out of the snapshot entirely, like a behavior that could not be read, so a recording made without them is as small as before. In Settings each has a separate "Record" tick (editable in both modes); the "check" tick follows the usual auto / manual rule. The Checkpoints window can add one to a checkpoint only if its snapshot holds it (otherwise its tip says it was not ticked under Record). **Auto pick.** `paperspace`: added and removed items, as for any behavior. `appearance`: an *added* item (a new entity, or a moved one, whose id holds its geometry) and a *changed* item are kept with their own fields only (`layer color linetype ltscale lineweight transparency [plotstyle]`; the resolved `rcolor`, `rlinetype`, `rlineweight`, `rtransparency` are left out because they are the layer's); a changed item whose difference is only in the resolved fields is dropped (`layers` owns it), and so is a removed item. So a layer color change adds nothing, a new red hatch is checked, and a MOVE adds small items for the moved entities. At replay a recording that lacks the resolved fields is not compared on them (`FieldRules` `Unrecorded`); one that has them (a manual pick) compares them like any other field. More than 2000 auto-picked `appearance` items (`BehaviorChecks.MaxAutoAppearanceItems`) leave `appearance` out of that checkpoint's checks (logged: `appearance: N items over the 2000 limit, not checked at this checkpoint`). Manual picks take the whole map. A drawing with more than 100000 entities for one of these two behaviors (`appearance`: model space and layouts together; `paperspace`: the layouts) leaves it out of the snapshot (`could not be read`, logged once) instead of writing a very large snapshot. Both grow linearly with the entity count: on the VM (AutoCAD 2026) a snapshot is about 250 bytes per entity (10 MB for 40000 model-space entities) and the whole entity pass takes under 10 microseconds per entity (about 0.4 s for 44000 entities, all seven behaviors that share it).
Recordings made before these behaviors hold neither, so they compare as before; a checkpoint gets them in auto mode only when they changed between two snapshots that both have them.
**xdata** covers model space and every paper-space layout; **xdicts** the same entities plus layers and non-anonymous, non-xref block definitions.
**Named object dictionary rule.** Top-level entries whose names start `ACAD_`, `AcDb`, `AEC_` or `ASDK_` (without case) are AutoCAD's own (groups, layouts, styles, scale list, plot settings, variable dictionary) and are skipped; an add-in's own dictionary is kept.
**symbols** skips xref-dependent records and, for blocks, layouts, anonymous blocks and names starting `*`. Layers and views have their own behaviors.
**Digest.** The digest no longer carries layer state or the current layer in new recordings (the `layers` and `sysvars` behaviors own them). Replay still reads layer state for an older mark that has no `checks` but recorded one, so such a mark is compared as before.
**Overall viewport.** Every layout that has been current once has an overall paper-space viewport, which AutoCAD resizes and gives records (xdata `ACAD`, an `ASDK_XREC_ANNOTATION_SCALE_INFO` Xrecord) when the layout is first made current. It is AutoCAD's, not the user's: it is left out of `viewports`, of the layout's `count`, `types` and `hash`, and of the `xdata` and `xdicts` owners. It is the first id of `Layout.GetViewports()`. The collection is empty on a layout that was never made current; that layout has no overall viewport and nothing is left out. (Not the first viewport of the block table record, not the one with `Number` 1, and not "the biggest one at the origin": each was wrong on a layout built by a program, after a paper-space zoom or with a user viewport larger than the sheet.) If `GetViewports()` throws, `layouts`, `viewports`, `xdata` and `xdicts` count as not readable for that capture.
**Session independence.** Two values depend on what happened in the AutoCAD session rather than on the drawing, when read through the managed API, so they are read from the object's saved DXF groups instead (`entget`; `Core/DxfGroups.cs`, `AutoCad/DxfRead.cs`):
- **PSLTSCALE is saved per paper layout** and loaded into the system variable whenever that layout becomes current (checked on new drawings from the default template, where Layout1 and Layout2 held different values; Model keeps the current value). A `sysvars` item `PSLTSCALE` therefore followed the layout tab last visited (`sysvar PSLTSCALE: expected 0, got 1` after a layout switch with no command). So `PSLTSCALE` is no longer in the default `sysvars` list (add it with `sysvarsAdd` if you want it anyway), and each `layouts` item has a `psltscale` field: bit 1 of the layout's group 70 after the `AcDbLayout` marker (the first group 70 is the plot-settings flags). It is read without making the layout current. AutoCAD writes the variable into a layout's flag when you move to another paper layout (and when the drawing is saved or written to DXF), not at once, so the flag can lag behind a change made in the layout that owns the live variable. That layout is the one whose block is the database's `*Paper_Space`: the paper layout that is current, and, while Model is current, the last paper layout that was current (checked on the VM: a change made in a layout is seen straight after moving to Model, and `PSLTSCALE` typed in Model changes the value of that last paper layout). For that layout `psltscale` is the live variable; every other layout reads its flag. Model space's value is not captured: Model keeps whatever value is current and has no value of its own to compare.
- **Viewport off.** `Viewport.On` (and COM `ViewportOn`) reads false for every viewport of a layout until that layout has been current in the running session, whatever the file holds (the saved status group 68 is 0 or -1 on such a layout too, and `Number` is -1). The persisted state is bit 0x20000 ("turn viewport off") of the viewport's group 90, which is what the `off` field reads (1 = off). It is the same before and after a layout visit, and a viewport turned off in the Properties palette or with a script changes it at once. The field is called `off`, not `on`, on purpose: recordings made before this change hold `on=0` for viewports that were on (the session-dependent reading), and that value must not be compared with the new meaning.
- **entget.** It is not in AutoCAD.NET and `Application.Invoke("entget")` is refused (`eInvalidInput`), so `DxfRead` calls the AutoCAD process's `acdbGetAdsName` (`acdb<major>.dll`) and `acdbEntGet` (`accore.dll`) by their C++ export names. These are not a published API, and a changed binary interface could crash AutoCAD past any try/catch, so they are used only on the AutoCAD major version they were checked on (`DxfRead.VerifiedMajor`, 25 = AutoCAD 2026). On another version, or if an export is missing or a call fails (a non-zero status or no result is logged once with the object class), the `psltscale` / `off` field is left out of the value.
- **Older recordings.** The schema stays 3. `Compare` tolerates only these fields in `layouts` and `viewports`: a viewport `on` in the recording is ignored (dropped; its value was session dependent), and `off` / `psltscale` that the recording does not have are skipped (not checked). A recording made before this change therefore passes on a drawing that did not change; re-record it to verify off and psltscale. If the recording has `off` or `psltscale` and the capture does not (entget unavailable or failed), the line is `viewport A|1 off: could not be read` / `layout A psltscale: could not be read`, never a quiet pass. Every other field is strict, including `expected (none)` lines. A recorded `sysvars` item `PSLTSCALE` is skipped when the name is no longer read (see below).
**One pass.** layouts, viewports, psattributes, xdata, xdicts, appearance and paperspace (`SharedPass`) are read together in one loop over model space and one over each paper-space layout: every entity is opened once, and its digest key and id key are each computed at most once. At replay only the behaviors a mark's checks name are read, and a mark with empty checks reads nothing.
**Recording.** `snapshots\start.json` is written when recording starts, `snapshots\mark-<n>.json` at each mark (`n` = the mark event's `n`, known once the event is written); `checks` come from `AutoPick(previous snapshot, now)` (previous = the last mark's, or the start snapshot) or `ManualPick`. A mark whose snapshot could not be read has no `checks`. Snapshots are kept per drawing: a mark in drawing B is compared with B's previous snapshot. A drawing the recording first sees (activated or opened) has no snapshot until the next idle moment with no command running (the activate / open handlers only note the need, they never read the drawing); a mark in it before that has empty `checks` (`{"mode":"auto","behaviors":{}}`), which verify nothing. A snapshot file that cannot be written is logged and recording goes on.

### Compare at replay

`BehaviorChecks.Compare(checks, snapshotNow)` returns one line per difference, ordered by behavior id then key; none means the checks passed. Lines:
`sysvar CLAYER: expected DIM, got 0`, `layer DIM frozen: expected 1, got 0` (canonical values: the field is named, one line per differing field; a field on one side only shows `(none)`, except the few that older recordings do not have: viewports `off`, layouts `psltscale`, and viewports `on`, which is ignored; see "Session independence"),
`xdata <id>|ACAD missing`, `symbols STYLE:Notes unexpected`, `layout Sheet1 count: expected 4, got 5`.
`sysvars` is compared by the names recorded only: a variable missing from the replay's snapshot (the user's list changed, or the variable is unknown on this AutoCAD) is skipped, and extra variables are ignored (the variable list is the user's and can change after recording). Empty checks verify nothing: a mark with empty checks and no digest stays "not verified", and no capture is made for it. A behavior the replay could not read gives one line, `<label>: could not be read`. A behavior id this build does not know (a newer recording) is skipped. A checkpoint with no digest but with checks passes when the checks match. The lines go into the checkpoint result (`checkDifferences`).

### System variables

The `sysvars` behavior reads a list of drawing-saved variables worth checking after a test: units, current layer / linetype / color / lineweight, text, dimension, multiline, multileader and table styles, scales, point and polyline settings and the drafting modes the drawing stores (`SysVarDefaults.Default`).
It leaves out variables a person toggles without a command (`ORTHOMODE`, `SNAPMODE`, `GRIDMODE`, `POLARMODE`, `OSMODE`, `DYNMODE`, `LWDISPLAY`, `ANNOALLVISIBLE`, `CTAB`, `TILEMODE`, `UCSNAME`: add them with `sysvarsAdd` to check them) and volatile variables (`DATE`, `CDATE`, `TDUSRTIMER`, `TDINDWG`, `TDUPDATE`, `DBMOD`, `VIEWCTR`, `VIEWSIZE`, `VIEWDIR`, `LASTPOINT`, `CMDACTIVE`, cursor, selection and other temporary variables, file paths).
The list used = default + setting `sysvarsAdd` - setting `sysvarsRemove`, names compared without case. A name AutoCAD does not know is skipped when reading.

## Result file (`results\<yyyy-MM-dd_HH-mm-ss>_<VERDICT>.json`, `results\latest.json`)

Written by `RETRACEREPLAY` / `RETRACERUN` / `RETRACESTEP` when the run ends, in the `results` folder of the test folder (temp file + move, so a poller never reads half of it):
one file per run named by the end time and the verdict (`PASS`, `FAIL`, `INCOMPLETE`, `ERROR`; a second run in the same second gets `_2`), and `latest.json`, a copy of the newest run, for scripts.
Earlier runs are kept: nothing is deleted when a replay starts.
If the results folder cannot be written the error goes to `retrace.log`.

```json
{
  "session": "2026-10-09_1015_draw-basics",
  "startedUtc": "2026-10-09T10:20:00.0000000Z",
  "finishedUtc": "2026-10-09T10:20:19.0000000Z",
  "coreVersion": "0.1.0",
  "verdict": "FAIL",
  "stepsTotal": 6,
  "stepsRun": 6,
  "startStateMatches": true,
  "checkpoints": [
    { "index": 1, "note": "line and circle shown", "pass": true, "differences": [], "expected": { }, "actual": { } },
    { "index": 2, "note": "", "pass": false, "differences": ["LINE: expected 2, got 3"], "expected": { }, "actual": { } }
  ],
  "message": ""
}
```

| Field | Meaning |
|---|---|
| `verdict` | `PASS`: the run completed and every checkpoint matched (at least one). `FAIL`: a checkpoint differed. `INCOMPLETE`: the run stopped before the end (a dialog needs a person, the user pressed Stop, the plugin was shut down), or a checkpoint has no recorded state, or the session has no checkpoints. `ERROR`: a command failed or was unknown, or the replay threw. Precedence: `ERROR`, `FAIL`, `INCOMPLETE`, `PASS` |
| `stepsTotal`, `stepsRun` | steps in the plan, steps started before the run ended |
| `startStateMatches` | the drawing at the start against the session's initial `state`; left out when the session has none. A mismatch only warns, the replay goes on |
| `checkpoints[]` | one per `mark` step reached: `index` (1-based), `note`, `pass` (`true`, `false`, or `null` = no recorded state, not verified), `differences`, `expected` (recorded digest), `actual` (digest at replay time); with behavior checks also `checkMode` (`auto` / `manual`), `checkedBehaviors` (ids) and `checkDifferences` (lines as in "Behavior checks", empty = matched); a difference there makes `pass` false. These three are left out for a checkpoint without checks. |
| `message` | why the run ended early, plus warnings (start state differs, unverified checkpoints, failed steps) |
| `interactive` | `true` for `RETRACERUN`: at a dialog step the replay waits for a person (below). `false` for `RETRACEREPLAY` and Step mode: a dialog ends an Auto run with `INCOMPLETE` |
| `steps[]` | one per step started: `index` (1-based, in the plan; a suite run may add one row with index 0 and kind `start` for a dialog that appeared before the first step), `kind` (`ribbon`, `command`, `lisp`, `mark`, `dialog`; `security-prompt` for AutoCAD's security prompt the run stopped at, with `title` = the prompt's title), `title`, `status` (`ok`; `failed` for a dialog step that did not happen; `needs-person` for the dialog an unattended run stopped at; `blocked` for a security prompt: the run ends at once, verdict `INCOMPLETE`, `message` says what to do; `timed-out` for the step a suite run cancelled after `suiteStepTimeoutSeconds`: verdict `ERROR`, `message` "step N timed out after S s"), `waitedForPerson` (a person handled a dialog while the replay waited; only ever true in an interactive run; false when auto-fill did it), `message` (why it failed), `autoFilled` (dialog steps; only present, as `true`, when the replay filled the dialog in and closed it itself), `fields` (dialog steps where auto-fill tried recorded values; only present then): one entry per recorded value, in recording order: `label`, `status` (`applied`: set and read back equal; `notFound`: no live control matched by label or stock id; `failed`: the control refused, or the value read back differs) and, unless applied, `message` |

A `failed` step makes the verdict `FAIL` (after `ERROR`, before `INCOMPLETE`).

### Interactive dialogs (RETRACERUN)

When the next step is a recorded dialog, the replay sends the command, which opens the dialog itself, and waits. The command line says
`A dialog opens: <title>. Fill it in and confirm (see steps.md, step N), then close it with OK.` (step N from the steps.md beside the recording, when it exists).
The replay continues when the dialog closes, with the steps and checkpoints after it; a checkpoint after the dialog verifies what the person entered. There is no time limit for the person.
If no dialog appears within 30 s, the command ends without one, or it is cancelled, the dialog step is recorded as `failed` (a still running command is cancelled) and the replay goes on.

With setting `autoFillDialogs` (default `true`) and a `dialog_values` event for the dialog, the replay first tries to handle it itself: it fills the recorded `after` values (matched to live controls by label, the n-th one for a `label:...#n` key,
the stock numeric id below 65536 only when no label matches), verifies each by reading the dialog again, and posts a click on OK (or Cancel when `closedWith` is `cancel`; then no values are typed).
`closedWith: null` with changed values counts as OK; no changed values and nothing read, or no event, is left to a person. If it all works the step has `autoFilled: true`, `waitedForPerson: false` and `fields[]`.
If anything fails the person gets the pause message with what is left (`Filled 2 of 3 values; set Spacing to 20 yourself, then close it with OK`), `autoFilled` stays absent, `waitedForPerson` is true and `fields[]` says what was and was not applied.

The command line shows `CHECK n PASS` / `CHECK n FAIL: <differences>` per checkpoint and ends with one line,
`RETRACEREPLAY: PASS 3/3 checkpoints (session x)`.

## Suite summary (`<stamp>_suite.json`)

Not a recording: what a suite run (`RETRACESUITE`) produced, in `C:\temp\retrace\results\` with a `.md` of the same name. The fields are in [suite.md](suite.md).

## Training manifest (`tdoc.json`)

Not a recording: what a Tdoc run (`RETRACETDOC`, see [tdoc.md](tdoc.md)) produced. It sits in `<docs folder>\training\<yyyy-MM-dd_HH-mm-ss>_<title>\` with `pictures\`, and `Engine/Recordings/TrainingDocx.cs` turns it into the Word file beside that folder.
One JSON object, camelCase, UTF-8, written through a temp file. Readers tolerate missing and unknown fields.

| Field | |
|---|---|
| `title`, `createdUtc`, `retraceVersion` | The title typed for the run (ends in "(incomplete)" when it was stopped), ISO 8601 UTC, the plugin version. |
| `tests[]` | One per replayed test: `name`, `folder` (the test folder), `verdict` (`PASS`, `FAIL`, `INCOMPLETE`, `ERROR`) and `message` of its replay result, and `steps[]`. |
| `steps[]` | `number` (1-based in the replay plan; ribbon tab clicks are not in it), `kind` (`ribbon`, `command`, `lisp`, `dialog`, `palette`, `mark`, `open`, `tab`, `other`), `title`, `instructions[]` (sentences for the reader), `pictures[]`, and for `mark` a `checkpoint`. |
| `pictures[]` | `file` (relative to the manifest, `/` separators: `pictures/t1-s3-1.png`), `caption`, `callouts[]`: `number` and `text` of each numbered box on the picture. |
| `checkpoint` | `index`, `note`, `pass` (`true`, `false`, or left out when not verified) and `differences[]`. |

A picture that failed to write is still listed; the Word file shows a note in its place.

## Example

```jsonl
{"type":"header","schema":1,"name":"draw-basics","started":"2026-10-08T10:00:00.0000000+02:00","autocad":"25.1.0.0","product":"AutoCAD 2026","locale":"en","workspace":"Drafting","plugin":"Retrace 0.1.0"}
{"t":6,"n":1,"type":"state","doc":"demo.dwg","state":{"count":0,"byType":{},"byLayer":{},"layers":["0"],"hash":"e3b0c44298fc1c14"}}
{"t":12,"n":2,"type":"doc_active","doc":"demo.dwg"}
{"t":1304,"n":3,"type":"tab","doc":"demo.dwg","id":"ID_TabHome","title":"Home"}
{"t":2210,"n":4,"type":"ribbon","doc":"demo.dwg","id":"ID_Line","text":"Line","macro":"^C^C_line","itemType":"RibbonButton","tabId":"ID_TabHome","tab":"Home","panelId":"ID_PanelDraw","panel":"Draw","path":"Draw","activeTab":"Home"}
{"t":2231,"n":5,"type":"cmd_start","doc":"demo.dwg","command":"LINE","origin":"ribbon","via":"ID_Line"}
{"t":3302,"n":6,"type":"prompt","doc":"demo.dwg","kind":"point","message":"Specify first point:","status":"OK","value":[10,20,0]}
{"t":4120,"n":7,"type":"prompt","doc":"demo.dwg","kind":"point","message":"Specify next point or [Undo]:","keywords":["Undo"],"status":"OK","value":[30.5,40,0]}
{"t":4800,"n":8,"type":"prompt","doc":"demo.dwg","kind":"point","message":"Specify next point or [Undo]:","keywords":["Undo"],"status":"None"}
{"t":4811,"n":9,"type":"cmd_end","doc":"demo.dwg","command":"LINE"}
{"t":9000,"n":10,"type":"modal_enter","doc":"demo.dwg","title":"","windowClass":"#32770"}
{"t":9300,"n":11,"type":"modal_title","doc":"demo.dwg","title":"Layer Properties Manager","windowClass":"#32770"}
{"t":15000,"n":12,"type":"modal_leave","doc":"demo.dwg"}
{"t":16000,"n":13,"type":"mark","doc":"demo.dwg","note":"expected: line shown on layer 0","state":{"count":1,"byType":{"LINE":1},"byLayer":{"0":1},"layers":["0"],"hash":"3fa9c2b01d4e7a55"}}
{"t":17000,"n":14,"type":"session_end","doc":"demo.dwg"}
```

## What is assumed, not verified

The names of all events and properties were checked against the AutoCAD 2026 reference assemblies (`AutoCAD.NET` 25.1.0) and, for the recorder and replay
logic, run in AutoCAD Electrical 2026 (R25.1): `ComponentManager.ItemExecuted` fires for ribbon buttons with tab and panel, built-in commands raise the managed prompt events,
and `SendStringToExecute` with `\n` replays typed and picked points, keywords and Esc. Not confirmed:

- `ItemExecuted` for Quick Access Toolbar and application-menu items.
- `RibbonCommandItem.CommandHandler.Execute(item)` running the macro of CUIX-loaded items.
- `RibbonItem.Id` of CUIX buttons equal to the CUIX element id in `sourcemap.json` (the join falls back to the macro's command).
- `PromptEntityResult.PickedPoint` is recorded as AutoCAD reports it, without a UCS transform; `PromptPointResult.Value` is treated as UCS and converted to WCS.
- `(handent "..")` expressions at object prompts.

## Renaming checkpoints

The Checkpoints window (`RETRACECHECKPOINTS`, and after `RETRACESTOP`) changes only the `note` and, for a recording with behavior checks, the `checks` of `mark` lines in `recording.jsonl` (per checkpoint it lists the behaviors, marked auto or manual; unticking removes one, ticking adds it as manual with the data of `snapshots\mark-<n>.json`, disabled when that file lacks it; the whole `checks` value is rewritten, an empty one kept as `{"mode":..,"behaviors":{}}`): the file is
written to `recording.jsonl.tmp` and moved over the original, and every other line stays byte for byte. An empty name means the `autoName`. Then
`steps.md` is written again (and `steps.docx` / `steps.xlsx`, when the settings ask for them or they already exist). Results of earlier runs keep the
old name. A checkpoint of an older recording without `autoName` is shown with a name worked out the same way, and written only when renamed.
In the guide's how-to pages a checkpoint's block key holds its name, so a renamed checkpoint gets a new block and the old one becomes a removed note.
