# Recording format, schema version 1

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
    screenshots\                        PNGs; every `shot` event's `file` is relative to recording.jsonl ("screenshots/step-02-click.png")
      unused\                           pictures steps.md does not show (kept, out of the way)
    results\
      <yyyy-MM-dd_HH-mm-ss>_<VERDICT>.json   one per replay run (see "Result file")
      latest.json                       copy of the newest run, for scripts
```

- The folder name is the header `started` (local clock), then `_`, then the name. The name is the header `name` (letters, digits, `-` and `_`; every other character becomes `-`). A folder that exists already gets `_2`, `_3`. Folders sort by date.
- Readers look for `<tests folder>\*\recording.jsonl` one level down. Folders whose name starts with `.` are skipped. "Newest" means the latest header `started`, else the file time. Any other file name is not a recording.
- If steps.docx or steps.xlsx is open in Word or Excel when it is rewritten, the new file is called `steps (new).docx` / `steps (new).xlsx` and a note says so.

## What writing the steps does to a test folder

`RETRACESTOP` calls the engine (`EngineRunner.WriteSteps`, `Engine/Recordings/ScriptWriter.cs`, `ShotNamer.cs`) on a background thread:

- It writes `steps.md`, with a short "Files in this folder" table after the introduction (what each file is for, and how to run the test automatically).
  It ends with a `## Result` section (one `[ ] pass  [ ] fail` line per checkpoint and an `Overall:` line). There is no sign-off line.
