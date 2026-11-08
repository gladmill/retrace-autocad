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
| `cmd_start` | `command` (global name), `origin`, `via`, `script` (true when AutoCAD said a script was active as the command started: CMDACTIVE bit 4; false when not; left out when it could not be read and in recordings made before it) | `Document.CommandWillStart` |
| `cmd_end`, `cmd_cancel`, `cmd_fail` | `command` | `CommandEnded`, `CommandCancelled`, `CommandFailed` |
| `cmd_unknown` | `command`, `script` (as on `cmd_start`) | `UnknownCommand`. AutoCAD also raises it for a command it then runs (the first UNITS of a session): a `cmd_start` of the same command follows within a moment. Readers make no step of it then (`steps.md` drops a `cmd_unknown` that a `cmd_start` of the same command follows within 1 s; the replay plan never makes a step from it) |
| `lisp_start` | `firstLine`, `script` (as on `cmd_start`: a LISP form that a script runs has it true), `startup` (true: LISP AutoCAD runs while a drawing opens, such as acaddoc.lsp or S::STARTUP, with no click or key since it opened; not a step) | `LispWillStart` |
| `lisp_end`, `lisp_cancel` | | `LispEnded`, `LispCancelled` |
| `prompt` | `kind`, `seq`, `input`, `by` (`mouse` or `keys`, with `input: true`), `message`, `keywords` (parsed from the message), `status`, `value`, `keyword` | `Editor.PromptingFor*` / `PromptedFor*` |
| `view_stale` | `for` (the number of a `prompt` event) | recorder timer: the view stored with that pick or click may have changed before it; readers drop its `view` (see "View and pick points") |
| `modal_enter` | `title`, `windowClass` of the foreground window at that instant | `Application.EnterModal` |
| `modal_title` | `title`, `windowClass`, sent 300 ms after `modal_enter` if the foreground window changed (the dialog often is not foreground yet at `EnterModal`) | |
| `dialog_click` | `for` (the dialog's `n`, as in `dialog_values`), `button` (the push button's text), `id` (its control id; WinForms ids change between runs), `index` (which of the dialog's enabled buttons with that text it is, 1 = the first), `fields` (the fields changed since the dialog opened or since the previous `dialog_click`, as in `dialog_values`), `read`, `tab` (only in a dialog with tab controls: the tab the button was pressed on, see "Tabs"), `statics` (see "Static texts") | a press on an enabled push button of the innermost open dialog that is not its OK, Cancel or Help: a mouse click that comes up on the same button (a double-click is two), Space on it, Enter on it or as the dialog's default button, its mnemonic; the fields are read before the button's handler runs. Win32 dialogs (stock AutoCAD, DCL, WinForms, MFC) and modal WPF windows of AutoCAD's process (`id` is 0 there; `statics` are the window's visible TextBlocks, Labels and read-only text boxes). Only with setting `dialogValues` on. See "Buttons inside a dialog" |
| `dialog_values` | `for` (`n` of the dialog's `modal_title` event, else of its `modal_enter`), `title`, `source` (`win32`, `wpf` or `uia`), `parent` (the `for` of the dialog this one was opened from, only for a dialog that opened while another recorded dialog was open), `fields` (only the fields that changed: `key`, `label`, `kind`, `before`, `after`; after a `dialog_click`, only the changes since the last one), `read` (how many fields were read), `closedWith` (`ok` or `cancel`, `cancel` also for the title bar's close box and Alt+F4; `click` with `button` when its last click closed it; left out when the dialog was closed some other way), `closeTab` (only in a dialog with tab controls: the tab showing when it closed, see "Tabs"), `statics` (the last read before the close, see "Static texts"), `unsupported` (a WPF dialog only: the tab controls, trees, grids and lists it showed while it was open, looked for about once a second and at each press, and Retrace does not follow, `[{"kind":"tree","name":"PartsTree"}]`, kind `tabs`, `tree`, `grid` or `list`, `name`, or for an unnamed part its tab `page`, never a null; left out when none; a reader tolerates it missing) | written when the dialog closes, just before its `modal_leave`; see below. Only with setting `dialogValues` on (default). `read: 0` = nothing could be read |
| `modal_leave` | | `Application.LeaveModal` |
| `palette_values` | `palette` (the palette's name: its tab in the set), `set` (the palette set's title), `source` (`win32` or `wpf`), `fields` (as in `dialog_values`), `read` | the user changed fields in a palette set and then clicked a push button in it (written just before that `palette_click`), moved to another palette, or did something outside it; also before a checkpoint and at Stop. Only with setting `dialogValues` on. See "Palette sets" |
| `palette_click` | `palette`, `set`, `button` (the push button's text), `index` (only when several enabled buttons of the palette have that text: which one, 1 = the first); for a double-click on a tree item `kind: "dblclick"`, `tree` (the tree field's key), `treeLabel`, `path`, and `button` = the item's text | a click on a push button in a palette set, or Space on a focused WinForms push button (read before the button acts); or a double-click on an item **without children** of a tree in a WinForms palette (a node with children is opened or closed by the double-click: not recorded) |
| `native_click` | `palette`, `set`, `control` (`tree`, `button` or `tab`), `action` (tree: `click`, `dblclick`, `rclick`; button and tab: `click`), `path` (tree: the item's texts from the top level down), `index` (which control of that kind in the palette, 1 = the first), `id` (the control's id), `label` (button: its tool tip, when it has one; tab: the text of the tab opened), `statics` (the palette page's static texts at the click, see "Static texts") | a click in one of AutoCAD's native palettes (no .NET content, such as AutoCAD Electrical's Project Manager) on a tree item (not its +/- button), an enabled push button, or another tab of a tab control. A double-click is a `click`, then a `dblclick` on the same item. See "Native palettes" |
| `native_select` | `palette`, `set`, `control` (`combo`), `index`, `id`, `value` (the entry now shown), `statics` (as in `native_click`) | the person clicked a drop-down list in a native palette, and its list has closed showing another entry |
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

The `dialog-after` picture is taken when OK is clicked or Enter is pressed (also in a WPF dialog, see "WPF dialogs").
The script shows the after picture when values changed, also if the "before" picture is missing.

### Dialog values (`dialog_values`)

The recorder finds the dialog window (the foreground window of AutoCAD's process once it is not the main window), reads its fields when it is found
(the initial values), again every ~150 ms, and again when a click or Enter reaches a window inside it. On close it compares the first value it saw per field with the last.
`fields` holds only the changed ones; an event with empty `fields` and `read > 0` means "looked, nothing changed". Nested dialogs each get their own event (with `parent`, see below).
When the user clicked buttons in the dialog (`dialog_click`), `fields` holds only what changed after the last click.

- `kind`: `edit`, `check` (`on`/`off`/`mixed`), `radio` (`before`/`after` = text of the checked radio of the group; `label` lists the group's radios as `A / B`), `combo`, `list`, `slider`, `tree`, `listview`, `treecheck`, and the common controls below: `datetime`, `monthcal`, `ipaddress`, `hotkey`, `comboex`.
- `tree` (a Win32 `SysTreeView32`, also a WinForms `TreeView`; `source` `win32` only): `before` / `after` = the selected item's text and its parents' texts, from the top level down, joined with ` › ` (`Parts › Fasteners › Bolts`; each text trimmed);
  `""` when nothing is selected. `key` and `label` as for any field (`name:<Name>`, the `AccessibleName`, the static text left of or above the tree, else `#<id>`). Read only for a tree of the thread that reads (AutoCAD's UI thread): the TVM messages pass pointers.
  steps.md says ``Select `Parts › Fasteners › Bolts` in **Catalog** (was `Parts`)``. Not recorded: a click on an item (a right-click and the menu item chosen are a `context_menu`, see "Context menus"; a double-click is a `dialog_click`, below), several selected items, label editing, drag and drop;
  a path is cut at the 200-character value cap (replay then cannot find the item and a person selects it); an item text that itself contains ` › ` makes a path ambiguous.
- `listview` (a Win32 `SysListView32`, also a WinForms `ListView`, in any view mode; `source` `win32` only): the **selected items**. `before` / `after` = their first-column texts in row order joined with `; `, a second row with the same text as `Bolts (2nd)`, a blank row as `(blank)` (`""` only when nothing is selected). The text is for display and wording; `items` is the truth for replay, comparison and the pause message (an item text may hold `; ` or `(2nd)`). A selection that could not be read (more than 100 items, a row past the first 5000, an error) is recorded as `(selection not recorded)` with no `items`, never as the one read before; replay stops for a person with "the selection could not be recorded". Two selections are the same when their items have the same text and occurrence (the row only decides for a blank item), so a sorted list or an inserted row is no change. The field also has
  `items`, an array of `{"text": "Bolts", "row": 3, "occurrence": 2}` (`row` from 0, `occurrence` = which row with that text it is, from 1); up to 100 items, a larger selection is not recorded. `key` and `label` as for any field (`name:<Name>`, the `AccessibleName`, the static text left of or above the list, else `#<id>`),
  numbered among list views only. Read only for a list of the thread that reads it. Replay (`MenuListView.SelectItems`, UI thread): every item is looked up first by text and occurrence (`ListViewRows.Resolve`; a blank text by its row), and when one is missing nothing is changed and the replay stops with
  `no item "X" in the list` (never another row); then two items that resolve to one row are selected once and a line in `retrace.log` says so; the rows that differ get `LVM_SETITEMSTATE` (deselect, select; the list sends `LVN_ITEMCHANGED` to its owner from inside the call), the last row gets the focus and `LVM_ENSUREVISIBLE`, and the list takes the keyboard focus. Without `items` (a hand-written
  recording) `after` is split on `; `. steps.md: "Select `Bolts` in **Products**".
- `treecheck` (the check boxes of a tree with `TVS_CHECKBOXES`, also a WinForms `TreeView` with `CheckBoxes`; `source` `win32` only): **one field per item**, not one per tree. `before` / `after` = `on` (state image 2) / `off` (state image 1, read with `TVM_GETITEM` and `TVIS_STATEIMAGEMASK`); an item with no check box is not listed.
  `key` = the tree's own key, `|check:`, the item's path (`name:CheckTree|check:Parts › Fasteners › Nuts`; with a tab `tab:Lines|name:CheckTree|check:A`); `label` = the tree's label, ` ¦ `, the path (`Layers ¦ Parts › Fasteners › Nuts`); `tab` as for the tree.
  Why per item: only the items the person changed are recorded (the diff, as for every other field), so the 200-character value cap never cuts a list of ticked items, and an item that is not there yet is no problem: the reader lists the items that exist at each read (every ~150 ms, and before each click), so the
  children of a node that fills when it opens are listed once it was opened, and an item seen for the first time takes the state it has then as its baseline (a person opens the node, then ticks: the read before the tick has it). At most 1500 items per tree are listed per read, depth first, an item path once.
  steps.md says ``Tick `Parts › Fasteners › Nuts` in **Layers**`` / ``Untick `A` in **Layers**``. A recording from before this has no such field, so nothing about older recordings changes; a reader that does not know the kind ignores it.
  Replay (`TreeControl.SetChecked`, on the UI thread): the tree is found as a `tree` field is (by its key, else its label) and the path walked as for a `tree` value (each parent opened, a lazily filled node fills); an item already in the wanted state is left alone. An item that is the selected one takes the space key (`WM_KEYDOWN` / `WM_KEYUP`: a native tree changes the state image and sends `TVN_ITEMCHANGED`, a WinForms `TreeView` raises `BeforeCheck` / `AfterCheck` as `ByKeyboard`). Any other item takes the mouse messages of a click on its state icon (as a person's click does: it ticks without selecting, so no `AfterSelect`; the point is the middle of the icon at the left end of the item's rectangle, checked by one hit test). Only when that did not change the state is the item selected, the space key sent and the selection put back (then the program sees two selection changes).
  There is no fallback that sets the state without telling the program (`TVM_SETITEM`): when neither way changed it the replay stops with the reason (`the check box of A did not change (tried the space key and a click on the state icon; a person ticks it)`, `no item B under A in the tree`, `the item A has no check box`). UI Automation (WPF) dialogs have no `treecheck` field.
  Order and trees that tick by themselves (`Core/TreeChecks.cs`, `TreeCheckApplier`): the ticks of a segment are applied **deepest first** (a leaf before its parent; the other values keep their place), and after each tick the tree is read again: a recorded item applied before that is not as recorded any more is set again, and an item the recording has no change for that differs from what it was before the first tick (a native tree such as Purge's ticks the children of a ticked parent, a program may tick others) is put back; when that cannot be (putting the siblings back unticks the parent too) the replay stops and says which item changed. The ticks are read back one by one with the item's own state (`DialogFill.LiveValue`, shared by dialogs and palettes: a tick is never compared with the tree's selected path).
  Cost: the tick states of a tree are kept between reads (`TreeTickCache`) and read again when the tree received input, at the last read of the dialog and at least every 2 s, not at every ~150 ms read; a replay does not list the ticks at all. A tree with more than 1500 items with check boxes is cut: a warning goes to `retrace.log` and `dialog_values` gets `truncated` (the labels of the trees), the items past the cap are neither recorded nor replayed.
- `key`: `name:<control name>` for a WinForms control with a unique `Name` (stable between runs; WinForms window ids are not), else `id:<control id>` if the id is non-zero and unique in the dialog, else `label:<label>#<occurrence>`. `label` is the control's own text (check, radio), else the nearest static text left of it on the same row or just above it, else `#<control id>`.
- `source` `win32`: child windows (stock AutoCAD dialogs, DCL, WinForms, MFC). `wpf`: a modal WPF window of AutoCAD's own process (one window, no child windows), read in process from its visual tree like a WPF palette (see "WPF dialogs"). `uia`: no readable child windows and not a WPF window of this process (a message box, a task dialog, a recording made before `wpf` existed), read through UI Automation on a pool thread (at most 200 elements, 1 s); a radio is then one field per button with `on`/`off`. A tree in a WPF dialog is not read.
- Replay of a `tree` value (`win32`, on the UI thread, inside the replay's tick): a tree that already shows the path is left alone. Else the path is walked from the top level, each level matched by its text (trimmed, case-insensitive; the first of
  equal siblings), and each parent is opened on the way: TVM_EXPAND sends the owner TVN_ITEMEXPANDING (a WinForms `TreeView`'s `BeforeExpand`, so a tree that fills a node when it opens fills it) only the first time an item is opened that way,
  so the state TVIS_EXPANDEDONCE of a collapsed node is cleared first (as a click on its +/- button would fire it again). Then the item is selected with TVM_SELECTITEM and scrolled into view; the owner is told with the action TVC_UNKNOWN (WinForms `TreeViewAction.Unknown`), which a program that only reacts to the mouse or the keyboard ignores. (A click sent at the label text selected in a stock tree, AutoCAD's Purge, but not in a WinForms `TreeView`, where it also made the dialog's replay about 6 s slower, so it is not used; only `native_click` in a native palette clicks.) What a selection handler does (fills other
  fields, opens a message box) therefore runs inside the tick, after the fields recorded before the tree and before those recorded after it, as for a combo box. A level that is not found fails the field (`no item Bolts under Parts › Fasteners
  in the tree`) and a person does it. A dialog whose tree was read counts as having a field, so it is never taken for a message box. A tree is numbered among trees: the `label:L#n` key of any other field counts no tree, as before trees were read.
- Values are cut at 200 characters. Password edits and read-only edits are skipped.
- Readers: events without a matching `for`, or recordings without the event, change nothing.

### Common controls and MFC windows (`datetime`, `monthcal`, `ipaddress`, `hotkey`, `comboex`, toolbar clicks)

Read like any other field of a Win32 window (a stock dialog, a modal MFC `CFrameWnd` with a `CFormView` of any depth, a `CDialog`; `source` `win32` only, the control must belong to the UI thread). `key`, `label` and `tab` as for any field. The value is text:

- `datetime` (`SysDateTimePick32`, an MFC `CDateTimeCtrl`): the date `2026-03-14`, or with the `DTS_TIMEFORMAT` style the time `09:30:00`; `""` when the picker shows no date (`DTS_SHOWNONE` box unticked). Replay sets it with `DTM_SETSYSTEMTIME` (the other half of the date and time stays as it is) and then sends the owner `WM_NOTIFY` / `DTN_DATETIMECHANGE`, which a program message does not send by itself. A picker with a custom format (`DTM_SETFORMAT`) is read by its style only.
- `monthcal` (`SysMonthCal32`, `CMonthCalCtrl`): the selected day `2026-03-14`; with `MCS_MULTISELECT` the range `2026-03-14..2026-03-18`. Replay: `MCM_SETCURSEL` / `MCM_SETSELRANGE`, then `MCN_SELCHANGE` and `MCN_SELECT` to the owner.
- `ipaddress` (`SysIPAddress32`, `CIPAddressCtrl`): `192.168.1.10`; `""` when all four fields are blank; a partly filled control reads as it shows (`192.168..`) and replay stops for a person for such a value (it sets complete addresses and clears). A recording made before this control was one field has its four inner edit boxes as four `edit` changes; they find no live field and a person finishes them, as any unmatched field. A `combo` change on the inner combo box of a `ComboBoxEx32` (same control id, so the same key) is the `comboex` field. Read from the four edit boxes inside the control, which are no fields of their own. Replay: `IPM_SETADDRESS` / `IPM_CLEARADDRESS`.
- `hotkey` (`msctls_hotkey32`, `CHotKeyCtrl`): `Ctrl+Shift+A`, `Alt+F5`, `Ctrl+Ext+Delete` (modifiers `Ctrl`, `Shift`, `Alt`, `Ext` in that order, then the key: letters, digits, `F1`-`F24`, the named keys, `Num0`-`Num9`, otherwise `VK` and two hex digits); `""` for none. Replay: `HKM_SETHOTKEY`, then `EN_CHANGE` to the owner.
- `comboex` (`ComboBoxEx32`, `CComboBoxEx`): the selected item's text of a drop-down list, the edit box of a drop-down. Its inner combo box and edit box are no fields. Replay selects the item by text (`CB_SETCURSEL`, then `CBN_SELCHANGE` and `CBN_SELENDOK` to the owner) or sets the edit box.
- A rich edit (`RichEdit20W`, `RICHEDIT50W`, `RichEditD2DPT`, a `CRichEditCtrl`) is an `edit` field of its plain text (paragraphs separated by `\r`); this was already so (the class name contains `richedit`); the formatting is not recorded. A read-only or password rich edit is not a field. Verified in AutoCAD with `RichEdit20W` and `RICHEDIT50W`; `RichEditD2DPT` could not be made on the test machine (the class is not registered there), so it is covered by the class-name rule only.
- `CMFCEditBrowseCtrl` is an `Edit` window (its text is an `edit` field, the browse button is drawn in its frame and is not a click); `CMFCButton` is a `Button` window (verified: a push-style one is a `dialog_click` by its text; a check style was not tried); `CMFCColorButton` is a `Button` window too, so it looks like a push button, a click opens its colour pop-up and the chosen colour is not read (nothing flags it: its class says nothing).
- A **toolbar** (`ToolbarWindow32`, an MFC `CToolBar`) button press is a `dialog_click` with `kind: "toolbar"`, `id` = the command id and `button` = the button's text, else its tooltip, else `toolbar button <id>`. The name is for steps.md and messages only. The tooltip is what the owner answers to a `TTN_NEEDTEXTW` asked as the toolbar's own tooltip would ask (an MFC prompt string `status\ntooltip` gives the tooltip), sent from a hidden window of Retrace's own because MFC's handler moves the window named in `hwndFrom` to the top and that must not be the program's toolbar; it is asked only while recording, once per button, never at replay. It is recorded when the mouse comes up on the button it went down on. Replay finds the button by its **command id** in any toolbar of the window and by nothing else (an MFC command id is the same in every run; a step with no id finds nothing). It looks again until the controls time out, then stops for a person with "toolbar button '<name>' (command <id>) not found". It posts `WM_COMMAND` (command id, notification 0, the toolbar as the control, as the toolbar sends it) to the toolbar's parent, as the control does (not `TB_PRESSBUTTON`: that only changes how the button looks and runs no command). A toolbar's notify window can differ from its parent (`TB_SETPARENT`, a toolbar docked in an MFC docking bar or floating in a mini frame) and no message reads it, so such a toolbar was not tested; the replay cannot tell whether a posted command ran, and the checkpoint after the step decides. A disabled button is not a click when recording; at replay it is waited for until the controls time out (the program enables its buttons from the values just set). A button whose whole face opens a menu (`BTNS_WHOLEDROPDOWN`, or `BTNS_DROPDOWN` on a toolbar without `TBSTYLE_EX_DRAWDDARROWS`, read with `TB_GETEXTENDEDSTYLE`) sends a `TBN_DROPDOWN` notification, not `WM_COMMAND`: the replay stops for a person ("a drop-down toolbar button: press it yourself"; the click is still recorded). The main part of a split button (`BTNS_DROPDOWN` with the arrows drawn) is a plain command and replays as one; its menu is not recorded. A **check button** is put in the state a click leaves it in first (`TB_CHECKBUTTON`: a check button toggled, a button of a check group checked), then the command is posted, and half a second later `TBSTATE_CHECKED` must still be that state, else the replay stops for a person. A Tdoc picture boxes the button's own rectangle (`TB_GETITEMRECT`), not the toolbar. Retrace sends no `TB_` message to a window whose class is not `ToolbarWindow32` (an `Afx:ToolBar` is a `CMFCToolBar` and is only named as unsupported).
- **Unsupported parts** (`unsupported` of `dialog_values`, as for a WPF dialog, plus the replay segments' `note`): an MFC Feature Pack tab control (`CMFCTabCtrl`), property grid (`CMFCPropertyGridCtrl`) and toolbar (`CMFCToolBar`), which draw themselves and answer no Win32 message about their state, are named by kind (`mfc-tabs`, `mfc-propertygrid`, `mfc-toolbar`) and window class (their real classes are `Afx:TabWnd:<hex>`, `Afx:PropList:<hex>` and `Afx:ToolBar:<hex>`; the per-process suffix is cut), e.g. `[{"kind":"mfc-tabs","name":"Afx:TabWnd"}]`; an `Afx:ControlBar` window is a container (a toolbar docked in it is its `ToolbarWindow32` child, a toolbar of its own); a part is told by its class name alone, no window is asked. The check marks of a `CCheckListBox` are neither recorded nor flagged: it is an owner-drawn `ListBox` like any other and MFC answers `LB_GETITEMDATA` itself, so nothing tells it from outside (its selected item is recorded, as a `list`; see TODO.md). The log gets one line per new part (`dialog "X" (win32): the dialog has an MFC tab control (...)`). Replay does not touch them and says `unverified: the dialog has ...` in the `note`. Not done: MSAA / UI Automation readers for them (see TODO.md).

### Tabs (`tab`, `closeTab`, keys `tab:...|...`)


Only visible controls are read, so a field on a tab page that is not showing is read when its page is shown. A dialog (or a WinForms palette) that holds tab controls (`SysTabControl32`: a native one, an MFC / comctl32 property sheet, a WinForms `TabControl`) gives every field and every pressed button the **tab** it is on:
the selected tab's text of each tab control whose page holds it, outermost first, joined with ` › ` (`Lines`, `Fit › Text placement`; mnemonic ampersands and a trailing `:` taken out). The tab texts are read in process (`TCM_GETCURSEL`, `TCM_GETITEMW`: only for a tab control of the reading thread, as for a tree).
A control is in a page when it is a child window of the tab control (a WinForms `TabPage`), else when the window that holds it next to the tab control (a property sheet's page, a `#32770` child of the sheet) lies in the control's display rectangle (`TCM_ADJUSTRECT`), which is also how a plain dialog that shows and hides its
controls on `TCN_SELCHANGE` is read: the control itself must lie in the rectangle. A field outside every tab control (an "OK", a "Preview" check box below the tabs) has no tab.

- `fields[].tab` (`dialog_values`, `dialog_click`, `palette_values`): the tab path, left out for a field that is on none. A dialog with no tab control is written exactly as before.
- **Keys.** In a dialog with tab controls the `key` of a field on a tab starts with its tab: `tab:Lines|id:1001`, `tab:Text|name:txtHeight`, `tab:Text|label:Height#2`. A property sheet reuses control ids across its pages, so without it `id:1001` on page "Lines" and `id:1001` on page "Text" would be one field of the change tracker
  and one page's value would be diffed against the other's. Ids, names and `label:L#n` occurrence numbers are counted per tab. A field outside the tabs, and every field of a dialog without tabs, keeps its key as it was.
- `dialog_click.tab`: the tab the button sits on, or for a button outside the tabs (Apply) the tab the dialog shows (the first outermost tab control's selected tab, then the first tab control inside that page, and so on).
- `dialog_values.closeTab`: the tab the dialog shows when it closes (from its last read; the dialog is gone when it closes). A replay shows it before it presses OK, so the page that checks its input at OK is the one the person closed the dialog from. Left out in a dialog without tabs.
- Readers ignore `tab`, `closeTab` and the `tab:` part of a key when they are absent; a recording from before tabs was read replays as well as before (below).
- **Matching** (`FillMatcher.Match`): a recorded field with a tab is only looked for on that tab, so the same label or id on another page is another field. A recorded key with no tab (an older recording, or a field outside the tabs) matches a live key by its part after the tab, anywhere in the dialog. A live field that sits on no tab
  (a dialog read through UI Automation) is never ruled out by a recorded tab. A `label:L#n` key counts the fields of its own tab. A recorded field with no tab prefers the live field outside the tabs when the dialog has one with that label (an older recording of a tabbed dialog whose label also occurs outside the tabs may take that one). Each value's controls timeout (1.5 s) runs from when it was first tried on its own tab; a tab switch does not restart it, and no other tab is shown while a value on the showing tab is still being retried. After each pass every value set so far that shows on the tab is read back again (earlier tabs are not shown again for that). In a scan of a hidden (auto-hidden) palette, controls under any window without its own `WS_VISIBLE` are left out, which drops the other pages of a WinForms `TabControl` and of a property sheet.
- **Replay** (`RETRACERUN`, `DialogAutoFiller`): the values of a segment are set in the recorded order. A value whose tab is not showing has no live field yet: the values before it are set and read back, then the tab is shown, then the page's controls are waited for (the next 200 ms tick; the 1.5 s for controls starts again at each switch) and the values go on from there.
  Consecutive values on one tab are done together; a tab is switched only when the next value is on another one. The same before a click's button (the click's `tab`) and before the closing OK (`closeTab`; not before a Cancel). How a tab is shown (`TabControl32.Select`), and checked afterwards (`TCM_GETCURSEL`):
  a WinForms `TabControl`: `SelectedIndex`, through WinForms (reflection). A property sheet (the parent answers `PSM_GETCURRENTPAGEHWND` with one of its pages): `PSM_SETCURSEL` to the sheet, which runs the pages' kill-active and set-active logic; a page may refuse to be left (invalid input), then the tab stays and the replay stops with
  that reason (`the Text page did not let go ...`) and a person finishes the dialog. Any other tab control: `TCN_SELCHANGING` to its parent (an answer of "stay" stops the replay with that reason), `TCM_SETCURSEL`, `TCN_SELCHANGE`. A tab text that is not found (after the controls timeout) stops the replay with `no tab X in the dialog`;
  more than 40 switches in one dialog stop it with `the tabs keep changing`. Nested dialogs from a click (`parent`) work as before inside a dialog with tabs, and a nested dialog may have tabs of its own.
- `steps.md` and the pause message group the changes by tab (`On the **Text** tab: set **Text height** to `3.5`` in the steps; `on the Text tab: set Text height to 3.5` in a pause message); the Training doc (Tdoc) says `On the Text tab:` before a run of sentences, goes back to the tab the dialog was closed from before "Click OK.", and puts a numbered box on each tab the replay clicked
  (the tab control's item rectangle); a field on a tab that is not showing when the picture is taken has no box, only its sentence.
- Run in AutoCAD 2026 on 2026-11-06 (`RETRACERUN` and `-RETRACESUITE`): a WinForms dialog with a `TabControl` of three pages (`DRAWTABS`: Name, Width and Height, a check box; shown through WinForms; also a recording that ends on the first page, so the replay goes back to it before OK), the stock DIMSTYLE > Modify (`Modify Dimension Style: Standard`, a nested dialog under the Dimension Style Manager: Text height on the Text tab and Precision on the Primary Units tab, then OK; AutoCAD's tabbed dialogs are plain tab controls, switched with `TCM_SETCURSEL` and the notifications),
  and a WinForms palette with a `TabControl` (`DRAWPALETTE`, page Tabs). The Modify dialog has controls that share one id across pages (2542 is "Fraction height scale" on Text and "Scaling for height" on Tolerances): a value set on Tolerances is kept in the recording when the Text page is shown afterwards (the keys are `tab:Tolerances|id:2542` and `tab:Text|id:2542`).
  OPTIONS, DSETTINGS and the plotter configuration editor were read (their tab controls are logged) but not replayed. Not run in AutoCAD: a real comctl32 / MFC property sheet (`PSM_SETCURSEL`: none of those four is one), a page that refuses to be left, a tab control inside a page of another one.
- Not covered: a tab control of another thread or process; a WPF `TabControl` (WPF dialogs and palettes are read through UI Automation, which has no tabs here); a tab control that is only the strip of tabs (its display rectangle is empty) with the page controls elsewhere; two tab controls that both have a tab of the same text at the same level (the first is taken);
  a tab text that contains ` › ` or is cut differently by the program on each run; a palette button on a tab (a `palette_click` records no tab: it is pressed with the tab the last value left showing).

### Buttons inside a dialog (`dialog_click`, `parent`, `closedWith`)

A push button that is neither OK nor Cancel (nor Help, which opens a window of its own) can change the dialog or open another one: **Add**, **New...**,
**Apply**, **Options...**. While a Win32 dialog is open the recorder sees the input of its message loop (a `WH_GETMESSAGE` hook on the UI thread, which sees a message before it is dispatched, so before
any handler runs) and writes `dialog_click` for a press on an enabled push button (class `Button`; a WinForms check box or radio is not one):
- a **mouse click**: it is written when the button comes up **on the same button and inside it**; released elsewhere, it is dropped. A double-click is two clicks. The dialog is read when the button comes up (before the button's handler, which runs from the button-up message), so a focused field's `Leave` handler that reformats its text as the mouse goes down on the button has run, and the click's fields hold what the button's handler saw. A read made at the button-down (without changing the tracker) is kept only as the fallback for a press that commits before its button-up (`onDown`). A button that acts as it
  goes down (Text Style's Close closes the dialog before the button comes up) still counts: a press that is pending when the dialog goes away is its click, written just before the dialog's `dialog_values`, with the last read of the dialog (its window is gone).
  A button that acts as the mouse **goes down** and opens a nested dialog (a WinForms button whose `MouseDown` handler calls `ShowDialog`) loses its button-up to the new window. When a window opens over the dialog while a press on one of its push buttons is still pending
  (`DialogWatch.DetectNested`; also when AutoCAD announces the window with `EnterModal`, and at the button-up itself when the dialog is disabled under a window by then: `DialogClickRules.UpCommits`, no tick needed in between), that press is committed as the `dialog_click` right then, from the read kept at the button-down, so the nested-dialog gate opens for the window and it is recorded as that click's nested dialog (the `dialog_click` is written before its `modal_enter`). A click committed before its button-up has **`onDown: true`**. Exception: a message box that a focused field's validation opens as the mouse goes down on the button (a field had the focus, the window has only a text and buttons: `DialogClickRules.OnDownWindowIsNested`) is not the button's nested dialog; the press is dropped as it was before mouse-down buttons were recorded (a mouse-down button that opens only a message box while a field has the focus is therefore not recorded).
- a **double-click on an item of a tree** of the dialog (`WM_LBUTTONDBLCLK` on a `SysTreeView32`, on the item, not on its +/- button): `dialog_click` with `kind: "dblclick"`, `tree` (the tree field's key), `treeLabel`, `path` (the item's path, `Housing › Lid`), `button` (the item's own text), `id` (the tree's control id), `index` 1, `fields` (read at the double-click, so with the selection
  the first click made) and `tab`. It is a pending press until it is complete: written when a window opens over the dialog (a sub form), or when the button comes up and the double-click **did something**: a field changed, the dialog is gone, the item has no children, or its expanded state did not flip. A double-click that only opened or closed a node
  (the item has children, its state flipped, nothing else changed: `Core/TreeClickRules.IsAction`, looked at once its button-up has been dispatched) is **not** recorded, and a sub form that a double-click opens is recorded as that click's nested dialog exactly as for a button. A `dialog_click` without `kind` is a push button (every older recording).
- a **button of a toolbar** (`ToolbarWindow32`): `kind: "toolbar"`, see "Common controls and MFC windows".
- **Space** on a focused push button (written at the key's release, on the same button).
- **Enter**: it presses the push button that has the focus, else the dialog's default button (a WinForms `AcceptButton`, `DM_GETDEFID` of a standard dialog, a `BS_DEFPUSHBUTTON`), unless the focused control keeps the key for itself. That is
  decided as the dialog manager (and WinForms) decide it: the control is sent `WM_GETDLGCODE` with the key message, and `DLGC_WANTALLKEYS` / `DLGC_WANTMESSAGE` in the answer means it is the control's (a multi-line edit that takes Return, a combo box with its list
  open, a list view or tree view label edit, a grid cell being edited: no class list). When the default button is OK or Cancel it is `closedWith`; when no default is found Enter is taken as OK, as before.
- a **mnemonic**: Alt+letter, or the letter alone (no Ctrl or Windows key held) when the focused control does not take characters (its `WM_GETDLGCODE` answer for that character has no `DLGC_WANTCHARS`; an edit box, a list, a grid do), in a standard
  dialog and a WinForms one alike, when exactly one control (button, check box, radio, group, label) has that letter underlined and it is an enabled push button.
A WM_COMMAND / BN_CLICKED hook would see a click, Space and a standard dialog's Enter and mnemonics, but not WinForms' Enter on its `AcceptButton` and its mnemonics (`PerformClick` calls `OnClick` with no message), so the
presses are read from the input.

`dialog_click` holds the button's `button` text, `id`, `index` (the n-th enabled button with that text, as for `palette_click`), the `fields` that changed since the dialog opened or since the click before, `read` and `statics`; for a double-click on a tree item also `kind`, `tree`, `treeLabel` and `path`.
Replay of a `dialog_click`: a push button is pressed with a posted `BM_CLICK` (the button's own Click, as `PerformClick`); one with `onDown: true` (it acted as the mouse went down) is pressed with the mouse messages (`WM_MOUSEMOVE`, `WM_LBUTTONDOWN`, `WM_LBUTTONUP` at its middle, posted; `BM_CLICK` sends only `BN_CLICKED`, so a `MouseDown` handler would never run), after a check that the window at that screen point is the button (else `BM_CLICK`). A WinForms button whose Click opened nothing of what the recording has within 2 s (a recording from before `onDown`) is pressed once more with the mouse messages, but only when nothing happened since the first press (the dialog is enabled, no window opened over it, no field or static text changed: `DialogClickRules.MayRetryPress`); a Click that ran slowly without a visible effect cannot be told apart, and a press that did something is never repeated. For such recordings no retry is better than a double press: record them again to get `onDown`. If the point of an `onDown` press is covered, the dialog is brought to the front once; still covered, the replay stops with that reason (no `BM_CLICK` instead). A `dblclick` selects the item (`TreeControl.SelectItem`, so the tree value recorded before it is in place), then sends `WM_LBUTTONDBLCLK` and `WM_LBUTTONUP` at the item's label (`TVM_GETITEMRECT`) from the dispatcher after the tick (what the handler opens runs with the replay's ticks going on, as for a pressed button; a sent button-down before it made a tree wait for a drag for seconds), and goes on with the nested dialog as for a button, to any depth (the filler of a nested dialog does its own clicks and nested dialogs the same way). A double-click is recorded only on the item's icon or label (not the +/- button, the indent or the check box).
The change tracker's baseline then moves to the values read, so what the button's handler did to the dialog (it cleared the text box, filled a list) is not counted as a change the person made: the baseline is taken again at
the **earliest** of the first input to the dialog after the click (read before that input is dispatched, so the person's typing is never in it) and the first timer read 100 ms or more after the click. After a nested dialog closes
the same rule holds for the dialog under it. The closing `dialog_values` therefore holds only what changed after the last click. A dialog with no `dialog_click` is written exactly as before. `source` only says how the fields are read: a dialog
with no Win32 field in four reads in a row is read through UI Automation (`uia`, as before), but its Win32 push buttons are tracked whatever its source is, so a message box (buttons and texts, `source` `uia`) has its clicks (the Yes of a nested box). A
real WPF dialog has no child windows: its buttons are tracked through its visual tree ("WPF dialogs" below), and it is read in process, not through UI Automation.

**How a dialog closed** (`closedWith` of `dialog_values`): `ok` and `cancel` as the OK or Cancel button, Enter on the default button, Esc, **the title bar's close box, Alt+F4 and `SC_CLOSE`** (`cancel`); `click` (with `button`, the
button's text) when the dialog went away after its last `dialog_click` with no other input to it in between, however long that took (a sanity cap of 30 s; a Close button whose id is not IDCANCEL, the Yes / No of a message
box); left out when the dialog was closed some other way. Readers use the field; they do not guess it. A recording with clicks and no `closedWith` and no values after the last click is left to a person by the replay (never an OK nobody pressed); values after the last click
count as before (a left-out `closedWith` with changed values is OK).

A dialog that opens while another recorded dialog is open gets `parent` in its `dialog_values`: the `for` of the outer dialog. AutoCAD raises no `EnterModal` / `LeaveModal` for a dialog that a button
of another dialog opens (checked with Text Style > New... and with a WinForms dialog shown from a button handler, by `ShowDialog` or `Application.ShowModalDialog`), so the recorder finds it
by its window (`AutoCad/ModalAbove.cs`, `DialogWatch.Tick`, every ~120 ms): the innermost tracked dialog is disabled and a visible, enabled window with a title bar of the same thread is there, the
one the dialog owns, else the one in front, **and it follows a click on one of that dialog's buttons** (within 10 s of the click, or of the close of the nested dialog of that click before it, with no other input to the dialog since, one open at a time: a click can open a sequence,
Clear > "Remove all 3 items?" > "3 items removed": `Core/DialogClickRules.cs`, `ClickTrail`). A dialog AutoCAD does announce (`EnterModal`) gets `parent` through the same gate, else it is a dialog of its own. Any other window over a dialog (a validation message box after OK, a Help window, a dialog a double-click in a list opens) was not recorded before and still is not, so it makes no step. The recorder then
writes the nested dialog's `modal_enter` (its `title` and `windowClass`; no `modal_title`), tracks it like any dialog (its own `dialog_click` events, its own nested dialogs, its picture), and when its window is gone writes its
`dialog_values` and `modal_leave`. A dialog that opens and closes within one tick is not seen. If AutoCAD does announce a dialog (`EnterModal`) that is tracked already, the entry that found it takes the announcement over: its
`modal_enter`, `dialog_values` and `modal_leave` are written once: the recorder also writes `modal_alias` (`for`: the `n` of the announcement's `modal_enter`, `of`: the `n` of the `modal_enter` of the entry that found the window), and the readers (`DialogNesting.Alias`) then drop the announcement's step, so the nested dialog is one dialog in the plan and in the script, whichever of the two came first. Readers (`Core/Plan.cs`, `Engine/Recordings/RecordingSession.cs`) attach a dialog with `parent` to the outer dialog's latest `dialog_click`, the button that opened
it, when it opened at most 10 s after that click or after the close of the click's nested dialog before it (`DialogNesting.Attach`, rule `DialogClickRules.NestedBelongsTo`; the reader knows a dialog's close from its `dialog_values`), and do not make it a step of its own; one with no such click stays a step of its own,
as before. The nested dialogs of a click are kept in order and all of them replayed: Text Style > New... with a name that is taken is New Text Style (the name), "Name Already Exists" (its Close), New Text Style (the name that worked), and the replay does the same three, in that order. The recorder also ignores any
input to a dialog that a modal window is over (the dialog is disabled): a message posted to it is not a press. Retrace's own windows (any WPF window of the plugin) are never taken for a nested dialog.

Limits of the button and nested-dialog recording:
- A nested dialog that AutoCAD announces late (`EnterModal` after the recorder found the window) and that closes within about 300 ms, before the announcement's window is looked for, may leave an extra empty dialog step (the announcement is not merged into a dialog that is already gone).
- A double-click on a tree item that does something Retrace cannot see (no field changes, no window opens, the dialog stays, the item has no children or the node's state did not flip, but the program draws something or runs a command) is recorded as a click; one on a node with children that only opened or closed it is not. A right-click on a tree item (a context menu) is not recorded.
- A message box with only an OK button is recorded `closedWith: "cancel"`: its OK has the control id 2 (`IDCANCEL`), and the id decides. `steps.md` then says "Close it with Cancel"; the replay presses that same button.

### WPF dialogs (`source` `wpf`)

A modal WPF window of AutoCAD's own process (a `Window` shown with `Application.ShowModalWindow`; one `HwndWrapper`, no child windows) is followed in process: Retrace runs on the UI thread that owns the window, so UI Automation (which would deadlock there) is not used.
Fields and push buttons come from the visual tree (`AutoCad/WpfDialogs.cs`, `WpfPalette.ScanHost`): the same kinds, labels and keys as a WPF palette (`name:<x:Name>` when unique), read on the UI thread; `source` is `wpf`. A check box, radio button or other toggle is a field, never a click.
A press is what WPF itself does: the recorder listens to `ButtonBase.ClickEvent` with a class handler (it runs before the button's own handlers and its `Command`) while a WPF dialog is tracked, and writes a `dialog_click` when a real push button of the window is clicked after an input of a person (`Core/WpfClickCorrelator`): the left mouse button went down on that same button (however long it was held, so a slow click counts; a ClickMode Press button is clicked at the button-down), or a key is down or came up less than 0.5 s ago (Space, Enter, the default button's Enter, the cancel button's Esc, an access key with or without Alt, bare letters too when the focused control does not take text). A Click with no such input, or on another button, is from code and is not recorded. A click swallowed by an open drop-down, an Enter that a grid or a text box with `AcceptsReturn` keeps, two buttons with the same access key (WPF only moves the focus) raise no `Click`, so they are no press. Only real push buttons count (`WpfPalette.IsPushButton`, rules in `WpfPresses.IsPushButton`): a toggle button (check box, radio) is a field, and a repeat button, a `DataGridColumnHeader` / `GridViewColumnHeader`, or a button that is part of a scroll bar's, slider's, combo box's or date picker's template (the arrows of a scroll bar, a drop-down button) is nothing. The window's own input (preview mouse and key events from `InputManager.PreProcessInput`, subscribed only while a WPF dialog is tracked) feeds the correlator; a mouse press or a key other than a modifier or a system key also moves the baseline and ends the window of the nested-dialog gate, and Esc sets `closedWith` `cancel` until a Click (the cancel button, which says so itself) or the next input replaces it, so an Esc that the dialog's own handler acts on is a cancel. Alt+F4 and the close box are `cancel` as in Win32 dialogs. Nothing is read for an input; the fields are read in the `Click` class handler, before the button's handlers; OK and Cancel (by text) set `closedWith`, Help is no click.
- `button` is the text without the access key's marker (a string Content or an AccessText: the first single underscore goes, `_Add` is `Add`, `A__B` is `A_B`; an `AutomationProperties.Name`, which wins when set, and an x:Name are shown as they are); `index` counts the enabled push buttons with that text on the window; `id` is 0; `statics` are the visible `Label` / `TextBlock` texts and read-only text boxes that are not part of a control (id 0, tree order, `Core/DialogStatics`; see "Static texts"); `tab` is not written (WPF `TabControl`s are not supported: a field is recorded with no tab, and only while its page is showing).
- The nested dialog the button opens is found by its window, like for Win32 dialogs (`ModalAbove`, gate `ClickTrail.MayOpenNested`), whether it is WPF or WinForms, to any depth: WinForms > WPF > WinForms and WPF > WinForms > WPF each record as nested `dialog_values` with `parent`, each with its own `source`.

Replay (`AutoCad/DialogAutoFill.Wpf.cs`) takes this path for a recorded `source` `wpf` and for any dialog with clicks, when the window is a WPF window of the process (a recording made with `uia` and no clicks keeps its UI Automation path): per segment the values are set in process (`WpfPalette.Apply`: the property set and the binding pushed, a check box toggled through its automation peer), read back on the next tick, and the button is pressed through its automation peer (`IInvokeProvider`: `Click` handlers and `Command` run) from the dispatcher at background priority, after the tick that asked, so a button that opens a modal window runs that window's loop outside the tick. A queued press that fails (the button went away or was disabled) ends the replay with that message. After the press the common rules apply (a nested dialog of the recording must open, a child filler does it whatever its kind, the dialog must take input again). OK / Cancel at the end are pressed the same way: the window's `IsDefault` button for OK and its `IsCancel` button for Cancel first, then the button whose text is OK / Cancel, and a Cancel with no button at all closes the window (`WM_CLOSE`). A queued press carries the filler: one that gave up or was stopped by the time the dispatcher gets to it presses nothing. A Tdoc picture boxes the filled fields and the closing button by screen rectangle.

The button text is the same for dialogs and WPF palettes (`WpfPalette.ButtonText`), and one finder (`WpfPresses.Find`) picks a recorded button for both. A palette recording made before the marker was dropped, with `_Add`, still replays: both texts are compared through `StripAccess`. A palette button clicked on its text (a `Run`, no visual) is found too.

Limits: WPF `TabControl`s, trees, `ListView`s and `DataGrid`s are not recorded or replayed in a WPF dialog (a visible tab control, tree, grid or list with a selection that is no field is named in `retrace.log` when the dialog is recorded, once per part, and each segment of a replay result has a `note`: `unverified: the dialog has a tree "PartsTree" ..., which is not recorded or replayed`; a value that finds no control in a dialog with a tab control fails with a message that names the tab control as a possible reason); a touch or pen press raises no mouse message; a WPF window of another process, and Retrace's own windows, are not dialogs under test.

### Static texts (`statics`)

`statics` is machine-readable only: it is not in `steps.md`, the docs, Word / Excel or the Tdoc document. It is the text the window shows that is not a field:
`[{"id":1003,"text":"3 components found"}]`, one entry per visible Static control and read-only edit box (never a password box), in window order, `id` the control's id
(WinForms ids change between runs), each text with its mnemonic ampersands taken out and cut at 200 characters, at most 100 entries, empty texts left out. The field is left out
when there is none. Where: `dialog_click` (the texts as they were when the button was pressed), `dialog_values` (the last read before the close: the read for the press or key that closes the dialog, else the
last press's), `native_click` and `native_select` (the native palette page's texts at the click). The texts are read **on request** (a press, the input that closes a dialog, a native palette click, the replay's press and close), in a pass
of their own: the periodic reads of a recording and the replay's polls do not read them. Win32 and WPF dialogs (a WPF window's: the `Label`s, `TextBlock`s and read-only text boxes that are not part of a control, id 0, read from the visual tree on the same requests; nothing on a tab page that is not showing): a dialog read through UI Automation and a `palette_values` / `palette_click` palette have none.

### Palette sets (`palette_values`, `palette_click`)

A palette set is not a dialog: there is no modal phase to watch. The recorder looks at every click and key press (`PreTranslateMessage`): if it lands
in a palette with .NET content (WinForms or WPF windows under AutoCAD's palette windows: palette, palette set, control bar), it reads that palette's
fields (the initial values), again every ~300 ms while the user works in it, and once more at a push-button click or when input goes elsewhere. It
then writes the changed fields (`palette_values`, nothing if none changed) and the click (`palette_click`). Readers make one step of a
A double-click on a tree item of a palette (`WM_LBUTTONDBLCLK`, on the item's icon or label) is a `palette_click` with `kind: "dblclick"`: the tree's selection (set by the first click) is the `palette_values` step before it, so a replay selects the item and then sends the double-click to the item's label (`PaletteFill`, from the dispatcher after the tick). steps.md says ``double-click `Parts › Fasteners › Bolts › M8` in **Catalog**``. Not done: a double-click on a node with children, a WPF palette, a hidden (auto-hidden) palette.
`palette_values` followed directly by a `palette_click` of the same palette.

- `source` `win32`: the fields are child windows (WinForms), read like a dialog (a `TreeView` is a `tree` field: see "Dialog values"); WinForms check boxes and radio buttons are asked through WinForms.
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
- Tabs: a WinForms palette that holds a `TabControl` is read by the same scan as a dialog, so its fields carry `tab` and a `tab:...|` key (see "Tabs"), and the replay of a palette step shows a tab (`SelectedIndex`, through WinForms) before it sets a value on it. The values of a step are set in the
  recorded order, each tab's values read back before the tab is left (the palette step is tried again half a second after each switch, for up to 15 s), and the step remembers which values are set and checked. The button is pressed with the tab the last value left showing (`palette_click` has no tab). WPF palettes are not covered.
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
by their tool tip), drop-down lists (`ComboBox`) by the entry picked, tabs of a tab control (`SysTabControl32`, `control: "tab"`) by their text. Readers make each a palette step (`Engine/Recordings/NativeAction.cs`);
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
if the click did not (a warning in the result). A **tab** click (the click is seen before the tab control handles it; `TCM_HITTEST` at the click point says which tab is under it) writes `native_click` with `control: "tab"`, `label` (the tab's text), `index` (which tab control of the palette, 1 = the first), `id` (its control id) and `statics`; a click on the tab
that already shows, or on empty space, writes nothing. Readers make it a palette step ("Palette X: open the **Y** tab"). Replay finds the tab control as for trees and buttons (by id when unique, else by position), looks the tab up by its text (`TCM_GETITEMW`) and, if it does not show yet, sends the mouse messages to the tab control at the tab's rectangle
(`TCM_GETITEMRECT`; the control then tells its owner as after a click), and when the selection has not moved, `TCM_SETCURSEL` with `TCN_SELCHANGING` / `TCN_SELCHANGE` to the owner. A tab the palette does not have is retried for 15 s, then a person opens it. Run in AutoCAD 2026 (2026-11-06) in DesignCenter, whose Folders / Open Drawings / History are a `SysTabControl32` in a native palette (recorded with a real mouse, replayed by the mouse messages; the `TCM_SETCURSEL` fallback was not needed there).
Not recorded: keys in a native palette, the context menu itself (see "Context menus" for the menus of plugin dialogs and palettes), other control kinds, and
native palettes that draw their own controls.

### Context menus (`context_menu`)

A right-click (mouse, the Menu key, Shift+F10) on a control of a tracked dialog or a plugin palette (WinForms or WPF), and the menu item chosen from the menu it opens, is one `context_menu` event
(`AutoCad/MenuWatch.cs`, `MenuControls.cs`; pure parts `Core/ContextMenu.cs`). A menu closed without a choice (Esc, a click elsewhere) writes nothing, and neither does a menu item that opens a submenu.

```json
{"type":"context_menu","for":3,"kind":"winforms","target":"name:CatalogTree","label":"Catalog","targetKind":"tree","item":"Tools › Hammer","path":["Edit","Rename"],"fields":[],"read":3}
{"type":"context_menu","palette":"Menus","set":"Retrace Samples","kind":"winforms","target":"name:MenuTree","label":"Notes","targetKind":"tree","item":"Bolts","path":["Properties..."]}
```

- `for` (a dialog) or `palette` and `set` (a palette): where. In a dialog the event is a click like `dialog_click` and has its `fields` (changed since the dialog opened or the click before, read before the menu item's handler ran), `read`, `tab` and `statics`;
  the dialog's baseline moves as after a button, and the item opens the nested-dialog gate (`ClickTrail`) like a button: a sub form the item opens (Properties...) is recorded as nested (`parent`) and attached to this event by the readers (`DialogNesting.Attach`).
  In a palette the values changed so far go out first (`palette_values`), then this event; readers make one palette step of the two (like a `palette_click`), and a command the item starts (`cmd_start` with origin `palette`) belongs to it.
- `kind`: `win32` (a popup menu from `TrackPopupMenu`), `winforms` (a `ContextMenuStrip`), `wpf` (a `ContextMenu`).
- `target`, `label`, `targetKind`: the control the right-click went to, keyed and named as the fields of the dialog or palette are (`name:...`, `id:...`, `label:...`; `tab:...|` first on a tab) and its field kind (`tree`, `list`, `edit` ...; `listview` for a list view, which is no field: `target` is then `name:...` or `id:...` of the control and `item` the first-column text of the selected item). Left out when the control is no field
  (the menu then belongs to the window).
- A list view (`SysListView32`, a WinForms `ListView`) is a dialog field of kind `listview` (selected items) but its menu event keeps `targetKind` `listview` and no field value and these properties (all optional; a recording without them replays as before):
  `item` is the first-column text of the row the menu opened on, as the program sees it: the clicked row, or for the Menu key and Shift+F10 the focused row when it is selected (else the first selected one);
  `itemIndex` its row and `occurrence` which row with that text it is (from 1), so two rows with the same text are told apart (replay takes text + occurrence, the row when the text is blank);
  `selection` the texts of all selected rows when the menu opened and `selectionOccurrences` / `selectionRows` which row with that text each is and its row (a blank text is found by its row) (a click on an unselected row selects it alone; a click on empty space records none, the list view clears the selection; replay checks that every row is there first and only then selects them again, so a missing row leaves the person's selection untouched);
  `selectionTruncated` the row is among more than 100 selected ones (`selection` is left out and replay stops for a person instead of acting on part of a selection);
  `noItem` a hit test (or, for a key, no focused selected row and no selection) found no row: replay clears the selection and opens the menu on empty space below the rows;
  `onHeader` the right-click went to the column header: no row, the selection is not touched, replay posts the right-click to the header window. No-item replay clicks empty space below the rows (never the header band) or, when the rows fill the list, posts WM_CONTEXTMENU in its keyboard form with nothing selected. `target` of a WinForms list view without a Name is `ord:N` (the Nth list view of the window: counted over the form's control tree, created or not, when the window is a WinForms form, else over its child windows, which counts only list views that exist; a list view on a tab page that was never shown cannot be used by replay until the page has been): its dialog id changes every run. Fewer rows with the item's text than the recording used is a reason ("only 1 row \"A\", the recording used the 2nd"), never another row. A menu on a row past the first 5000 records no item. The label is the accessible name, else the static text next to the list. A list that could not be read records none of these (not `noItem`), and replay stops there for a person rather than right-clicking a row it cannot know. Limits: a list view's key has no tab part, so two list views with the same id or name on different pages of a property sheet are not told apart (the first visible one is used); a report view scrolled sideways past its first column is right-clicked outside its rows.
- `item`: for a `tree` the path of the item under the click (` › ` joined, as a tree value), for a `list` the item's text; left out for other controls. It is worked out at the right-click, before the program reacts (a program that selects the node in its mouse-down handler is
  then not confused). With the Menu key the selected item.
- `path`: the menu items chosen from the top, as their texts show without mnemonics and accelerators (`["Edit","Rename"]`; the ellipsis is kept: `"Properties..."`). `id`: the command id of a Win32 menu item (not for the other kinds).

How each kind is seen, always before the item's own code runs:
- `win32`: while a right-click is armed, a `WH_CALLWNDPROC` hook sees `WM_MENUSELECT` (the highlighted item and its menu: the path through the submenus) and the posted `WM_COMMAND` with that item's id says it was chosen (`Core/ContextMenu.cs`, `MenuTracker`).
  A menu shown with `TPM_RETURNCMD` posts no `WM_COMMAND`: not recorded. A command id that arrives more than 2 s after the menu closed is not the menu's.
- `winforms`: the button-up on the drop-down window (a `ToolStripDropDown`, asked through reflection) names the item under it (`GetItemAt`) and its `OwnerItem` chain is the path. A separator, a disabled item and an item that opens a submenu are not choices. Not recorded: choosing with Enter or a mnemonic key.
- `wpf`: a class handler for `MenuItem.Click` (class handlers run before the item's own handlers); the path is the `MenuItem` headers up to the `ContextMenu` (mnemonic underscores out), the control its `PlacementTarget` (for a list or tree item, the item and its parents' headers).

Replay (`AutoCad/MenuReplay.cs`; dialogs through `DialogAutoFill`, palettes through `PaletteFill`; the values before it are set first, and the tab is shown): the target is found by its key as a field of that name is, the `item` is selected (`TVM_SELECTITEM` through `TreeControl` for a tree, as
a tree value; the list item for a list), then the menu is opened the way the program expects it and the item is picked by its text path (ignoring case, mnemonics, accelerators and a trailing ellipsis; the first enabled item with that text):
- `win32`: `WM_CONTEXTMENU` is posted to the control at the item (then the mouse messages if no menu window appears within 1.5 s); the program shows its menu from its own message loop. The menu window's `HMENU` (`MN_GETHMENU`) is walked by text; then `EndMenu` ends the menu and the item's
  command id is posted to the menu's owner as `WM_COMMAND` (the recorded `id` is the fallback when the items have no text, owner-drawn menus). The menu window must be gone within 2 s.
- `winforms`: the mouse messages for a right-click are posted at the item (a `TreeView` shows its menu only for a right-click it saw itself), else `WM_CONTEXTMENU`, else the strip is shown where the item is; the control's `ContextMenuStrip` (a tree node's own first) opens by itself, so its `Opening` handlers run. The item is found in the
  strip by its text path, the strip is closed and the item's `PerformClick` is queued on the dispatcher (an item may open a modal form: never inside the replay's tick).
- `wpf`: the element (or the list or tree item container, which is selected) is found by its key and text, `ContextMenuOpening` is raised on it (its handlers run), and when that does not open the menu (it did not in AutoCAD 2026) `ContextMenu.IsOpen` is set with the element as `PlacementTarget`, the item is found by its header path, the menu is closed and the click is raised through the item's automation peer (queued).
A menu is never left open: when a step fails, `EndMenu` / `Close` / `IsOpen = false` come before the reason is handed to a person (the pause message: "right-click Bolts in Catalog, choose Edit › Rename yourself"). A nested dialog the item opens is waited for and done like one a button opens.

steps.md says `Right-click **Bolts** in **Catalog**, choose **Edit › Rename**` (a palette step: `In the **Notes** palette (Retrace Samples), right-click ... choose ...`); the Tdoc sentence and the pause message use the same words.
Not recorded or replayed: a menu of a native AutoCAD palette (its `native_click` with `rclick` still selects the item and types the command picked), the ribbon's and AutoCAD's own window menus, the system menu, keyboard choices in a WinForms menu, a Win32 menu with `TPM_RETURNCMD`,
menus on a grid cell (the item is not known: the menu belongs to the control; a list view's item is, see `targetKind` `listview`), a menu item whose handler waits for the mouse, and WPF menus of a dialog (a WPF dialog has no controls Retrace can press from outside: a person does it).

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

**A ribbon click that runs a SCRIPT file (`script` on `cmd_start`, `lisp_start`, `cmd_unknown`).** The recorder reads CMDACTIVE as each of these events is written and adds `script`: `true` when bit 4 ("a script is active") is set,
`false` when not, left out when the variable cannot be read and in recordings made before this field. A button can start a LISP that runs commands and then `(command "SCRIPT" file)`, or its macro can be `^C^C_SCRIPT file`;
the script file types more commands, which can open dialogs. `Plan.ScriptChain` finds them: the click's `lisp_start` follows its `ribbon` event within 1 s (not a start-up LISP) and a `SCRIPT` `cmd_start` is inside that LISP, or the click's
first `cmd_start` is `SCRIPT` with origin `ribbon`. After that, `Plan.ContinuesChain` decides for every command or LISP that starts: with `script` it is exact (`true` belongs to the click, however slow the script is; `false` is a person's);
without it (an older recording) it is a guess: `typed/other`, started at most 250 ms after the previous command (or the LISP) ended. The end of the SCRIPT command or of the LISP, recorded after the LISP's own end, is a member whenever it comes in a recording with
the flag. A `cmd_unknown` and the `cmd_start` of the same command that follows it are one command. Events a dialog writes about itself (`modal_*`, `dialog_click`, `dialog_values`, `shot`, `view_stale`, and a `context_menu` with `for`) never break
the chain; a `context_menu` of a palette, `native_*`, `palette_*`, `tab`, `doc_created` do. The LISP, the commands and their prompts belong to the ribbon step (`Step.ChainClick`): not typed again, not steps of their own. A person's `input: true` answer
(in a recording without any `input` flag: any prompt) inside a command ends the chain after that command: what starts next is its own step. An answer a person gave before the first dialog opened, including at SCRIPT's own prompt for the file name
(FILEDIA 0), leaves the whole click split as before (a click cannot give it); AutoCAD's security prompt is no dialog here. Answers after the first dialog stay with the ribbon step (`Step.ChainAnswers`); the replay does not type them (it cannot know which prompt of the
script is waiting): it fills each dialog of the chain in turn and, where the recording has such an answer, stops for a person ("the script \"<button>\" started asks for input a person gave while recording (point at \"<prompt>\"); give it, then press Next step");
in a suite run that is a needs-person end. The ribbon step keeps the click's LISP form (`Step.Lisp`), typed when the click starts nothing and the button has no macro. `steps.md`, the live step count and the script export follow the same rule
(`RecordingReader` and `StepTally` call `Plan.ScriptChain` / `Plan.ContinuesChain`). A typed `SCRIPT` command, a palette button and a typed LISP that run a script are not owners yet (TODO.md).

Which plan a recording gets does not depend on when it was made: the replay plans from the raw events, so an older recording of such a button gets this plan too. The replay's safety net
(`ReplayRules.DialogAfterChain`) only covers a recording the chain rule rejects, which is planned with a step per command: when a dialog opens while the next step is not a dialog, the steps up to the dialog step are
command / LISP steps without a person's answers (an answer with no `input` flag counts as one), and the open dialog's title equals the next dialog step's title exactly (never a generic title such as "AutoCAD" or the main window's, and the window class
equal when the recording has it, after dropping the parts that change per run: `HwndWrapper[...]`, `WindowsForms10.<kind>.`, `Afx:...`), the replay skips those steps and the later commands of the same script (`ReplayRules.LaterScriptCommands`, or it stops for a person when it cannot tell). Non-interactive replay
(`RETRACEREPLAY` Step / Auto) cannot give such an answer: a chain click that has one stops with a needs-person message that names it, before the click. The replay waits while AutoCAD reports a script or AutoLISP active (CMDACTIVE bit 4 or 32).

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
| `verdict` | `PASS`: the run completed and every checkpoint matched (at least one). `FAIL`: a checkpoint differed. `INCOMPLETE`: the run stopped before the end (a dialog needs a person, the user pressed Stop, the plugin was shut down), or a checkpoint has no recorded state, or the session has no checkpoints. `ERROR`: a command failed, or was unknown and did not start within 2 s of AutoCAD saying so (AutoCAD reports some commands as unknown a moment before it runs them; the replay waits for the command it sent), or the replay threw. Precedence: `ERROR`, `FAIL`, `INCOMPLETE`, `PASS` |
| `stepsTotal`, `stepsRun` | steps in the plan, steps started before the run ended |
| `startStateMatches` | the drawing at the start against the session's initial `state`; left out when the session has none. A mismatch only warns, the replay goes on |
| `checkpoints[]` | one per `mark` step reached: `index` (1-based), `note`, `pass` (`true`, `false`, or `null` = no recorded state, not verified), `differences`, `expected` (recorded digest), `actual` (digest at replay time); with behavior checks also `checkMode` (`auto` / `manual`), `checkedBehaviors` (ids) and `checkDifferences` (lines as in "Behavior checks", empty = matched); a difference there makes `pass` false. These three are left out for a checkpoint without checks. |
| `message` | why the run ended early, plus warnings (start state differs, unverified checkpoints, failed steps) |
| `interactive` | `true` for `RETRACERUN`: at a dialog step the replay waits for a person (below). `false` for `RETRACEREPLAY` and Step mode: a dialog ends an Auto run with `INCOMPLETE` |
| `steps[]` | one per step started: `index` (1-based, in the plan; a suite run may add one row with index 0 and kind `start` for a dialog that appeared before the first step), `kind` (`ribbon`, `command`, `lisp`, `mark`, `dialog`; `security-prompt` for AutoCAD's security prompt the run stopped at, with `title` = the prompt's title), `title`, `status` (`ok`; `failed` for a dialog step that did not happen; `needs-person` for the dialog an unattended run stopped at; `blocked` for a security prompt: the run ends at once, verdict `INCOMPLETE`, `message` says what to do; `timed-out` for the step a suite run cancelled after `suiteStepTimeoutSeconds`: verdict `ERROR`, `message` "step N timed out after S s"), `waitedForPerson` (a person handled a dialog while the replay waited; only ever true in an interactive run; false when auto-fill did it), `message` (why it failed), `autoFilled` (dialog steps; only present, as `true`, when the replay filled the dialog in and closed it itself), `fields` (dialog steps where auto-fill tried recorded values; only present then): one entry per recorded value, in recording order: `label`, `status` (`applied`: set and read back equal; `notFound`: no live control matched by label or stock id; `failed`: the control refused, or the value read back differs) and, unless applied, `message`, `segments` (dialog steps a replay did through Win32 controls: one entry per recorded button click and a last one for the close, with the static texts the dialog showed before each; see "Interactive dialogs") |

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

A dialog with `dialog_click` events is done in **segments** (`Core/DialogSegments.cs`, `AutoCad/DialogAutoFill.cs`): one per click (set the values recorded before it, check them, press the button),
and a last one (set the values changed after the last click, press OK, or Cancel when `closedWith` is `cancel`; the values after the last click are not set then, the clicks are done).
The button is the `index`-th enabled push button with that text; the click is posted (`BM_CLICK`), so the dialog runs it from its own message loop. A button that is not enabled yet is looked for until the
controls time out (1.5 s). After a press the replay waits until either a nested modal dialog opens, or the dialog stays quiet for about 1 s:
- a nested dialog opens (the outer dialog is disabled and a visible window with a title bar is enabled on the thread): it must be the one recorded for that click (same title, `closedWith` and values known);
  a child filler then does it from its own segments, recursively, and the replay waits until the outer dialog takes input again (300 ms after) before it goes on. The outer step does not end when the
  nested dialog closes (the replay's step ends when the last modal dialog has closed). A click can have several nested dialogs, one after the other: the replay does them in the recorded order, each when the one before it has closed;
- a nested dialog was recorded for the click but none opens within 8 s, a dialog opens that the recording does not have (or with another title), a button is missing, a value does not go in, or the dialog is
  gone after a click that was not the last: the replay gives up and the person gets the pause message, which now also names what is left to press (`Filled 1 of 3 values; set Name to B, press Add yourself, then close it with OK`;
  a nested dialog that failed is named in the note first). A button whose press closed the dialog (Yes / No of a message box, a Done button, the Close of Text Style, whose id is not IDCANCEL so it is a click and not
  `closedWith: cancel`) ends the dialog when the recording says `closedWith: "click"`. The replay then presses nothing more and waits up to 10 s for the window to go (it never goes on to OK; the dialog still open after that is a give-up, the person finishes it); `steps.md` says "the last click
  closes the dialog" and the Tdoc picture is taken before that click. Unattended (`-RETRACESUITE`, and `RETRACEREPLAY ... Auto`, which does not fill dialogs at all) a segment that cannot be done ends the test as needs-person / `INCOMPLETE`, as for any dialog.
- A dialog with recorded clicks needs Win32 controls or a WPF window of AutoCAD's process ("WPF dialogs"); any other dialog without child windows goes to a person.

The result file's dialog step then also has `segments[]` (Win32 dialogs the replay tried; not for a dialog done through UI Automation): one entry per segment with `button` (left out for the last, closing segment),
`status` (`ok`, `failed` with `message`, `notRun`, or `person`: the replay gave up in this segment and a person finished the step by hand, so this segment and the rest were theirs; `message` on the first says why), `fields` (as above, for that segment's values), `statics` (the static texts the dialog showed just before the button was pressed, or before the close), `note` (a WPF dialog only: the tab controls, trees, grids and lists it has that are not recorded or replayed; never the cause of a failure),
and `nested[]` (`title`, `segments[]`) for the dialogs the button opened. `fields` stays the flat list of all the values, in recording order. In a Tdoc run the picture of the dialog is taken before the last
click (OK / Cancel) as for any dialog: it shows the final fill (a callout on each field set after the last click) and a callout "Click <button>" on each button pressed on the way, not the dialog as it looked at each click; the nested dialogs get no picture.

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
