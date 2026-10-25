# Retrace.Samples

A tiny sample plugin to practise the Retrace flow on: record a manual workflow, replay it as a test, and
see the same commands in the guide that Retrace generates from this source. Not a product.

Target: AutoCAD 2026 (R25.1), net8.0-windows, `AutoCAD.NET` 25.1.0. Build: `dotnet build -c Release`
(produces `Retrace.Samples.dll`). It is not part of the release bundle: `NETLOAD` it, or copy it next to `Retrace.dll` for a VM test.

## Commands

| Command | Prompts | Draws |
|---|---|---|
| `DRAWLINE` | start point, end point (rubber band from the start) | a LINE |
| `DRAWCIRCLE` | center, radius (drag from the center; Enter = last radius, default 5) | a CIRCLE |
| `DRAWRECT` | first corner, opposite corner | a closed LWPOLYLINE |
| `DRAWLABEL` | text (spaces allowed), insertion point, height (Enter = last, default 2.5) | a single-line TEXT |
| `DRAWGRID` | dialog (rows, columns, spacing, layer name; OK/Cancel), then the lower-left point | rows x columns POINT objects on the layer (created if missing) |

Each command works in one transaction and prints one confirmation line. Esc at any prompt (or Cancel in the
dialog) draws nothing. The **Retrace Samples** ribbon tab has a "Draw" panel (Line, Circle, Rectangle, Label) and a
"Dialogs" panel (Grid...). The buttons run the command as `^C^C_DRAWLINE `, so a recording shows the command.

`DRAWGRID` is the one with a dialog (WinForms, `GridDialog`): in a replayed test that step needs a person.

## Practice workflow

1. In the Retrace ribbon tab (see `../../README.md`): Record (name the test, e.g. `sample-basic`).
2. Retrace Samples tab > Draw > Rectangle: pick two corners.
3. Add a Checkpoint. Retrace Samples > Draw > Label: type `Room 1`, pick a point, Enter for the height. Add a Checkpoint.
4. Retrace Samples > Dialogs > Grid...: set values, OK, pick a point (the dialog is the manual step).
5. Stop, then Run test on the recording; then build the docs from this folder (Retrace ribbon > Build docs) and compare it.
