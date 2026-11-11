# AutoCAD status board: file contract, v1

One place to see what AutoCAD and the tools driving it are doing: is a test running, is AutoCAD waiting for input,
is it blocked by a dialog (a LISP `alert`, a security prompt), or is it hung. Every tool that drives or watches AutoCAD writes
one small JSON file; readers merge them. Tools share only these files, never code or dll references, so any version of any
tool can be present or missing.

## Folder

`%ProgramData%\Gladmill\acad-status\` (create it if missing). The environment variable `ACAD_STATUS_DIR`, when set, replaces it
for every writer and reader.

## Files

- One file per writer instance: `<source>.json` or `<source>-<instance>.json`. `source` matches `^[a-z0-9][a-z0-9-]{0,39}$`.
  In-process AutoCAD writers use the AutoCAD process id as instance (`acad-4812.json`, `retrace-4812.json`), so two AutoCAD
  sessions never share a file. Writers outside AutoCAD use their own process id (`acad-pilot-7720.json`: one file per
  dev kit script), so no two processes ever write the same file. Known sources: `acad` (the AcadStatus add-in), `retrace`, `acad-pilot` (dev kit scripts).
- A writer only ever writes its own file. No locking.
- UTF-8 without BOM. Atomic write: write `<file>.tmp-<pid>` in the same folder, then move it over the file (replace).
  Readers that hit a half-written or unparsable file skip it this round (never crash).
- Readers open a status file with sharing for read, write and delete (`FileShare.ReadWrite | FileShare.Delete`), read it
  in one go and close it at once, so a writer's replace never fails because a reader holds the file. Writers retry a failed
  replace on their next refresh; a writer backs off (at most 10 s) only after several failures in a row.
- A writer that exits cleanly writes `state: "stopped"` (or deletes its file). On start and then every 10 minutes, the `acad` add-in deletes
  any status file whose `pid` is gone (or reused, see `pidStart`) and whose `updated` is over 1 hour old.

## Fields

Required in every file:

| field | type | meaning |
|---|---|---|
| `v` | int | contract version, `1` |
| `source` | string | as in the file name |
| `writer` | string | product and version, e.g. `"Retrace 0.6.7"` |
| `pid` | int | process id of the writer (for in-process writers: acad.exe) |
| `updated` | string | UTC ISO 8601 with `Z`, last write |
| `ttlSec` | int | the writer refreshes at least every `ttlSec / 2` seconds while alive; past `updated + ttlSec` the entry is **stale** |
| `state` | string | see States |
| `summary` | string | one line, at most 200 chars, for a person or a model |

Optional: `pidStart` (UTC ISO 8601, the start time of process `pid`; Windows reuses process ids, so a reader that finds
`pid` alive but with a different start time, more than 2 s apart, treats the process as gone), `short` (at most 18 chars, for the status bar pane, e.g. `"Suite 3/7"`), `since` (UTC, when this state began),
`owner` (who started the work: `"user"`, `"COM"`, `"claude opus-5.5"`...; dev kit scripts take it from `ACAD_STATUS_OWNER`),
`detail` (object, source-specific, see below).

## States

Display priority, highest first: `blocked` > `needs-person` > `waiting-input` > `busy` > `error` > `running` > `idle` > `stopped`.
An unknown state is shown as its text, with neutral colour, ranked just above `idle`. A stale entry ranks as `busy` when its
`pid` is alive (the writer cannot refresh: for in-process writers, AutoCAD's UI thread is not running) and is ignored when
the pid is gone. A `stopped` entry is never ranked `busy`: stale or not, it only says the writer is gone.

Readers read only files named `^[a-z0-9][a-z0-9-]{0,39}\.json$` (so `<source>.v2.json` and `.tmp-` files are skipped) and skip a
file that lacks a required field or whose `v` is not an integer >= 1.

- `blocked`: a window is in the way and nothing is dealing with it (a dialog or message box open 15 s or longer).
- `needs-person`: a tool stopped because a step needs a person (Retrace's needs-person).
- `waiting-input`: a command prompt or a dialog waits for an answer (under 15 s for a dialog).
- `busy`: working but not answering (stale heartbeat with the process alive, or `IsHungAppWindow`).
- `error`: the writer's work ended in an error it wants seen.
- `running`: a test, script or command is running normally.
- `idle`, `stopped`.

## Compatibility rules

- Fields are only added, never renamed or retyped. Readers ignore unknown fields and unknown sources.
- A breaking change is a new file name (`<source>.v2.json`), written next to the v1 file for at least one release.
- A writer never fails its real work because a status write failed (catch, log once, carry on).

## `detail` of `acad` (the AcadStatus add-in)

```json
{ "doc": "fx-all.dwg", "docs": 2, "cmdActive": 1, "cmdNames": "OFFSET",
  "prompt": "Specify point on side to offset or [Exit/Multiple/Undo] <Exit>:",
  "dialog": { "title": "AutoCAD Message", "class": "#32770", "modal": true,
              "texts": ["; error: bad argument type: numberp: nil"], "buttons": ["OK"], "since": "2026-11-05T10:12:01Z" } }
```
`dialog` is absent when none is open. `prompt` is absent when no prompt is pending.

## `detail` of `retrace`

```json
{ "mode": "replay", "test": "smoke040", "step": 4, "steps": 5, "stepText": "BREAK", "suite": { "index": 3, "count": 7 },
  "needsPerson": "a dialog Retrace cannot fill: Plot" }
```
`mode`: `record` | `replay` | `suite` | `tdoc` | `idle`.

## `detail` of `acad-pilot`

```json
{ "script": "run", "cmds": "ZOOM E|-RETRACEMARK plan", "stallSec": 30 }
```

## Example (`retrace-4812.json`)

```json
{ "v": 1, "source": "retrace", "writer": "Retrace 0.6.7", "pid": 4812, "updated": "2026-11-05T10:14:03Z", "ttlSec": 15,
  "state": "running", "short": "Suite 3/7", "summary": "Suite 3/7: smoke040, step 4/5 (BREAK)", "owner": "COM",
  "since": "2026-11-05T10:13:40Z", "detail": { "mode": "suite", "test": "smoke040", "step": 4, "steps": 5 } }
```
