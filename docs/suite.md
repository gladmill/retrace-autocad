# Test suites (RETRACESUITE)

`RETRACESUITE` replays many recorded tests one after another, unattended, and writes one summary. It keeps going past FAIL, INCOMPLETE, ERROR and tests that need a person: such a test ends that test only. A dialog Retrace cannot fill is not closed for you; if it is still open 30 s later the suite stops (STOPPED, the rest NOT-RUN). It is for a nightly run or a check before a release: start it, leave AutoCAD alone, read the summary.

## Starting a suite

- `RETRACESUITE` (window): tick the tests in the Open test window (`RETRACEOPEN`), press **Run suite**. `RETRACESUITE` typed while that window is open with ticked tests starts them too; with nothing ticked it opens the window. **Tick all** and **Untick all** work on the list as filtered. The list shows each test's last verdict, which a suite run updates.
- `-RETRACESUITE` (command line, no window): asks `Tests (...)`. Answer with names or paths separated by `;` (a test folder name, a name without its date, `last`, a path), `@file` (one test per line; blank lines and lines starting with `#` are skipped), or `all` / Enter for every test in the tests folder. Scriptable over COM: send `-RETRACESUITE`, Enter, the answer, Enter. If a name finds no test, nothing starts and the names that failed are listed.

Tests run oldest first (ticked tests and `all`) or in the order given (names and `@file`). A name given twice runs once.

A suite and a Tdoc run cannot run together: whichever starts second is refused with a message.

While a suite runs, `RETRACESUITE` says so. Between two tests it ends the suite; during a test, press Stop in the replay window (the suite ends after that test).

## How each test runs

Each test goes through the RETRACERUN code path, unattended: `^C^C` is typed first (so a prompt a test before left open cannot take the next command), a new drawing opens, the replay runs in Auto mode, dialogs are filled in from the recording when possible. A step that needs a person (a dialog Retrace cannot fill, a palette it cannot press) ends the test as `NEEDS-PERSON` at once; nobody is waited for.

After each test the suite closes, without saving, every drawing the test left open (its new drawing and any drawing its "open" step opened). A drawing that was open when the suite started is never closed, saved or continued in. One rule: a test's explicit "open" step for such a file opens a copy of it from a temp folder instead (same file name; ERROR if the file is gone). Every other way into one (a palette double-click that opens it, the drawing the test ran in being closed so AutoCAD falls back to the active one) ends the test as ERROR, "the test switched to <file>, which was open before the suite: not touched; close it and re-run". A recording whose last step closes its drawing still passes. If AutoCAD closes every drawing that was open at the start and a test remains, the suite opens a blank drawing of its own to run from; it counts as protected too, and is closed at the end if untouched. If no drawing can be opened, the suite stops. At the end the suite closes exactly the drawings it knows about (those the tests left open), on a timer until they are gone (a command was running in one); drawings you open afterwards are never touched. The temp copies are deleted then, and copies left by a closed AutoCAD are deleted at the next suite start when a day old.

A temp copy loses what the file needs next to it: relative xrefs, images and AutoCAD Electrical project membership. Close project drawings before running a suite whose tests open them.

If the last step of a test closes its drawing (a `CLOSE`, with or without the "Save changes?" dialog that belongs to it) the test finishes there and passes. If a step closes every drawing and none is left, the test ends as ERROR "no drawing to continue in" after the step timeout and the suite goes on from a blank drawing of its own.

RETRACERUN and the Open test window's Run this test are refused while a suite or a Tdoc run is active; the suite's own RETRACERUN is not.

## Timeouts and blocked AutoCAD

- **Step timeout** (setting `suiteStepTimeoutSeconds`, default 120, 5 to 3600; Settings window group "Test suites", and `-RETRACESETTINGS`). A step that has run longer than this (a command waiting at a prompt, a hung dialog) is cancelled with `^C^C` and the test is recorded as `ERROR`, `step N timed out after S s` (step status `timed-out` in its result file). The suite goes on. It only applies in a suite; RETRACERUN and RETRACEREPLAY wait as before.
- **Start watchdog**: a test whose RETRACERUN did not start within 90 s is `NOT-RUN` and the suite goes on (as in Tdoc).
- **Security prompt or dialog**: the test ends at once (`NEEDS-PERSON`, or `ERROR` after the step timeout) with the reason. An unexpected dialog is blamed on the step that was sent (that step's own result line becomes `needs-person`) and the message names the dialog's title. The prompt or dialog is not answered by Retrace. If it is still open 30 s after the test, or a drawing cannot be closed for 30 s, the suite stops: the summary says why and lists the tests not reached as `NOT-RUN`. A test that was running or starting when the suite ended is listed as `INCOMPLETE` or `NOT-RUN`; a suite never ends as PASS with a test missing. If AutoCAD (or a person) closes it within 30 s, the suite goes on. A dialog that stays open also keeps the test's drawing open (it cannot be closed while the dialog is modal): close it yourself after dismissing the dialog.

## The summary

`C:\temp\retrace\results\<stamp>_suite.json` and `<stamp>_suite.md` (written through temp files; a second suite in the same second is `<stamp>_suite_2.json`, `_3`...; the latest is the newest by write time). The Open test window's **Last suite** button opens the newest `.md`.

| Field (json) | |
|---|---|
| `stamp`, `startedUtc`, `finishedUtc`, `coreVersion`, `stepTimeoutSeconds` | |
| `verdict` | `PASS` (every test passed), `FAIL` (any other result), `STOPPED` (the suite ended early) |
| `stopReason` | Why it ended early, else `""`. |
| `counts` | Number of tests per verdict. |
| `tests[]` | One per test, in run order: `name`, `recording`, `verdict`, `durationSeconds`, `message`, `failingCheckpoints[]` (`index`, `note`, `differences`: the checkpoint differences and failed behavior checks), `result` (path of the test's own result file, `<test folder>\results\<stamp>_<VERDICT>.json`). |

Test verdicts are those of a replay (`PASS`, `FAIL`, `INCOMPLETE`, `ERROR`; see [recording-format.md](recording-format.md)) plus `NEEDS-PERSON` (an `INCOMPLETE` run that stopped at a step that needs a person) and `NOT-RUN` (it could not start, or the suite stopped before it). The `.md` has one table row per test with the same information.

The code: `Core/SuiteResult.cs` (summary, no AutoCAD), `Core/SuiteTestList.cs`, `AutoCad/TestQueue.cs` (the queue shared with Tdoc), `AutoCad/SuiteRun.cs`, `AutoCad/Commands.Suite.cs`.
