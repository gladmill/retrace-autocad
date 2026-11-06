# AcadStatus

A small AutoCAD 2026 add-in that answers one question at a glance: **what is AutoCAD doing right now?**
Idle, running a command, waiting for you at a prompt, blocked by a dialog nobody answered, or not responding at all.

It does two things:

1. It writes what it sees to a small JSON file, `acad-<process id>.json`, when something changes and about every 2 seconds
   otherwise. Other tools (Retrace, test scripts, a person or Claude) read that file, and the files they write next to it, to know
   whether AutoCAD is free to use. The file format is in [CONTRACT.md](CONTRACT.md).
2. It shows one **pane on AutoCAD's status bar**: a coloured dot and, when something is going on, a few words.

It never touches your drawing and never presses anything in a dialog. It only looks.

## The pane

The pane sits at the right end of AutoCAD's status bar, next to the Isolate Objects button. It is compact. When AutoCAD is idle it is a
green dot and no text. (AutoCAD 2026 shows a status bar pane's icon or its text, never both, so the dot and the text are two adjacent
panes that the bar draws as one cell; the text pane is hidden while there is no text.)

| Dot | Meaning | Text, for example |
|---|---|---|
| green | idle | (none) |
| blue | a command or a test is running | `Running: PLOT`, `Suite 3/7` |
| amber | waiting for input: a prompt, a new dialog, or a tool that needs a person | `Input: OFFSET`, `Dialog`, `Needs person` |
| red | a dialog has been open 15 s or more (blocked), AutoCAD does not answer (busy), or a tool reports an error | `Blocked 2:31`, `No reply 0:25`, `Error` |
| grey | nothing to show, a state this version does not know, or a writer that stopped | |

The pane shows the most important entry of **all** status files in the folder, not only AutoCAD's own, so a Retrace test run that needs
a person shows up here too. Priority, highest first: blocked, needs-person, waiting-input, busy, error, running, idle, stopped.
At most 18 characters of text are shown. Hover for a tooltip with one line per entry.

**Click the pane** to open a small details window above it: every entry with its source, state, summary, age, owner, and for AutoCAD
the drawing, command, pending prompt, and any open dialog with its title, texts and buttons. It refreshes every second.
**Copy for Claude** copies a plain-text dump of everything, including the raw JSON of each file, so you can paste it into a chat.
The window closes on a second click on the pane, on Esc, or when it loses focus. When AutoCAD is not the active window, the first click on
the pane only activates AutoCAD (Windows eats it); click again.

## Commands

| Command | What it does |
|---|---|
| `ACADSTATUS` | Prints the same dump as "Copy for Claude" to the command line. |
| `ACADSTATUSPANE` | Shows or hides the pane. The choice is remembered. The status file keeps being written either way. |

## Install

AcadStatus comes in the Retrace release zip. Download `Retrace-<version>.zip` from the [Releases](https://github.com/gladmill/retrace-autocad/releases) page
and unzip it. It holds two folders side by side: `Retrace.bundle` and `AcadStatus.bundle`. They are independent: AcadStatus works without Retrace, and Retrace
without AcadStatus. With AutoCAD 2026 **closed**, copy `AcadStatus.bundle` (and `Retrace.bundle`, if you want Retrace) to
`%APPDATA%\Autodesk\ApplicationPlugins\`, replacing an older copy. Then trust the folder once, so AutoCAD does not stop at
"Security - Unsigned Executable File" at every start:

```powershell
$b = "$env:APPDATA\Autodesk\ApplicationPlugins\AcadStatus.bundle"
powershell -NoProfile -ExecutionPolicy Bypass -File "$b\Contents\trust-acadstatus.ps1" -Folder $b
```

This adds the bundle folder to AutoCAD's Trusted Locations (`TRUSTEDPATHS`) in every AutoCAD 2026 profile of your Windows user. It changes
nothing else. `-Check` reports whether it is trusted, `-Remove` takes it out again. Start AutoCAD: the pane appears on the status bar a
second after it starts.

To build the bundle yourself, see [AGENTS.md](AGENTS.md), "Build, test, pack": `scripts/pack.sh <out-dir>` writes `<out-dir>/AcadStatus.bundle`, the same folder as in the zip.

To uninstall, close AutoCAD, run the script with `-Remove`, and delete the bundle folder.

## Where things are

| What | Where |
|---|---|
| Status files | `%ProgramData%\Gladmill\acad-status\`, or the folder in the environment variable `ACAD_STATUS_DIR` when it is set |
| Pane setting | `%LocalAppData%\Gladmill\AcadStatus\settings.json` |
| Error log | `%LocalAppData%\Gladmill\AcadStatus\acadstatus.log` (each distinct problem once) |

The status files hold the name of the active drawing (not its folder), the command and prompt text, and the text of open dialogs. Anyone who
can read the status folder can read them. The folder is created readable and writable for all users of the machine, so that two Windows
users running AutoCAD at once can both write; point `ACAD_STATUS_DIR` somewhere private if you do not want that.

At start-up and then every 10 minutes AcadStatus deletes status files (of any tool) whose process is gone, or whose process id another program
has taken since, and that were last updated over an hour ago. When AutoCAD closes normally it writes `stopped` to its file.

## What it cannot see

- The **text and buttons of WPF dialogs and of Windows task dialogs** (the kind used by "Security - Unsigned Executable File"). For these it
  reports the title, the class, that they are modal, and since when, and nothing else: a WPF window has no child windows, and a task
  dialog draws its texts and buttons itself, so a window scan finds nothing in them. Reading them would need UI Automation, which is not
  called from AutoCAD's UI thread because it can deadlock against the window it asks about. Classic message boxes, `(alert ...)` boxes,
  MFC / DCL dialogs and WinForms dialogs are read in full.
- A **prompt of a command that never tells AutoCAD's editor** it is prompting. Built-in commands and LISP `getXXX` calls do.
- While AutoCAD's **UI thread is blocked** (a long LISP loop, a long regen) nothing can be updated, in the file or on the pane. That is
  what the status file's `ttlSec` is for: a reader sees the file stop being refreshed and reports "no reply".
