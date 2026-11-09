# Retrace: pending ideas

Open work that is known but not scheduled. Pick an item, do it on an `agent/claude/<topic>` branch, and delete
its line here in the same PR. Released limits live in the README's Limitations section; this file is for what
could be done about them. Started 2026-11-07 after the 0.6.0 "untested paths" round (#311, #312).

## Features

- **MSAA / UI Automation readers for the MFC Feature Pack parts** (`CMFCTabCtrl`, `CMFCPropertyGridCtrl`, `CMFCToolBar`, `CMFCColorButton`'s colour) (the three Feature Pack parts are flagged as unsupported today: `dialog_values.unsupported`, a replay note, and not recorded). Their windows answer `IAccessible` (MSAA) with the tab names, properties and buttons; the work is reading them from the UI thread and replaying with `accDoDefaultAction`. Not done in the MFC controls PR.
- **`CCheckListBox` check marks are not recorded and not flagged.** It is an owner-drawn `ListBox` (`LBS_OWNERDRAWFIXED | LBS_HASSTRINGS`) like any other, and MFC answers `LB_GETITEMDATA` / `LB_SETITEMDATA` itself with the program's own data, so no message gives away its check state or even that the box has check marks (checked on the VM: after ticking an item, `LB_GETITEMDATA` from another process returned 0 for every item). Only its selected item is recorded (a `list` field); a test that ticks items shows the difference at its checkpoint. Ways in: MSAA (the check boxes are `IAccessible` states of its items), or reading `CCheckListBox`'s data through the MFC runtime of the add-in (not reachable from outside: the handle map belongs to the add-in's module state).
- **Docked and floating MFC toolbars**: a toolbar's replay posts `WM_COMMAND` to its parent, as the control does, but its notify window can be another one (`TB_SETPARENT`, a toolbar docked in an MFC docking bar `AfxControlBar`, floating in a mini frame) and no message reads it. Only a toolbar directly in a frame or a dialog was tested. After posting, the replay cannot tell whether the command ran (a disabled-by-the-program command, a handler elsewhere); the checkpoint after the step decides. Reading the notify window needs the program's own `CToolBar::GetOwner`, not reachable from outside.
- **`RichEditD2DPT`** was not exercised in AutoCAD: the class is not registered on the VM (Windows 11 ARM64 build there), so the MFC sample's D2D box is not created. It is read as an edit by its class name.
- **Date picker with a custom format** (`DTM_SETFORMAT`, e.g. `yyyy-MM-dd HH:mm`) is read by its style (date or time), so the half it does not show is not recorded.

- **WPF tab controls in dialogs** (about a day): record each field's tab and show the tab before setting it,
  reusing the WinForms `TabControl` logic. Today replay fills only the visible tab and stops for a person on the
  others, with a tab hint (#311).
- **WPF tree views in dialogs** (about a day): selected item and check boxes, reusing the Win32 / WinForms tree
  logic. Today the choice is not repeated; the replay note says "unverified" (#311).
- **WPF grids and multi-select lists in dialogs**: still unsupported, only flagged in `dialog_values.unsupported`.
- **List view key with a tab part**: two list views with the same control id on different property-sheet pages
  are not told apart (documented limit in `docs/recording-format.md`).
- **List scrolled sideways**: a list view scrolled past column 0 is right-clicked outside its rows (documented
  limit). Scroll it back, or hit-test the visible part of the row.
- **Menus shown with `TPM_RETURNCMD`** are not recorded.
- **A tree node's own `ContextMenuStrip`** and **an auto-hidden palette's context menu**: not supported or not tried.
- **Localized AutoCAD**: a typed command name that differs from the global one falls back to the old "unknown
  command" error.

- **SDI = 1 follow-ups**: the reason a text sent with `Document.SendStringToExecute` is sometimes lost in a drawing Retrace
  opened (the replay types through `RETRACETYPE` instead); "the recorder sees no dialogs under SDI" was not reproduced
  (5 recordings had `modal_enter`); not run in AutoCAD with SDI = 1: the Open test window, xrefs, `RETRACETDOC` with palettes,
  the stops for a drawing opened outside the replay's own open step.
- **SDI = 1, not run in AutoCAD** (merged with these limits, #318): the 22-recording regression set (only run with SDI = 0);
  AutoCAD Electrical and other plug-ins that open drawings themselves; `RETRACETDOC` beyond two tests; the cancel's retry
  path and a failing COM `SendCommand`; the end-of-run save while another dialog is open beside the verdict box (unit
  tests only). Text that answers a running command still goes through plain `SendStringToExecute`. Starting AutoCAD with
  SDI = 1 and switching to 0 live makes style-dup5 / style-x4 fail even on the baseline (passes after a fresh start).
- `-RETRACESUITE` started from a `.scr` file doesn't start its first test ("did not start within 90 s"): the `RETRACERUN`
  it sends is not run while the script is still active. The second test runs once the script ends. Found while
  verifying #316.
- The Step window (`RETRACESTEP`) opens behind AutoCAD's main window when the command is started over COM, with SDI 0
  and 1. Check it from the ribbon on a real PC, and bring the window to the front if needed.
- Script chains whose `SCRIPT` is started by a typed command, a palette button or a typed LISP still plan one step per
  script command (#316 covers ribbon clicks only).

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
- The VM's 0.6.0 recording `rec5` of the `DRAWMENU` sample fails since #312 added a list view to that dialog (one more TEXT
  at the checkpoint; it passes with the old sample). Re-record it against the current sample.
- `DRAWMENU` stock DIMSTYLE menu recording has no checkpoint, so a suite reports it INCOMPLETE; re-record it with
  one.

## Script chains

- Owners other than a ribbon click (a typed `SCRIPT` command, a palette button that runs `SCRIPT`, a typed LISP that runs it) plan as before: the `script` flag of the recording already tells which commands the script ran
  (`Plan.ContinuesChain`), so the chain rule could own them, once the replay can run such a step and skip what the script runs.
- The replay types what a person answered inside a chain's script (points, angles, numbers, keywords, Enter; a drag is one answer) when the command asks for a prompt with the same message. Still a stop for a person: object picks and selections (they
  need the view and handles), Esc, text with a space, and a prompt that does not come within 20 s. A chain whose command opens no dialog is not a chain here (an answer before the first dialog splits the click), so answers of such a script are not delivered.
- Drag samples: `acedDragGen` through P/Invoke was not tried (a .NET `EntityJig` already shows the pattern). A claimed result of a plain prompt that follows a result of the same prompt (a retry) is only claimed by Enter, Space or a mouse button; dynamic input that
  accepts a value with Tab or a comma alone is not seen as ending the prompt, so that result stays `input: false`. Samples of a script-fed jig (text sent with `SendCommand`) are `input: false` and collapse to the last one in an older recording.
- **Stuck replay between two dialogs** (reported with an MFC add-in whose Add button closes a window and opens a dialog): not reproduced. The sample `DRAWCATALOG` does the same and replays fine; AutoCAD reports the first dialog's leave before the second's enter. The report is better explained by the list view selection not being recorded (now a field). Look again with the add-in if it recurs.
- **Slow dialog in a 0.6.4 or earlier recording**: a dialog that took more than 300 ms to come to the front has no `modal_title` event, so the plan reads its modal state as windowless (main window title) and the dialog is "unexpected" on replay. Not fixable from the recording; record it again.
