# vm

Scripts to drive AutoCAD 2026 in a Windows machine or VM that you can reach over SSH, from a Mac or Linux box. Use a VM that holds only test drawings: tests restart AutoCAD and close drawings without saving.

`vm` below stands for your ssh host. `run-test.sh` reads it from the environment variable `RETRACE_VM_HOST` (default `retrace-vm`, an example alias: define it in `~/.ssh/config`). The scripts live in `C:\retrace-vm\` on the VM and write to `C:\retrace-vm\out\`. Copy them with `scp tools/vm/* vm:C:/retrace-vm/`.

Requirements on the VM: Windows with the OpenSSH server, AutoCAD 2026 with the Retrace bundle installed, and a user logged on to the desktop (the scripts that need AutoCAD or the screen run in that interactive session).

SSH runs in session 0, which has no desktop. Anything that needs AutoCAD or the screen therefore runs as a one-shot scheduled task
in the interactive session (`schtasks /create ... /it`, `/run`, then `/delete`).

| Script | What it does |
|---|---|
| `run-in-session.ps1 -Cmds "A\n\|B\n" [-Query q.ps1] [-WaitSec n] [-StallSec n]` | From SSH: runs `acad-cmd.ps1` in the interactive session and prints its result. Each run has an id (`C:\retrace-vm\out\run.txt`); a worker left over from a timed-out or stopped run sends and writes nothing more. `-StallSec n` (off by default, because `RETRACEREPLAY` keeps the command line busy on purpose) stops the run when a command has run n s and AutoCAD answers COM as busy: `STUCK`, exit 4, AutoCAD left as it is. |
| `acad-cmd.ps1` | Attaches to the running AutoCAD over COM. Runs `-Query` (a PowerShell file with `$acad`/`$doc` in scope, for read-only COM queries), sends each `\|`-separated command with `SendCommand` (`\n` = Enter), and writes the command-line log tail and a screenshot to `C:\retrace-vm\out`. Commands go through `C:\retrace-vm\cmds.txt`, because `schtasks /tr` mangles spaces, quotes and parentheses. |
| (recording from a script) | End a recording sent over COM with `-RETRACESTOP`, not `RETRACESTOP`: the plain command opens the modeless "Name your checkpoints" window on the test machine (verified: COM `SendCommand` is not seen as a script, CMDACTIVE 1). `RETRACEREPLAY` over COM always needs a path (an empty answer opens a file dialog). `-RETRACEMARK` for checkpoints. No script here stops a recording yet. |
| `com-replay-test.sh` | Runs on your Mac or Linux box: regression test that a replay started over COM (`RETRACEREPLAY <path> Auto` through `run-test.sh`) really runs its typed commands. Replays a small recording (CIRCLE, LTSCALE 7.25, `-LAYER Make RTCOM`) in a new drawing, then reads the drawing back over COM: 1 entity, LTSCALE 7.25, layer RTCOM. Exit 0 PASS, 1 the drawing did not change, 2 infrastructure. |
| `q-ents.ps1` | Query: model space objects by type and some system variables. |
| `run-test.sh <test folder or recording.jsonl> [--new-drawing] [--run]` | Runs on your Mac or Linux box: copies the recording to the VM (`C:\temp\retrace\tests\<folder>\`), replays it unattended (`RETRACEREPLAY <path> Auto`; with `--run` `RETRACERUN <path>`, which opens its own drawing), waits for `results\latest.json`, copies it back, prints the verdict and checkpoints. Exit 0 PASS, 1 FAIL/INCOMPLETE/ERROR, 2 infra. |
| `q-newdoc.ps1` | Query: opens a new drawing (`$acad.Documents.Add()`; with SDI = 1, where Add is not available, a copy of the template, and only when the open drawing has no unsaved changes). Run it in a call of its own: later commands go to the document that was active before the query. |
| `q-netload.ps1` | Query: NETLOADs Retrace.dll from the installed bundle with `FILEDIA` off. AutoCAD then asks once whether to load the unsigned dll; a person answers that. |
| `esc.ps1` | Brings AutoCAD's frame to the front and presses Esc twice, without COM (leaves a prompt). Writes `C:\retrace-vm\out\esc.txt`; `foreground: False` there means Esc was only posted, so check with a query or `shot.ps1`. |
| `shot.ps1` | Screenshot without COM. |
| `wait-acad.ps1 [-TimeoutSec 240]` | From SSH: returns as soon as AutoCAD answers over COM and is quiescent (`ready after Ns`, exit 0; exit 1 on timeout with the last state). Uses `acad-ready.ps1` as a one-shot task. `start-acad` already ends with it; use it instead of sleeping or watching window titles: from session 0 `MainWindowTitle`/`MainWindowHandle` read empty/0, and the Start tab has no title anyway. |
| `run-hidden.vbs <command…>` | Runs a command with no window (`wscript //B //Nologo C:\retrace-vm\run-hidden.vbs powershell … -File x.ps1`). Every task here launches through it: `powershell -WindowStyle Hidden` still flashes a console window (a Windows Terminal window where Terminal is the default console host). Don't start AutoCAD itself through it (it would start hidden); `start-acad` runs a hidden `cmd` whose `start` shows AutoCAD normally. |
| `acad-ready.ps1` | Task-only: writes `ready`, `busy` or `none: <reason>` to `C:\retrace-vm\out\ready.txt`. |
| `start-acad.ps1` / `.cmd` | Starts AutoCAD in the interactive session. Returns when it is ready (runs `wait-acad.ps1`). AutoCAD's first start may need a person for its sign-in and start-up screens. |
| `wincancel.ps1` | Posts IDCANCEL to a top-level window by title. |
| `ui-act.sh "<action>" ...` / `ui-act.ps1` | Runs on your Mac or Linux box: drives AutoCAD's UI in the interactive session for palette and checkpoint tests (real mouse clicks (also `rawel <AutomationId>`, a real click on a button found by UI Automation) and hovering, keys, the checkpoint shortcut, posted clicks, UI Automation, window lists) and prints what it did. The actions are listed at the top of `ui-act.ps1`. |
| `ui-menu.sh "<action>" ...` / `ui-menu.ps1` | Runs on your Mac or Linux box: like `ui-act.sh`, but finds controls by UI Automation (`rclick TreeItem\|Hammer`, `lclick ListItem\|Wrench`, `menuitem Edit`, `menuitems`) and sends real keys, for context menu tests. `vk 5D` presses the Menu key by virtual-key code (SendKeys has none); Shift+F10 is `key +{F10}`. A Win32 popup menu (`TrackPopupMenu`) is not always found as a MenuItem: click its item with `ui-act.sh rawmouse x,y`. |
| `burst.ps1 [-Count 14]` | Task-only: a screenshot every 1.5 s to `C:\retrace-vm\out\burst-NN.png`; start it, then a replay, to see the screen while steps run. |
| `marks.sh <test folder>` | Runs on your Mac or Linux box: the checkpoints of a recording on the VM, with what the checkpoint shortcut found at the cursor. |
| `install-samples.sh [--remove]` | Runs on your Mac or Linux box, with AutoCAD closed: builds the test add-ins (`samples/Retrace.Samples`, `samples/Retrace.PartsFinder`) and installs them as their own bundle, `%APPDATA%\Autodesk\ApplicationPlugins\Retrace.Samples.bundle`, which AutoCAD loads at every start (ribbon tabs "Retrace Samples" and "Parts"). A separate bundle, because a Retrace build rewrites `Retrace.bundle`. Until a person trusts the folder (the script prints the `trust-retrace.ps1` command), AutoCAD asks at each start whether to load the two dlls. |
| `dialog-watch.ps1` | Task-only: watches AutoCAD for dialogs and message boxes every 500 ms. Writes the ones open now to `out\dialogs-open.txt`, each opened / closed dialog (title, buttons, texts) to `out\dialogs.log` and a picture of it to `out\dialogs\`. Optional `C:\retrace-vm\dialog-rules.txt`, lines `<title regex> => <button text>`, presses that button once; security prompts are never answered. |
| `dialogs.sh [n] \| --start \| --pic <file>` | Runs on your Mac or Linux box: the dialogs open in AutoCAD now and the last n log lines; `--start` runs the watcher until logoff; `--pic` copies a dialog picture back. Check it when a script times out waiting for AutoCAD. |
| `install-dialog-watch.ps1` | Run once on the VM by a person: runs the watcher at every logon (task `AcadDialogWatch`). |

Run the task-only scripts (`esc.ps1`, `shot.ps1`) like this:

```powershell
schtasks /create /tn AcadEsc /tr "wscript //B //Nologo C:\retrace-vm\run-hidden.vbs powershell -NoProfile -ExecutionPolicy Bypass -File C:\retrace-vm\esc.ps1" /sc once /st 00:00 /it /f /rl limited
schtasks /run /tn AcadEsc; Start-Sleep 3; schtasks /delete /tn AcadEsc /f
```

Lessons learned:

* A WPF dialog (e.g. "a Retrace dialog") ignores `wincancel.ps1`; post WM_CLOSE to it by title instead.
* Scripts here must be run with `powershell -NoProfile -ExecutionPolicy Bypass -File …` (PowerShell script execution is disabled by default on Windows).
* COM calls hang or are rejected (`RPC_E_CALL_REJECTED`) while AutoCAD waits at a prompt or shows a modal dialog. Never leave a
  command half-answered. `esc.ps1` gets AutoCAD out of a prompt; `shot.ps1` shows what is on screen.
* Avoid typing AutoLISP through this chain: quotes break on the way, and an unterminated string leaves AutoCAD waiting for input.
  Read state with a COM query file instead.
* Real input from an interactive one-shot task (`SetCursorPos` + `mouse_event`, `keybd_event`, SendKeys) reaches the desktop (VM with a
  logged-on user, 2026-10-27). Posted window messages reach WinForms buttons but do not press WPF buttons.
* To take the foreground a hidden task taps Alt: that shows the ribbon's key tips (they eat the next letters: press Esc) and rolls up an
  open auto roll-up palette. `ui-act.ps1`'s `chordraw`, `raw` and `rawmouse` skip it.
* `SetVariable` over COM needs the variable's type, e.g. `[int16]0` for `DYNMODE` and `FILEDIA`.
* SDI = 1 (a stored setting): `Documents.Add` throws "Method not available in SDI mode" (`acad-cmd.ps1` and `q-newdoc.ps1` open a copy of the template instead), opening a drawing replaces the one that is open, and a COM call made while a "Save changes?" question or a dialog is open hangs, as everywhere. Set it from a query (`$doc.SetVariable("SDI", [int16]1)`) while one drawing is open; AutoCAD 2026 refused it with several open.

## Fixtures

Test drawings that hold every kind of thing a Retrace checkpoint can verify, plus one-change mutations to prove a detector sees each of them. They are built on the VM from a new drawing (no binary is in git) by `fixtures.sh`, which uploads the scripts to `C:\retrace-vm\` and runs them through `run-in-session.ps1`. AutoCAD must be running in the interactive session (`start-acad`).

```sh
export RETRACE_VM_HOST=retrace-vm
tools/vm/fixtures.sh build [name]      # no name = every fixture; overwrites C:\temp\retrace\fixtures\fx-<name>.dwg
tools/vm/fixtures.sh list              # fixtures on the VM and the mutation names
tools/vm/fixtures.sh open all          # open fx-all.dwg (an open copy, mutated or not, is closed without saving first), then make each layout current once and go back to Model
tools/vm/fixtures.sh dump              # q-fixture-dump.ps1 on the active drawing
tools/vm/fixtures.sh mutate layers-freeze-dim   # run mut-layers-freeze-dim.ps1 on the active drawing
tools/vm/fixtures.sh check [name|all]  # per mutation: reopen fx-all, dump, mutate, dump, print the diff
tools/vm/fixtures.sh shot "A-101 Plan" out.png   # show a layout of the active drawing, copy the screenshot back (keep it out of git)
```

Files: `fx-build.ps1` (query; reads the fixture name from `C:\retrace-vm\fx-arg.txt`), `fx-lib.ps1` (the builders and helpers), `q-fx-open.ps1`, `q-fx-layout.ps1`, `q-fixture-dump.ps1`, `mut-*.ps1`, `noise-*.ps1`. A build takes 20-90 s; `check all` about 25 minutes.

| Fixture | Contents |
|---|---|
| `fx-layers` | behavior 1: 11 layers (ACI colours, one true colour 255,128,32, one DIC colour-book colour, DASHED / HIDDEN / CENTER, lineweights 0.13-0.70 and the default, one off, one frozen, one locked, one non-plotting) with 44 entities on them (line, circle, polyline, text each), 3 entity-level overrides |
| `fx-layouts` | behaviors 2 and 9: layouts A-103 Details, A-101 Plan, Z-Notes, A-102 Sections in that tab order (Letter landscape, DWG To PDF), each with a border, notes and the `FX_TITLEBLK` insert (7 attributes: TITLE, NUMBER, REV, DATE, DRAWNBY, INTERNAL invisible, DESC multiline) with its own values |
| `fx-viewports` | behavior 3: the layouts plus 1-3 viewports each (8 in all): scales 0.025-0.2, different view targets, one locked on A-101 Plan and one on Z-Notes, one off on A-102 Sections, per-viewport frozen layers (VPLAYER) |
| `fx-attrs` | behavior 9: the layouts and title blocks, no viewports |
| `fx-xdata` | behavior 4: apps FX_APP_A/B/C; xdata with codes 1000, 1040, 1070, 1071, 1010, 1002 on 4 model-space entities (one with two apps) and 2 paper-space entities |
| `fx-extdict` | behavior 5: 3 entities with extension dictionaries (Xrecords with mixed codes), dictionary `FX_DICT` under the named object dictionary with an Xrecord and a nested dictionary `FX_SUB` with 2 Xrecords |
| `fx-symbols` | behavior 6: text styles FX_Arial / FX_Narrow / FX_Times, dim styles FX_DIM1 (DIMSCALE 1) / FX_DIM2 (48, DIMTXT 0.125, _ARCHTICK) with a dimension each, blocks FX_INNER, FX_OUTER (nested), FX_TAG (2 attributes, 3 inserts), UCS FX_UCS_A / FX_UCS_B, regapps FX_EXTRA_REG1/2 |
| `fx-sysvars` | behavior 7: CLAYER DOORS, CELTYPE DASHED, CECOLOR 3, CELWEIGHT 25, TEXTSTYLE FX_Narrow, DIMSTYLE FX_DIM2, LTSCALE 2.5, PSLTSCALE 0, INSUNITS 4, LUNITS 4, LUPREC 3, AUNITS 1, AUPREC 2, MEASUREMENT 1, PDMODE 35, PDSIZE 2.5, MIRRTEXT 1, FILLMODE 0 |
| `fx-views` | behavior 8: 4 model-space named views and 2 paper-space ones (A-101 Plan, Z-Notes), over the layers and layouts |
| `fx-all` | all of the above in one drawing. The mutations are written for this one |
| `fx-picks` | five model-space lines for the pick-point tests (BREAK, TRIM, FILLET): `(0,0)-(100,0)`; cutter `(250,-30)-(250,30)` and target `(200,0)-(300,0)`; `(400,0)-(500,0)` and `(450,-50)-(450,50)`. Built by `fixtures.sh build picks` only (`q-fx-picks.ps1`); `fixtures.sh open picks` opens it |

`q-fixture-dump.ps1` prints one section per behavior (`[LAYERS]`, `[LAYOUTS]`, `[VIEWPORTS]`, `[XDATA]`, `[EXTDICT]`, `[SYMBOLS]`, `[SYSVARS]`, `[VIEWS]`, `[ATTRIBUTES]`) and a last `[NOISE]` section with view state. It names things instead of printing handles, so dumps of two builds compare equal. A mutation changes only its own lines in the dump. Two side effects, both restored: it makes each layout current once (a no-op on a built fixture, which is already visited) and each dim style current in turn. It no longer puts `PSLTSCALE` back: on a built fixture a layout visit must not change it, and the `PSLTSCALE=` value at the end of `[NOISE]` shows it if it did.

**Visited fixtures and sessions.** Two separate effects made a baseline recorded on a fresh fixture differ from a replay that switched layout tabs. Behavior capture no longer depends on either (the layouts' `psltscale` field and the viewports' `off` field are read from the saved DXF flags, `docs/recording-format.md`, "Session independence"; `PSLTSCALE` is not in the default `sysvars` list), so a baseline recorded on a plain open of `fx-all` replays clean after any layout visit. The notes below explain the AutoCAD behavior:

1. `PSLTSCALE` (below). `Save-Fixture` now makes every layout current once (paper space), sets `PSLTSCALE` to the fixture's value inside each layout, returns to Model, sets it again and saves. Rebuild after pulling this change; an older `fx-*.dwg` still flips.
2. Viewport `on` reads 0 until the layout has been current **in the running session**, whatever the file holds (checked: a fixture saved after visiting every layout still read `on = 0` right after opening). A visited file does not help, so `fixtures.sh open` (`q-fx-open.ps1`) makes each layout current once after opening and goes back to Model. Record the baseline after `open`. (Retrace's own `off` field is no longer affected (see `docs/recording-format.md`, "Session independence"); COM `ViewportOn` and the managed `Viewport.On` still are.)

The builder also registers a COM message filter (`FxMsgFilter` in `fx-lib.ps1`), which makes COM wait while AutoCAD is busy instead of throwing `RPC_E_CALL_REJECTED`; without it most builds failed on this VM, and `fx-build.ps1` retries a failed build up to 3 times.

**Why `PSLTSCALE` flipped (AutoCAD behavior, not a fixture bug).** AutoCAD saves `PSLTSCALE` per paper layout and loads the layout's own value when the layout becomes current (probed on new drawings from the default template: Layout1 held 0 and Layout2 held 1; Model keeps whatever value is current and does not reload one). Setting it while inside a layout stores it there. Setting it in Model is not free of effect either (checked later with DXFOUT): the variable stays backed by the flag of the last paper layout that was current, so `PSLTSCALE 0` typed in Model changes that layout's saved value. So on any drawing whose layouts carry different values, switching to a paper layout changes the system variable, including on a drawing a user made by hand. The old builder set 0 in Model only, so entering `A-101 Plan` loaded 1. A checkpoint compares the current value, so a baseline taken in Model and a replay that visits a layout differ on `PSLTSCALE` for such drawings (see `docs/recording-format.md`, "Session independence").

### Mutations

Each `mut-<area>-<what>.ps1` changes exactly one thing in the active drawing (open `fx-all` first) and prints what it changed. A detector that compares checkpoints should report the behavior in the second column, and nothing else.

| Mutation | Behavior it should trip |
|---|---|
| `appearance-color` | appearance (a model-space circle on DOORS: colour ByLayer -> 1) |
| `appearance-linetype` | appearance (same circle: linetype -> DASHED) |
| `appearance-lineweight` | appearance (same circle: lineweight -> 0.50) |
| `appearance-transparency` | appearance (same circle: transparency -> 50) |
| `paperspace-move-line` | paper-space entities, layouts (the DIM line of A-101 Plan moved 0.25) |
| `paperspace-text` | paper-space entities, layouts (the "Scale varies" note of A-102 Sections edited) |
| `attrs-invisible-visible` | attributes |
| `attrs-layout-value` | attributes |
| `attrs-model-tag` | attributes |
| `attrs-multiline-text` | attributes |
| `extdict-delete-nested` | extension dictionaries |
| `extdict-delete-xrecord` | extension dictionaries |
| `extdict-nested-value` | extension dictionaries |
| `extdict-xrecord-value` | extension dictionaries |
| `layers-freeze-dim` | layer state |
| `layers-linetype-hide` | layer state |
| `layers-lineweight-walls` | layer state |
| `layers-rgb-color` | layer state |
| `layers-unlock` | layer state |
| `layouts-delete-line` | layouts |
| `layouts-psltscale` | layouts (the layout's own PSLTSCALE, set inside one layout) |
| `layouts-move-text` | layouts |
| `layouts-rename` | layouts |
| `layouts-taborder` | layouts |
| `symbols-block-edit` | symbol tables |
| `symbols-dimstyle-scale` | symbol tables |
| `symbols-regapp-add` | symbol tables |
| `symbols-textstyle-font` | symbol tables |
| `symbols-ucs-delete` | symbol tables |
| `sysvars-clayer` | system variables |
| `sysvars-fillmode` | system variables |
| `sysvars-ltscale` | system variables |
| `sysvars-lunits` | system variables |
| `sysvars-pdmode` | system variables |
| `viewports-center` | viewports |
| `viewports-lock` | viewports |
| `viewports-off` | viewports |
| `viewports-scale` | viewports |
| `viewports-vplayer-thaw` | viewports |
| `views-delete-paper` | named views |
| `views-delete` | named views |
| `views-rename` | named views |
| `xdata-paper-int` | xdata |
| `xdata-real` | xdata |
| `xdata-remove-app` | xdata |
| `xdata-string` | xdata |

### Noise

These change only view state. A checkpoint must NOT report them (the dump changes only in `[NOISE]`).

| Noise | What it does |
|---|---|
| `noise-zoom` | ZoomCenter in model space (VIEWCTR, VIEWSIZE) |
| `noise-pan` | PAN in model space (VIEWCTR) |
| `noise-layout-switch` | makes layout tabs A-101 Plan and Z-Notes current and goes back to Model |
| `noise-regen` | REGEN |
| `noise-paper-zoom-pan` | ZOOM and -PAN in paper space of A-101 Plan (the overall viewport changes), back to Model |

### What COM could not do

* Per-viewport frozen layers: there is no COM property. The builder and `mut-viewports-vplayer-thaw` use `VPLAYER` through `SendCommand`, picking the viewport with `(handent "<handle>")`; the dump reads the frozen names from the viewport's `ACAD` xdata (1003 entries). `ActivePViewport` / `MSpace` failed ("no active modelspace viewport").
* `Views.Add` after `ZoomCenter` saved the wrong window, so named views are made with `ZOOM Center` and `-VIEW Save`.
* Dim styles have no properties in COM: the builder sets `DIMSCALE` etc. and `CopyFrom`s the drawing into the style; the dump makes each style current to read it.
* The template's layouts have no plotter (paper size in pixels, `AddPViewport` refused a width), so the builder gives each layout `DWG To PDF.pc3` and Letter landscape first.
* A viewport reports `ViewportOn = False` until its layout has been current once, and one made in a layout that is then left is saved off: the builder sets on / off / locked in a second pass, and `Save-Fixture` visits every layout before saving.
* Layouts touched in paper space carry a viewport record with target 0,0,0 (the layout's own view); the dump skips it.
* A multiline attribute: `MTextAttribute` / `MTextAttributeContent` on the attribute definition worked through COM (the dump shows `mtext=True`).
* `PAN` through `SendCommand` needs `-PAN`; plain `PAN` waits for the mouse and hung AutoCAD (Esc with `esc.ps1` freed it).