- With `exportDocx` / `exportXlsx` (or `EngineRunner.Export`, used by the Open test window) it also writes `steps.docx` / `steps.xlsx` from the written `steps.md`
  (parsed with Markdig; `StepsDoc.cs`, `ExportDocx.cs`, `ExportXlsx.cs`, `Exporter.cs`; DocumentFormat.OpenXml). In the docx: headings 1-3, paragraphs with bold / italic / inline code,
  nested bullet and numbered lists (the numbers are text), tables, pictures embedded from the relative `screenshots/...png` links (scaled down to at most 16 cm wide, aspect kept; a missing picture
  becomes the text `[picture not found: ...]`), `[ ]` / `[x]` as a ballot box. In the xlsx, sheet `Steps`: `Step | Action | Details | Check | Screenshot | Result | Notes` (Details = the step's sub-items, one per line; Check = the expected text of a
  checkpoint step; Screenshot = relative hyperlink to the step's first picture; Result = list PASS,FAIL; header bold and frozen, text wrapped), sheet `Info`: test name, recorded line, start-with line, step count, folder.
  Both are written to a temp file and moved over the target.
- It renames each picture the script shows to `step-NN-<what>.png` (NN = the script's step number, two digits, three from 100 steps; a clash gets `-2`, `-3`):
  `click` (ribbon click), `command` (command start), `dialog-before`, `dialog-after`, `pick-K` / `enter-K` / `answer-K` (the K-th answer picture of the step, by how the
  answer was given: mouse pick, typed, unknown), `end` (command end), `cancelled` (command cancelled), `check` (checkpoint).
- It moves pictures in `screenshots\` that the script does not show to `screenshots\unused\`.
- It rewrites `file` in the matching `shot` lines of `recording.jsonl` (temp file, then replace; every other line and byte stays) and only renames when the name differs,
  so running it again changes nothing. If a line cannot be rewritten nothing is renamed and a warning is printed.

## Header (first line)

| Field | Meaning |
|---|---|
| `type` | `"header"` |
| `schema` | `1` |
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
| `lisp_start` | `firstLine` | `LispWillStart` |
| `lisp_end`, `lisp_cancel` | | `LispEnded`, `LispCancelled` |
| `prompt` | `kind`, `seq`, `input`, `by` (`mouse` or `keys`, with `input: true`), `message`, `keywords` (parsed from the message), `status`, `value`, `keyword` | `Editor.PromptingFor*` / `PromptedFor*` |
| `modal_enter` | `title`, `windowClass` of the foreground window at that instant | `Application.EnterModal` |
| `modal_title` | `title`, `windowClass`, sent 300 ms after `modal_enter` if the foreground window changed (the dialog often is not foreground yet at `EnterModal`) | |
| `dialog_values` | `for` (`n` of the dialog's `modal_title` event, else of its `modal_enter`), `title`, `source` (`win32` or `uia`), `fields` (only the fields that changed: `key`, `label`, `kind`, `before`, `after`), `read` (how many fields were read), `closedWith` (`ok` or `cancel`; left out when the dialog was closed some other way) | written when the dialog closes, just before its `modal_leave`; see below. Only with setting `dialogValues` on (default). `read: 0` = nothing could be read |
| `modal_leave` | | `Application.LeaveModal` |
| `doc_active`, `doc_created`, `doc_closing` | | `DocumentActivated`, `DocumentCreated`, `DocumentToBeDestroyed` |
| `state` | `state` (a drawing digest) | `RETRACERECORD`: written right after the header, the drawing as it was when recording started. Readers treat it as session metadata, not a step |
| `mark` | `note` (may be empty: Enter at the prompt still makes a checkpoint), `state` (a drawing digest, left out if the drawing could not be read) | `RETRACEMARK` |
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
`RETRACESTOP` takes the pictures still queued before it writes `session_end`.

### Dialog values (`dialog_values`)

The recorder finds the dialog window (the foreground window of AutoCAD's process once it is not the main window), reads its fields when it is found
(the initial values), again every ~150 ms, and again when a click or Enter reaches a window inside it. On close it compares the first value it saw per field with the last.
`fields` holds only the changed ones; an event with empty `fields` and `read > 0` means "looked, nothing changed". Nested dialogs each get their own event.

- `kind`: `edit`, `check` (`on`/`off`/`mixed`), `radio` (`before`/`after` = text of the checked radio of the group; `label` lists the group's radios as `A / B`), `combo`, `list`, `slider`.
- `key`: `id:<control id>` if the id is non-zero and unique in the dialog, else `label:<label>#<occurrence>`. `label` is the control's own text (check, radio), else the nearest static text left of it on the same row or just above it, else `#<control id>`.
- `source` `win32`: child windows (stock AutoCAD dialogs, DCL, WinForms, MFC). `uia`: no readable child windows (WPF), read through UI Automation on a pool thread (at most 200 elements, 1 s); a radio is then one field per button with `on`/`off`.
- Values are cut at 200 characters. Password edits and read-only edits are skipped.
- Readers: events without a matching `for`, or recordings without the event, change nothing.

### `origin` on `cmd_start`

`"ribbon"` if a `ribbon` event happened at most 2000 ms earlier, was not yet matched to another command, and its `macro`
contains the command name (ignoring `^C`, `_`, `.`, `-` prefixes); `via` is then that item's `id`. Otherwise `"typed/other"`
(typed, a toolbar, a LISP call, a palette, a script...). Commands that start while another one runs (transparent, nested) are
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
| `entity`, `nestedEntity` | `{"handle":"2F","dxf":"LINE","pick":[x,y,z]}` (`pick` as AutoCAD reports it) |
| `selection` | `{"count":3,"handles":["1A",...],"dxf":["LINE",...],"method":"Window"}` (handles and dxf names capped at 200; `method` is the first object's `SelectionMethod`) |

When `status` is `Keyword`, `keyword` is the keyword text the user chose.

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
| `v` | key rules: `2` (text, blocks and dimensions, below); missing = `1` (every entity keyed on its extents) |

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

A recording without `state` events or `mark.state` still replays; its checkpoints are reported as not verified.

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
| `checkpoints[]` | one per `mark` step reached: `index` (1-based), `note`, `pass` (`true`, `false`, or `null` = no recorded state, not verified), `differences`, `expected` (recorded digest), `actual` (digest at replay time) |
| `message` | why the run ended early, plus warnings (start state differs, unverified checkpoints, failed steps) |
| `interactive` | `true` for `RETRACERUN`: at a dialog step the replay waits for a person (below). `false` for `RETRACEREPLAY` and Step mode: a dialog ends an Auto run with `INCOMPLETE` |
| `steps[]` | one per step started: `index` (1-based, in the plan), `kind` (`ribbon`, `command`, `lisp`, `mark`, `dialog`; `security-prompt` for AutoCAD's security prompt the run stopped at, with `title` = the prompt's title), `title`, `status` (`ok`; `failed` for a dialog step that did not happen; `needs-person` for the dialog an unattended run stopped at; `blocked` for a security prompt: the run ends at once, verdict `INCOMPLETE`, `message` says what to do), `waitedForPerson` (a person handled a dialog while the replay waited; only ever true in an interactive run; false when auto-fill did it), `message` (why it failed), `autoFilled` (dialog steps; only present, as `true`, when the replay filled the dialog in and closed it itself), `fields` (dialog steps where auto-fill tried recorded values; only present then): one entry per recorded value, in recording order: `label`, `status` (`applied`: set and read back equal; `notFound`: no live control matched by label or stock id; `failed`: the control refused, or the value read back differs) and, unless applied, `message` |

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
