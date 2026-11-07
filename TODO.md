# Retrace: pending ideas

Open work that is known but not scheduled. Pick an item, do it on an `agent/claude/<topic>` branch, and delete
its line here in the same PR. Released limits live in the README's Limitations section; this file is for what
could be done about them. Started 2026-11-07 after the 0.6.0 "untested paths" round (#311, #312).

## Features

- **WPF tab controls in dialogs** (about a day): record each field's tab and show the tab before setting it,
  reusing the WinForms `TabControl` logic. Today replay fills only the visible tab and stops for a person on the
  others, with a tab hint (#311).
- **WPF tree views in dialogs** (about a day): selected item and check boxes, reusing the Win32 / WinForms tree
  logic. Today the choice is not repeated; the replay note says "unverified" (#311).
- **WPF grids and multi-select lists in dialogs**: still unsupported, only flagged in `dialog_values.unsupported`.
- **List view as a Scan field kind**: the scanner and the docs do not know list views as fields yet (follow-up
  named in the #312 PR body).
- **List view key with a tab part**: two list views with the same control id on different property-sheet pages
  are not told apart (documented limit in `docs/recording-format.md`).
- **List scrolled sideways**: a list view scrolled past column 0 is right-clicked outside its rows (documented
  limit). Scroll it back, or hit-test the visible part of the row.
- **Menus shown with `TPM_RETURNCMD`** are not recorded.
- **A tree node's own `ContextMenuStrip`** and **an auto-hidden palette's context menu**: not supported or not tried.
- **Localized AutoCAD**: a typed command name that differs from the global one falls back to the old "unknown
  command" error.

## Not yet run in AutoCAD (unit tests only)

- Menu on a list view with several rows selected, and the over-100-rows truncation.
- An unnamed list view (`ord:N` key).
- The keyboard `WM_CONTEXTMENU` fallback, and the other list-view fallbacks.
- Static texts of a context menu opened in a WPF dialog.
- The once-per-window cap log of the WPF part scan.
- A replay of a recording whose dialog has `unsupported` parts (the segment `note`).
- From 0.6.0: a tab control inside a page of another one, Tdoc with tabs in a palette, a tree in an auto-hidden
  palette or a Tdoc run, a tree over 1500 items, a nested dialog that opens and closes within about 120 ms
  (missed), other AutoCAD releases.

## Samples and VM test kit

- `DRAWPALETTEHIDE`: the sample palette's tree keeps its state, so a second replay in the same AutoCAD session
  fails. Reset the tree when the palette opens, so tests need no AutoCAD restart.
- The VM's shared `ui-act.ps1` is an old copy; agents used their own. Update the shared one from `tools/vm`.
- A PASS / FAIL message box left open after a replay blocks the next run. Close it in the test scripts, or offer
  a setting that skips it in unattended runs.
- `trust-retrace.ps1` cannot run over ssh on the VM (script execution disabled for that session); call it with
  `-ExecutionPolicy Bypass` in the VM scripts.
- `DRAWMENU` stock DIMSTYLE menu recording has no checkpoint, so a suite reports it INCOMPLETE; re-record it with
  one.
