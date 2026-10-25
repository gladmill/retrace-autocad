# Docs folder layout

`RETRACEDOCS` (the Build docs button) runs the engine on the source folder and writes the docs folder: setting `docsFolder`, default `C:\temp\retrace\docs`.
Use the same docs folder every time. A different folder starts the guide over, because your edits live in the old one.

Input, from settings: `sourceFolder` (the code, `.cuix`, `.rc`, XAML and `.dcl` files; required), `capturesFolder` (real dialog pictures, used when the folder exists)
and `testsFolder` (recorded tests, used when the folder exists; they add the recordings pages and the how-tos).
The source folder is read, never changed. Files whose names look like secrets are listed as skipped and not opened. `bin`, `obj` and `.vs` folders and binaries are skipped.

```
docs\
  README.md                       start here: counts, skipped files, warnings
  guide\                          YOU EDIT THIS
    index.md                      contents of all ribbon pages, the how-to list, an intro
    ribbon\cuix\<file>\<tab>.md   one page per ribbon tab from a .cuix file
    ribbon\code\<project>\<tab>.md  one page per ribbon tab built in code
    howto\<test>.md               one how-to per recorded test
    images\captures\<form id>.png real dialog pictures (copied from the captures folder)
    images\howto\<test>\*.png     the recorded screenshots that the how-to shows
  reference\                      GENERATED, never edit: rewritten on every run
    projects.md                   the scanned projects and files
    ribbon\index.md               ribbon tabs; ribbon\cuix\...\<tab>.md, ribbon\code\...\<tab>.md
    commands\index.md             every command entry point; commands\<project>.md with the UI flow of each
    dialogs\index.md              dialogs, windows and palettes; dialogs\<project>\<form>.md
    images\ribbon\...svg          wireframes of ribbon tabs
    images\dialogs\...svg         wireframes of dialogs
  test-plans\                     GENERATED manual test scripts written from the source
    index.md                      ribbon\..., commands\<project>.md, dialogs\<project>.md
  recordings\                     GENERATED from the tests folder
    index.md                      most used buttons, repeated sequences, ribbon coverage
    <test>.md                     one page per recorded test, named after its test folder
    images\<test>\*.png           the test's screenshots
  data\
    sourcemap.json                every UI element: source location, wireframe, reference page, how to reach it, capture hint
    model.json                    the scanned model
    recordings.json               the recordings analysis (only with recorded tests)
```

Page names are slugs of the tab, form or project name. A clash gets a longer name (tab id, form kind) so no page overwrites another. Links between pages are relative, with `/`, so the folder can be moved.

## What the owner edits

Only `guide\`. Everything else is rewritten on every run: edits there are lost. Reference pages hold the technical detail; guide pages hold the same tab, panel and button sections plus your own words.

### Guide merge rules

Every page in `guide\` has engine blocks and owner text.

- Engine text sits between `<!-- engine:begin KEY -->` and `<!-- engine:end KEY -->` lines, each alone on its line. Keys are `page`, `panel:<panel id>` and `button:<source map id>` on tab pages, and `page` and `step:<kind>:<what>:<n>` on how-tos.
  Do not edit inside a block; the next run replaces it.
- Everything outside the blocks is yours. The engine never changes it, and a second run on unchanged input changes nothing.
  A new tab page gets an italic stub after each block (for example "What does Line do ..."): write there. A new how-to gets one stub after its title block and none after its steps; text you write between steps is kept.
- Move sections around freely. The engine keeps the order it finds. A new button, panel or tab is inserted before the next block that already is on the page (after your text), or at the end.
- When a button, panel or step disappears from the source or the recording, its block is replaced by a note, "Removed from the source. Delete this section when no longer needed; your text below is kept.", and your text stays. Delete the section yourself when you no longer need it.
- Broken markers on a page (missing, duplicate or mismatched begin / end lines): the engine leaves the page alone, prints a warning, and writes the fresh version as `<name>.engine-new.md` next to it. Fix the markers and run again.
- Pages for tabs that no longer exist are left where they are.
- Saving a page in the guide palette keeps its line endings and writes UTF-8 without a BOM. If you change text inside an engine block the palette warns you that the next build replaces it.

### How-to pages

One page per recorded test, `guide\howto\<test>.md`, built from the recording with the same wording as `steps.md` ("Click **Home > Draw > Line**", "Type `CIRCLE` on the command line and press Enter", then one line per prompt answer, then "**Check:** ..." for each checkpoint).
A dialog step shows the screenshot taken while recording; without one it shows the capture of that dialog, else its wireframe. Recorded value changes are listed under it.

- Step keys are `step:<kind>:<what>:<n>`. `kind` is `ribbon`, `command`, `lisp`, `dialog`, `mark` or `doc`. `what` is the ribbon item id, the command name, the dialog title or the checkpoint note, as a slug. `n` counts earlier steps with the same kind and what.
  A key does not depend on the step's position, so inserting a step in a re-recording leaves every other block alone. If the item, command or note itself changes, the old block turns into a removed note and a new block appears.
- Each button section on a tab page starts with an anchor `<a id="b-<label slug>">` and gets a line "**Used in:** [how-to](...)" for every how-to that clicks it. That is what the how-to links point at.
- A ribbon click is matched to the tab page by item id, tab id (the part before the last `.` is ignored), tab title, panel title and text, and finally by the macro's command.

## Captures

The captures folder holds real pictures of dialogs: `<Stem>__<Name>.png` and a `.json` next to it with a `formId`. They come from `tools/dialog-capture` (Win32 `.rc` dialogs, run on Windows) and `RETRACECAPTUREDCL` (DCL dialogs, inside AutoCAD, which writes `<stem>__<dialog>.png` + `.json`).
A form with a capture shows it in the guide (copied to `guide\images\captures\<form id slug>.png`, a clash gets `-2`); a form without one shows its wireframe.
Captures are data: do not commit them.

## Wireframes

Wireframes are SVG pictures drawn from the source, not captured from AutoCAD. Each element carries a `data-source="file:line"` attribute. Win32 `.rc` and WinForms wireframes are drawn at true 100 % / 96 DPI size (1 SVG px = 1 screen px); only XAML and DCL auto layouts are scaled.
Dialog units come from the `FONT` line, and text is measured with per-character widths (`src/Retrace/Engine/Util/FontMetricsData.cs`, generated by `tools/gen_font_metrics.py`). `tools/compare_captures.py` scores wireframes against real captures.
CUIX icons are resource ids inside `acad.exe`, so the wireframes cannot show the real icons.

## Screenshots of recordings

The engine copies recorded screenshots into the docs folder (never links them in place), so the docs folder can be moved: how-to pictures to `guide\images\howto\<test>\<file>`, recordings-page pictures to `recordings\images\<test>\<file>`.
A file is copied only when its bytes differ, so a run on unchanged input changes nothing. A missing picture is left out with a warning.
