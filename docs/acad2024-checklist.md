# First run in AutoCAD 2024: checklist

The AutoCAD 2024 build (R24.3, .NET Framework 4.8) is built and tested without AutoCAD by us. A user has run the 0.6.17 build in an AutoCAD 2024 vertical product and reported that it loads from the new folder layout, that the record-start tab switch and the centred windows work there, and that the Excel links are gone (no detailed results); the status bar and auto-hide changes of 0.6.18 have not been run there.
Whoever has AutoCAD 2024 can settle that in about half an hour with the steps below. Write down what differs; "it worked" is a result too.
Close AutoCAD before each install step; use a test drawing and generic names only.

## 1. Install

1. Unzip `Retrace-<version>.zip`. Copy `Retrace.bundle` (and, if you want it, `AcadStatus.bundle`, which does not load in 2024) to `%APPDATA%\Autodesk\ApplicationPlugins\`.
2. Check the folder holds `Retrace.bundle\PackageContents.xml`, `Contents\2026\Retrace.dll` (the AutoCAD 2026 build, unused in 2024) and `Contents\2024\` with `Retrace.dll`,
   `System.Text.Json.dll` and nine more dlls. No dll sits loose in `Contents\`. If you update an older install, delete the old `Retrace.bundle` folder first (0.6.16 kept the 2026 dll in `Contents\`).
3. Either start AutoCAD 2024 once and answer its security prompt with **Load Once** or **Always Load**, or, with AutoCAD closed, run
   `powershell -NoProfile -ExecutionPolicy Bypass -File "%APPDATA%\Autodesk\ApplicationPlugins\Retrace.bundle\Contents\trust-retrace.ps1" -Folder "%APPDATA%\Autodesk\ApplicationPlugins\Retrace.bundle"`
   (it trusts the folder in every AutoCAD 2024 and 2026 profile of your Windows user).

## 2. Loads

4. Start AutoCAD 2024. Expected: no "Security - Unsigned Executable File" prompt after step 3 (or one prompt, if you skipped it), no error on the command line.
5. A **Retrace** tab appears in the ribbon, right of Home. Note any error text on the command line.
6. Run `RETRACESTATUS`. Expected: `Retrace <version>` and the folders under `C:\temp\retrace`.
7. Where to look when something is wrong: `C:\temp\retrace\retrace.log` (a line per error, with the exception; send it along). Also note whether
   `NETLOAD` of `...\Retrace.bundle\Contents\2024\Retrace.dll` works when the autoloader did not load it.

## 3. Record and write the steps

8. Click **Record** on the Retrace tab (or run `RETRACERECORD`), give it a test name.
9. Draw a `LINE` from the ribbon (two points).
10. Open any dialog that has a few fields (for example `UNITS`, or a command of your own) and change a value, then close it with OK.
11. Click a button on any docked palette (for example `DESIGNCENTER` or the tool palettes).
12. Stop with `-RETRACESTOP` (or the Stop button).
13. Open the test folder (`C:\temp\retrace\tests\<date>_<time>_<name>\`). Expected: `recording.jsonl`, `steps.md`, pictures; with Word and Excel export on (default),
    `steps.docx` and `steps.xlsx`. Open all three. Note whether the pictures are there and look right.

## 4. Replay

14. Start from a new drawing. Run `RETRACERUN` and pick the test. Expected: it replays the line, the dialog value and the palette click, and ends with verdict PASS.
15. Run `RETRACEREPLAY` in the current drawing as well and note the verdict.

## 4b. Status bar and auto-hide palettes

Only with an add-in of yours that puts an item into the status bar (or the samples, `STATUSBARADD`, which are built for 2026; any add-in with a `Pane` that opens a dialog or runs a command will do):
a. Record, click that item (it opens a dialog), Cancel the dialog, stop. Expected: steps.md has "Click **<text>** on the status bar" with a picture of the bar (a green box on the item) before the dialog step. If the click is missing, send `retrace.log`: it holds one `status bar probe:` line (written at the first click near the bottom of the window: how the bar was found, its class and
   rectangle, the window chain and the WPF elements under the mouse) and a `status bar click not recorded: ...` line saying why (the 2024 status bar may differ from 2026); `status bar click N: ...` appears when it is recorded.
b. `RETRACERUN` that test. Expected: the item is clicked for real, the dialog opens and closes, verdict PASS.
c. Open a palette set that has Auto-hide on from a Quick Access Toolbar or ribbon button while a recording runs. Expected: its end picture has a green box (the strip, or the palette) and the caption "The **X** palette opens ..." in steps.md; otherwise `retrace.log` says
   `end picture N: ...` with the reason and the state it read.

## 5. Windows

16. `RETRACESETTINGS`: the Settings window opens. Change one value, Save, close, open it again: the value is kept, and `C:\temp\retrace\settings.json` has it.
    Use a folder button: the folder dialog opens (the old Windows folder browser, one folder at a time on 2024) and puts the chosen folder in the box.
17. Open the guide palette from the Retrace tab. Expected: the page shows (it needs the Microsoft Edge WebView2 Runtime). If it stays blank, send `retrace.log`.
18. `RETRACEMAP`: the Map window opens; add a folder with "Add folders" (one per click on 2024) and run it. Expected: a result list, or a clear message.
19. Ribbon repair: `RIBBONLOSS` is only in the samples; without it, hide the tab with the ribbon right-click menu "Show Tabs" and type `RETRACERIBBON`: the tab is back and active, with one or two lines on the command line. Switching workspace (`WSCURRENT`) and back keeps the tab without any command.

## 6. Report

Send: what worked, what differed, `C:\temp\retrace\retrace.log`, and the AutoCAD build (`VER` on the command line, and Help > About).
Known and expected on 2024: layout `psltscale` and viewport `off` are left out of the behavior snapshot; the folder dialog takes one folder at a time.

What this checklist settles, for the record (open questions in the pull request that added the AutoCAD 2024 build): how AutoCAD loads `Contents\2024\Retrace.dll` and
whether anything redirects the bundled System.Text.Json, System.Memory and OpenXml names; whether AutoCAD 2024 ships its own WebView2, System.Memory or OpenXml; the
WinForms folder dialog opened from a WPF window; whether the Trusted Location covers `Contents\2024` under `SECURELOAD`; whether the autoloader takes two `Components`
blocks with one application name.
