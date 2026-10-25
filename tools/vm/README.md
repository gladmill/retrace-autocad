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
| `q-ents.ps1` | Query: model space objects by type and some system variables. |
| `run-test.sh <test folder or recording.jsonl> [--new-drawing] [--run]` | Runs on your Mac or Linux box: copies the recording to the VM (`C:\temp\retrace\tests\<folder>\`), replays it unattended (`RETRACEREPLAY <path> Auto`; with `--run` `RETRACERUN <path>`, which opens its own drawing), waits for `results\latest.json`, copies it back, prints the verdict and checkpoints. Exit 0 PASS, 1 FAIL/INCOMPLETE/ERROR, 2 infra. |
| `q-newdoc.ps1` | Query: opens a new drawing (`$acad.Documents.Add()`). Run it in a call of its own: later commands go to the document that was active before the query. |
| `q-netload.ps1` | Query: NETLOADs Retrace.dll from the installed bundle with `FILEDIA` off. AutoCAD then asks once whether to load the unsigned dll; a person answers that. |
| `esc.ps1` | Brings AutoCAD's frame to the front and presses Esc twice, without COM (leaves a prompt). Writes `C:\retrace-vm\out\esc.txt`; `foreground: False` there means Esc was only posted, so check with a query or `shot.ps1`. |
| `shot.ps1` | Screenshot without COM. |
| `wait-acad.ps1 [-TimeoutSec 240]` | From SSH: returns as soon as AutoCAD answers over COM and is quiescent (`ready after Ns`, exit 0; exit 1 on timeout with the last state). Uses `acad-ready.ps1` as a one-shot task. `start-acad` already ends with it; use it instead of sleeping or watching window titles: from session 0 `MainWindowTitle`/`MainWindowHandle` read empty/0, and the Start tab has no title anyway. |
| `run-hidden.vbs <command…>` | Runs a command with no window (`wscript //B //Nologo C:\retrace-vm\run-hidden.vbs powershell … -File x.ps1`). Every task here launches through it: `powershell -WindowStyle Hidden` still flashes a console window (a Windows Terminal window where Terminal is the default console host). Don't start AutoCAD itself through it (it would start hidden); `start-acad` runs a hidden `cmd` whose `start` shows AutoCAD normally. |
| `acad-ready.ps1` | Task-only: writes `ready`, `busy` or `none: <reason>` to `C:\retrace-vm\out\ready.txt`. |
| `start-acad.ps1` / `.cmd` | Starts AutoCAD in the interactive session. Returns when it is ready (runs `wait-acad.ps1`). AutoCAD's first start may need a person for its sign-in and start-up screens. |
| `wincancel.ps1` | Posts IDCANCEL to a top-level window by title. |

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
* Simulated mouse clicks (`SetCursorPos`/`mouse_event`) may not reach a VM's desktop; posted window messages do.
* `SetVariable` over COM needs the variable's type, e.g. `[int16]0` for `DYNMODE` and `FILEDIA`.
