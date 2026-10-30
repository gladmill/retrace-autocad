# Tdoc: a training document from a list of tests

`RETRACETDOC` replays recorded tests one after another and writes a Word document with pictures of AutoCAD at each step, for people who have to learn the flow.
The tests are the ones in the Tests folder; nothing is recorded again.

## Start

- Ribbon Retrace > Test > Training doc, or `RETRACETDOC`: a window with the tests (newest first, a check box each). Move up / Move down set the order, the title box names the document, Start begins, Settings opens the settings.
- `-RETRACETDOC`: asks for the title, then the tests, separated by `;`. A test is `last` (the newest), a test folder name (`2026-10-10_0243_dtest`), a test's name without its date (`dtest`; the newest of that name), a path to a test folder or its `recording.jsonl`, or `@file` with one of these per line (blank lines and lines starting with `#` are skipped). If any name finds no test, nothing starts and the names that failed are listed.
- Start needs an open drawing, no recording running and no replay running. The first test is started from the drawing that is active.

## What a run does

For each test Retrace types `_RETRACERUN` into the active drawing and the usual Run test path takes over: a new drawing, `Replayer` in interactive mode with `autoFillDialogs` on (turn it on: with it off dialogs are not filled in and get no picture), no verdict box between tests.
The replay window shows `Tdoc test i/n` in front of its heading. When a test ends, the next one starts. After the last one the manifest is written at once and the Word file in the background (the engine); a message on the command line says where.

Pictures are taken at these points:

| When | What the picture shows | Boxes |
|---|---|---|
| Before a ribbon, command, LISP or open step | AutoCAD's window. The step's ribbon tab is activated first so the button shows. | 1: the ribbon button (found in the ribbon's own WPF tree: the shown element whose DataContext is the ribbon item). When a narrow window has collapsed its panel into one button, the box goes on that panel and the legend says to open it first. Next: the command line, when the step types something (the command, or answers at prompts). |
| A dialog, filled in, before OK / Cancel is pressed | The dialog window. | One per field that was filled (in recorded order), then the button that closes it, named by its own text. A message box (a LISP `alert`) is pictured and pressed too, also when its command showed it right after the dialog before it. |
| Before a palette step | The palette (its frame), or AutoCAD's window when the palette is hidden. The step has not run yet: the picture shows where the values go. | The WinForms controls the step sets, and its button. WPF and native palettes get no boxes. |
| Before a checkpoint | AutoCAD's window, after ZOOM Extents (the view only; the checkpoint compares objects, not the view). | None. The result (PASS, FAIL and the differences) goes under the picture. |

A box is numbered only when it was found on the screen; the legend under the picture lists the numbered ones (`1  Click Create Block`, `2  Name: KitBlock`, `3  OK`). The text before the picture says the same in sentences ("Click Create Block on the Insert tab.", "In the Block Definition dialog:", "Set Name to KitBlock.", "Click OK.").

Before each picture Retrace waits `tdocStepDelayMs`, then hides its own replay window for a moment (it comes back right after) and waits for AutoCAD to be idle. Pictures of AutoCAD's window are cut to the monitor's work area, so the task bar is not in them.

## Settings

| Key | Default | |
|---|---|---|
| `tdocBringToFront` | `true` | Bring AutoCAD's window (and the dialog being filled) to the front before each picture, move a dialog that is off the screen onto it, and hide the replay window for the picture. |
| `tdocAnnotate` | `true` | Draw the green numbered boxes and write the legend. Off: plain pictures. |
| `tdocStepDelayMs` | `1500` | Pause before each picture, 0 to 10000. Raise it when tool tips or half-drawn dialogs show up in pictures. |

## Files

```
C:\temp\retrace\docs\training\
  2026-10-30_14-05-09_Block training.docx        the document
  2026-10-30_14-05-09_Block training\
    tdoc.json                                     what the run produced (format: recording-format.md, "tdoc.json")
    pictures\t1-s3-1.png                          test 1, step 3, first picture of that step
```

The title is part of both names, with characters Windows does not allow replaced by `-` and at most 60 characters. A folder that exists already gets `_2`, `_3`. The Word file is the engine's `EngineRunner.WriteTrainingDoc(tdoc.json, docx)`; to make it again from an existing manifest, call it with the same paths. A Word file that is open in Word is not overwritten: `... (new).docx` is written next to it.

## Stopping

Stop in the replay window ends the run: the document is written with the tests so far (the stopped one included, verdict `INCOMPLETE`) and its title ends in "(incomplete)". Between two tests `RETRACETDOC` ends the run. Closing AutoCAD writes `tdoc.json` only.

## Limits

- Leave the mouse and keyboard alone during a run. A click or a tool tip ends up in a picture, and windows are brought to the front.
- A dialog is pictured only when Retrace fills it in itself. When it cannot (a value does not go in, `autoFillDialogs` is off) a person finishes it as in Run test, there is no picture of it, and the step text still lists the recorded values.
- The command line box needs AutoCAD's command line window: found as the WPF window that holds AutoCAD's command line control, or by the title "Command Line"; if neither, the picture has no box for it.
- A ribbon button in a panel slide-out or on a tab that cannot be shown has no box (a collapsed panel gets the box, see above).
- A palette that is auto-hidden is pictured as part of AutoCAD's window, without boxes. A palette step is pictured before it runs, so the values it sets are not on the picture.
- Each test leaves its new drawing open, as Run test does. Retrace > Tools > Close all drawings closes them.
- Pictures are scaled down to at most 1400 pixels on the long side.
- A recorded answer AutoCAD reported as an error (Enter at an empty selection: `status` Error) is not typed again; the replay waits at that prompt for a person, as in Run test.
- Tested on AutoCAD 2026 in a VM (October 2026): a LISP command with a DCL dialog and its alert, a ribbon button, typed prompts and checkpoints, with AutoCAD covered by another window; the document opened with two tests. Not tested: palettes, WPF dialogs, more than one monitor, display scaling above 100 %.
