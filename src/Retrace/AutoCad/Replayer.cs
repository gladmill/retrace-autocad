using System.Windows.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.Windows;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

using Retrace.Ui;

using Retrace.Engine.Recordings;

namespace Retrace.AutoCad;

public enum ReplayMode { Auto, Step }

/// <summary>
/// Replays a recorded session. Design: a DispatcherTimer state machine on the UI thread (never blocks AutoCAD).
/// Each tick, once AutoCAD has settled (no command running, no modal dialog, no queued input), the next step runs:
///  - ribbon step with no recorded answers: find the RibbonItem by Id and run its CommandHandler (what a click does);
///    if the item is gone or has no handler, or the click starts no command within 2 s, send the recorded macro instead
///    (the log says which, and names the item's type and handler);
///  - ribbon/command/lisp step WITH recorded answers: one SendStringToExecute with "command + answers" so the answers
///    reach the prompts in order (typed input is the only input path that is deterministic and needs no prompt hooks);
///  - dialog step: tell the user to fill the dialog in; continue after LeaveModal;
///  - mark: computes the drawing digest and compares it with the recorded one (CHECK n PASS / FAIL: ...).
/// The run ends with a result file (ReplayResult). In Auto mode a step that needs a person (a dialog) ends the run with
/// INCOMPLETE instead of waiting forever; a failed or unknown command or an exception ends it with ERROR.
/// </summary>
sealed class Replayer
{
    public static Replayer? Current { get; private set; }

    readonly List<Step> _steps;
    readonly ReplayMode _mode;
    Document _doc;   // the drawing being replayed in; an "open" step moves the replay to the drawing it opens
    int _lispRunning;   // LISP running in _doc (a drawing's start-up LISP runs after it opens)
    bool _afterOpen; DateTime _quietSince = DateTime.MinValue;   // after an open: wait until the drawing has been quiet for a moment
    Step? _awaitOpen; DateTime _awaitSince;   // a native palette double-click that opened a drawing when recorded: waiting for it to open
    bool _inTick;    // a tick is running: a modal loop inside it must not start another
    bool _docGone;   // _doc was closed: go on in the active drawing
    string? _lastStarted; DateTime _lastStartedAt;   // the last command that started, and when: a native palette action may start it before the step waits for it
    readonly DispatcherTimer _timer;
    readonly ReplayWindow _win = new();
    readonly string _sessionPath;
    readonly ReplayResult _result = new();
    readonly List<string> _warnings = new();
    string? _eventError;            // set by Document events (a plain field write; no AutoCAD calls in handlers)
    DateTime _modalSince = DateTime.MinValue;
    int _checks;
    int _i;
    bool _running, _userNext;
    // the current palette step: retried since _paletteSince; waiting for a person (Next); its button's command still to start
    bool _paletteForPerson;
    DateTime _paletteSince = DateTime.MinValue, _paletteRetryAt = DateTime.MinValue, _paletteClickedAt;
    Step? _paletteCommand;
    // showing the palette being driven (setting showDrivenUi): the mouse is rested on it as a person would, and put back after
    readonly bool _showUi = RetraceSettings.Load().ShowDrivenUi;
    bool _revealDone;
    int _revealTry = -1;
    DateTime _revealAt, _cursorBackAt = DateTime.MaxValue;
    PaletteReveal.POINT? _cursorHome, _cursorSet;   // where the mouse was before; where Retrace last put it
    bool _paletteCommandStarted;
    int _modalDepth;
    int _modalsSeen;
    int _modalsAtStepStart;
    bool _pendingStart;
    Step? _clicked; int _clickedIndex;   // the ribbon step whose live item was just invoked (and its 1-based index): if no command starts within 2 s, its macro is typed instead
    DateTime _notBefore = DateTime.MinValue, _sentAt, _stepShownAt;
    bool _shown;
    bool _alert;
    // interactive (RETRACERUN): a dialog step waits for a person instead of ending the run
    readonly bool _interactive;
    readonly DialogWait _dialog = new();
    bool _announced, _cancelSent;
    DateTime _doneAt = DateTime.MinValue;
    readonly bool _autoFillOn;      // setting autoFillDialogs, read once at the start (interactive only)
    DialogAutoFiller? _fill;        // fills the current dialog; null = not tried / not applicable
    bool _fillTried, _fillAnnounced;
    string? _deferred;   // answers recorded after the dialog opened: typed once the person confirmed it (see Plan.InputAroundDialog)
    // Tdoc run (RETRACETDOC): pictures for the training document; null for every other replay, and then none of the hooks does anything
    TdocCapture? _tdoc;
    SuiteHook? _suite;   // suite run (RETRACESUITE): step timeout, no waiting for a person; null for every other replay
    StepClock? _clock;   // the suite's step timeout: which step runs and since when
    /// <summary>Nobody is there to help: RETRACEREPLAY, and a suite run (which fills dialogs itself but never waits for a person).</summary>
    bool Unattended => !_interactive || _suite != null;
    Action<ReplayResult>? _finished;   // called at the end of Finish, after the result file is written

    Replayer(List<Step> steps, ReplayMode mode, Document doc, string sessionPath, bool interactive)
    {
        _steps = steps; _mode = mode; _doc = doc; _sessionPath = sessionPath; _interactive = interactive;
        _autoFillOn = interactive && RetraceSettings.Load().AutoFillDialogs;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) =>
        {
            // a native palette action is handled inside the tick that does it, and may show a window that pumps messages meanwhile
            // (AutoCAD Electrical's "Updating..."): that must not start the next tick
            if (_inTick) return;
            _inTick = true;
            try { Tick(); }
            catch (Exception ex) { Log.Error("Replay tick", ex); Finish(false, true, "exception: " + ex.Message); }
            finally { _inTick = false; }
            StatusBoardHost.Changed();   // the status board: written only when what it shows changed; never throws
        };
        _win.NextClicked += () => _userNext = true;
        _win.StopClicked += () =>
        {
            if (_running && _tdoc != null) _tdoc.StopRequested = true;   // Stop ends a Tdoc run's whole queue (a normal end closes the window too: only while running)
            if (_running && _suite != null) _suite.StopRequested = true;
            _userStopped = true;
            Finish(false, false, "stopped by the user");
        };
    }

    string _testName = "";   // the test's name for the status board (the test folder's name without its date)
    bool _stoppedForPerson;  // Finish was called because a step needs a person

    /// <summary>Where the replay is, for the status board (read-only; UI thread). The step is the one being run: the one shown, or the one just sent;
    /// during a dialog wait the dialog's step, as the replay window shows it.</summary>
    internal ReplayProgress Status()
    {
        var n = _steps.Count;
        var cur = Math.Clamp(_dialog.Active || _shown ? _i + 1 : Math.Max(_i, 1), 1, Math.Max(n, 1));
        var step = n > 0 ? _steps[cur - 1] : null;
        var text = step == null ? "" : !string.IsNullOrWhiteSpace(step.Command) ? step.Command! : step.Title;
        string? note = null, person = null;
        if (_paletteForPerson) person = $"a palette step Retrace cannot do: {(string.IsNullOrWhiteSpace(step?.DialogTitle) ? "(untitled)" : step!.DialogTitle)}";
        else if (_dialog.Active && step != null)
        {
            var title = string.IsNullOrWhiteSpace(step.DialogTitle) ? "(untitled)" : step.DialogTitle!.Trim();
            switch (_dialog.Phase)
            {
                case DialogWaitPhase.WaitingForOpen: note = $"waiting for dialog {title}"; break;
                case DialogWaitPhase.WaitingForPerson:
                    if (!_fillTried) note = $"dialog {title} is open";
                    else if (_fill != null && _fill.State != DialogAutoFiller.Phase.GaveUp) note = $"filling dialog {title}";
                    else if (_suite == null) person = $"a dialog Retrace cannot fill: {title}";   // a suite run ends the test at once instead
                    break;
            }
        }
        else if (_mode == ReplayMode.Step && _shown && !_userNext) note = "waiting for Next";
        else if (_modalDepth > 0) note = "a dialog is open";
        return new ReplayProgress(_testName, cur, n, text, note, person);
    }

    /// <summary>Stops a running replay (timer, event handlers, panel). Used when the plugin is shut down.</summary>
    public static void StopCurrent()
    {
        var c = Current; if (c == null) return;
        c._alert = false;
        if (c._tdoc != null) c._tdoc.StopRequested = true;
        if (c._suite != null) c._suite.StopRequested = true;
        c.Finish(false, false, "stopped (core shutdown)");
    }

    /// <summary>Starts a replay in the active drawing (call from the RETRACEREPLAY command). <paramref name="initial"/> is the session's
    /// initial drawing state (null for older sessions).</summary>
    /// <param name="tdoc">a Tdoc run's pictures for this test (RETRACETDOC); null for a plain replay</param>
    /// <param name="finished">called once at the end of the run with its result (a Tdoc run starts the next test from it)</param>
    public static bool Start(List<Step> steps, ReplayMode mode, string sessionPath, DrawingDigest? initial, out string message, bool alert = false, bool interactive = false,
        TdocCapture? tdoc = null, Action<ReplayResult>? finished = null, SuiteHook? suite = null)
    {
        if (Current != null) { message = "A replay is already running (close its window or press Stop)."; return false; }
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc == null) { message = "No active drawing."; return false; }
        var r = new Replayer(steps, mode, doc, sessionPath, interactive) { _alert = alert, _tdoc = tdoc, _finished = finished, _suite = suite };
        Current = r;
        r.Begin(initial);
        message = $"Replaying {steps.Count} steps ({mode}).";
        return true;
    }

    void Begin(DrawingDigest? initial)
    {
        _result.Session = TestFolders.NameOf(_sessionPath);
        _testName = TestList.NameOf(_result.Session);
        _result.StartedUtc = DateTime.UtcNow.ToString("o");
        _result.CoreVersion = typeof(Replayer).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "unknown";
        _result.StepsTotal = _steps.Count;
        _result.Interactive = _interactive;
        // a test folder keeps its history; only results/latest.json is replaced, when this run ends
        CompareStart(initial);
        _running = true;
        AcApp.EnterModal += OnEnterModal;
        AcApp.LeaveModal += OnLeaveModal;
        Hook(_doc);
        AcApp.DocumentManager.DocumentToBeDestroyed += OnDocGone;
        if (_tdoc != null)
        {
            _win.Prefix = _tdoc.Label;
            _tdoc.Begin(_win.SetVisible, AcApp.MainWindow.Handle);
            TdocRun.Started(_tdoc);
        }
        _suite?.Started();
        if (_suite != null) _clock = new StepClock(_suite.StepTimeout);
        _win.Show();
        _win.Show("Retrace replay", "Starting...", false);
        _timer.Start();
        StatusBoardHost.ReplayStarted(queued: _tdoc != null || _suite != null);
    }

    /// <summary>A line on the command line of the drawing that is active now: a drawing opened by a palette click becomes active at once, and
    /// AutoCAD refuses to write to one that is not (eNotApplicable).</summary>
    void Say(string message)
    {
        try { (AcApp.DocumentManager.MdiActiveDocument ?? _doc).Editor.WriteMessage(message); }
        catch (Exception ex) { Log.Info($"replay message not shown ({ex.Message}): {message.Trim()}"); }
    }

    void Hook(Document d)
    {
        d.CommandWillStart += OnCmdStart;
        d.CommandFailed += OnCmdFailed;
        d.UnknownCommand += OnUnknown;
        d.CommandEnded += OnCmdEnded;
        d.CommandCancelled += OnCmdCancelled;
        d.LispWillStart += OnLispStart;
        d.LispEnded += OnLispEnd;
        d.LispCancelled += OnLispEnd;
    }

    void Unhook(Document d)
    {
        d.CommandWillStart -= OnCmdStart;
        d.CommandFailed -= OnCmdFailed;
        d.UnknownCommand -= OnUnknown;
        d.CommandEnded -= OnCmdEnded;
        d.CommandCancelled -= OnCmdCancelled;
        d.LispWillStart -= OnLispStart;
        d.LispEnded -= OnLispEnd;
        d.LispCancelled -= OnLispEnd;
    }

    void OnLispStart(object? s, LispWillStartEventArgs e) => _lispRunning++;

    /// <summary>The drawing being replayed in is closing: AutoCAD Electrical closes an untouched new drawing when it opens a project drawing or
    /// switches projects. Unhooked now; the next tick goes on in the active drawing (<see cref="_docGone"/>).</summary>
    void OnDocGone(object? s, DocumentCollectionEventArgs e)
    {
        if (!ReferenceEquals(e.Document, _doc)) return;
        Log.Safe("Replay doc gone", () => Unhook(_doc));
        _docGone = true;
    }
    void OnLispEnd(object? s, EventArgs e) { if (_lispRunning > 0) _lispRunning--; }

    /// <summary>An "open" step: opens the recorded drawing and replays the rest in it, the way the person went on in the drawing they
    /// opened. Already open and unchanged, it switches to it; open with unsaved changes it stops (they are not what the file holds, and
    /// a test never discards someone's changes). False when it could not: the run has finished with the reason.</summary>
    bool OpenDrawing(Step s)
    {
        var path = s.Drawing ?? "";
        var dm = AcApp.DocumentManager;
        try
        {
            var doc = OpenDocument(path);
            var ownCopy = false;
            // suite run: a drawing the person had open is never replayed into (or saved, or closed): the test gets a copy of the file instead,
            // one copy per file for the whole test (a second open of the same file goes back to it)
            if (doc != null && _suite != null && _suite.IsProtected(doc))
            {
                if (!_copies.TryGetValue(path, out var copy))
                {
                    if (!File.Exists(path)) { Finish(false, true, $"{Path.GetFileName(path)} was open before the suite and its file is gone: the test will not touch that drawing"); return false; }
                    copy = _suite.CopyOf(path); _copies[path] = copy;
                    Say($"\n  {Path.GetFileName(path)} is open already; the test uses a copy.\n");
                }
                path = copy; doc = OpenDocument(path); ownCopy = true;
            }
            else if (_suite != null && _copies.TryGetValue(path, out var again)) { path = again; doc = OpenDocument(path); ownCopy = true; }
            if (doc != null && !ownCopy && !(bool)((dynamic)doc.GetAcadDocument()).Saved)
            {
                Finish(false, true, $"{Path.GetFileName(path)} is open with unsaved changes: save or close it, then run the test again");
                return false;
            }
            if (doc == null)
            {
                if (!File.Exists(path)) { Finish(false, true, $"drawing not found: {path}"); return false; }
                doc = dm.Open(path, false);
            }
            if (!Follow(doc)) return false;
            Say($"\n  Opened {path}\n");
            return true;
        }
        catch (Exception ex) { Log.Error("OpenDrawing", ex); Finish(false, true, $"could not open {path}: {ex.Message}"); return false; }
    }

    /// <summary>Replays the rest in <paramref name="doc"/>, a drawing just opened. Its start-up (acaddoc.lsp, S::STARTUP, AutoCAD Electrical's
    /// project setup) runs after this, partly on the drawing's first idle: the next step waits until it has been quiet (no command, no
    /// LISP) for a second.</summary>
    readonly Dictionary<string, string> _copies = new(StringComparer.OrdinalIgnoreCase);   // suite: a protected file -> the temp copy this test opened

    bool Follow(Document doc)
    {
        if (RefusedProtected(doc)) return false;
        var dm = AcApp.DocumentManager;
        if (dm.MdiActiveDocument != doc) dm.MdiActiveDocument = doc;
        if (!ReferenceEquals(doc, _doc)) { Log.Safe("Replay unhook", () => Unhook(_doc)); _doc = doc; Hook(_doc); }
        if (_suite != null) _docGone = false;
        _lispRunning = 0; _afterOpen = true; _quietSince = DateTime.MinValue;
        _notBefore = DateTime.Now.AddMilliseconds(500);
        return true;
    }

    /// <summary>The one rule for protected drawings in a suite (the ones open before it, and the blank one it opened to run from): an explicit "open"
    /// step gets a temp copy (<see cref="OpenDrawing"/>); every other way into one (a palette double-click, the drawing the test ran in being
    /// closed) ends the test as ERROR. True when the run ended.</summary>
    bool RefusedProtected(Document doc)
    {
        if (_suite == null || !_suite.IsProtected(doc)) return false;
        string name; try { name = Path.GetFileName(doc.Name); } catch (Exception) { name = "a drawing"; }
        Finish(false, true, $"the test switched to {name}, which was open before the suite (or is the suite's own start drawing): not touched; close it and re-run");
        return true;
    }

    static Document? OpenDocument(string path) =>
        AcApp.DocumentManager.Cast<Document>().FirstOrDefault(d => d.IsNamedDrawing && string.Equals(d.Name, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>A native palette double-click that opened <see cref="Step.Drawing"/> when recorded: follow the drawing once it is open. If it
    /// does not open within 20 s, Retrace opens it itself (the run goes on, with a warning in the result).</summary>
    void TickAwaitOpen()
    {
        var s = _awaitOpen!;
        if (OpenDocument(s.Drawing!) is { } doc)
        {
            _awaitOpen = null;
            if (!Follow(doc)) return;   // a protected drawing: the test ends as ERROR (the palette is not raced with a copy)
            Say($"\n  Opened {s.Drawing}\n");
            NextPaletteStep();
            return;
        }
        if (DateTime.Now - _awaitSince < TimeSpan.FromSeconds(20)) return;
        _awaitOpen = null;
        _warnings.Add($"step {_i + 1}: the double-click in the {s.DialogTitle} palette did not open {Path.GetFileName(s.Drawing)}; Retrace opened it itself");
        if (!OpenDrawing(s)) return;
        NextPaletteStep();
    }

    /// <summary>Compares the drawing now with the session's initial state (command context: RETRACEREPLAY is running).</summary>
    void CompareStart(DrawingDigest? initial)
    {
        var ed = _doc.Editor;
        if (initial == null) { ed.WriteMessage("\nNote: the session has no recorded start state (older recording); the start drawing is not checked.\n"); return; }
        var now = DrawingState.Read(_doc, withLayerState: initial.LayerState != null);   // a schema 2 recording keeps its start layer compare
        if (now == null) { _result.StartStateMatches = false; _warnings.Add("start state could not be read"); ed.WriteMessage("\nWARNING: could not read the drawing to compare with the start state.\n"); return; }
        var diffs = DrawingDigest.Compare(initial, now);
        _result.StartStateMatches = diffs.Count == 0;
        if (diffs.Count == 0) { ed.WriteMessage("\nStart state matches the recording.\n"); return; }
        var text = "start state differs from the recording (" + string.Join("; ", diffs) + ")";
        _warnings.Add(text);
        ed.WriteMessage($"\nWARNING: {text}. Replaying anyway; later checkpoints may fail because of this.\n");
    }

    /// <summary>Ends the run: detaches everything, writes the result file and prints the one-line summary.</summary>
    /// <param name="needsPerson">the run stops because a step needs a person (a dialog it cannot fill, a security prompt, a palette it cannot drive): the status board
    /// says needs-person while that dialog is open</param>
    void Finish(bool completed, bool errored, string message, bool needsPerson = false)
    {
        if (!_running) return;
        _running = false;
        _stoppedForPerson = needsPerson;
        CursorBack();
        _timer.Stop();
        Log.Safe("Replay restore view", () => HandOffView(cancel: _userStopped || errored));
        Log.Safe("Replay stop", () =>
        {
            AcApp.EnterModal -= OnEnterModal;
            AcApp.LeaveModal -= OnLeaveModal;
            AcApp.DocumentManager.DocumentToBeDestroyed -= OnDocGone;
            if (!_docGone) Unhook(_doc);
        });
        Current = null;
        try { _win.Close(); } catch { }
        Log.Safe("Replay result", () =>
        {
            _result.FinishedUtc = DateTime.UtcNow.ToString("o");
            _result.StepsRun = Math.Min(_i, _steps.Count);
            var failedSteps = _result.Steps.Count(x => x.Status == "failed");
            _result.Verdict = ReplayResult.ComputeVerdict(completed, errored, _result.Checkpoints, failedSteps);
            var notes = new List<string>();
            foreach (var f in _result.Steps.Where(x => x.Status == "failed")) notes.Add($"step {f.Index} failed: {f.Message}");
            if (!completed || errored) notes.Add(message);
            if (completed && !errored && _result.Checkpoints.Count == 0) notes.Add("the session has no checkpoints (RETRACEMARK), nothing was verified");
            foreach (var c in _result.Checkpoints.Where(c => c.Pass == null)) notes.Add($"checkpoint {c.Index} has no recorded state (older recording)");
            notes.AddRange(_warnings);
            _result.Message = string.Join("; ", notes);
            var written = _result.Write(_sessionPath, ex => Log.Error("write result", ex));
            Say($"\nRetrace replay ended at step {_result.StepsRun}/{_steps.Count}: {message}\n");
            Say($"\n{_result.Summary()}\n" + (written != null ? $"Result: {written}\n" : "Result file could not be written (see retrace.log).\n"));
            StatusBoardHost.ReplayEnded(_result, queued: _tdoc != null || _suite != null, _stoppedForPerson);   // before the verdict box: it blocks until someone closes it
            if (_alert) ShowAlert(written);
        });
        if (_finished != null) Log.Safe("Replay finished callback", () => _finished(_result));
    }

    /// <summary>RETRACERUN only (never for RETRACEREPLAY, which the unattended runner uses): a short verdict box on the UI thread.</summary>
    void ShowAlert(string? written)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Retrace test: {_result.Verdict}  (session {_result.Session})");
        foreach (var f in _result.Steps.Where(x => x.Status == "failed")) sb.AppendLine($"  Step {f.Index} failed: {f.Message}");
        foreach (var c in _result.Checkpoints)
        {
            var label = string.IsNullOrWhiteSpace(c.Note) ? "" : " \"" + c.Note + "\"";
            var verdict = c.Pass == true ? "PASS" : c.Pass == false ? "FAIL: " + string.Join("; ", c.Differences.Take(3)) : "not verified";
            sb.AppendLine($"  Checkpoint {c.Index}{label}: {verdict}");
        }
        if (_result.Checkpoints.Count == 0) sb.AppendLine("  (no checkpoints)");
        if (!string.IsNullOrEmpty(_result.Message)) sb.AppendLine(_result.Message);
        sb.AppendLine(written != null ? "Result file: " + written : "Result file could not be written.");
        Autodesk.AutoCAD.ApplicationServices.Application.ShowAlertDialog(sb.ToString());
    }

    // handlers: plain field writes, no AutoCAD calls
    void OnEnterModal(object? s, EventArgs e) { _modalDepth++; _modalsSeen++; _dialog.ModalEntered(); }
    void OnLeaveModal(object? s, EventArgs e) { if (_modalDepth > 0) _modalDepth--; _dialog.ModalLeft(_modalDepth); }
    void OnCmdEnded(object? s, CommandEventArgs e) { if (!OwnCommands.IsOwnCommand(e.GlobalCommandName)) _dialog.CommandEnded(false, DateTime.Now); }
    void OnCmdCancelled(object? s, CommandEventArgs e) { if (!OwnCommands.IsOwnCommand(e.GlobalCommandName)) _dialog.CommandEnded(true, DateTime.Now); }
    void OnCmdFailed(object? s, CommandEventArgs e) { if (!OwnCommands.IsOwnCommand(e.GlobalCommandName)) _eventError ??= $"command {e.GlobalCommandName} failed"; }
    void OnUnknown(object? s, UnknownCommandEventArgs e) { _eventError ??= $"unknown command {e.GlobalCommandName}"; }
    void OnCmdStart(object? s, CommandEventArgs e)
    {
        if (OwnCommands.IsOwnCommand(e.GlobalCommandName)) return;
        _pendingStart = false; _clicked = null;
        _lastStarted = e.GlobalCommandName; _lastStartedAt = DateTime.Now;
        // a command without prompts can start and end between two ticks: remember that the palette button's command started
        if (_paletteCommand != null && string.Equals(e.GlobalCommandName, _paletteCommand.Command, StringComparison.OrdinalIgnoreCase)) _paletteCommandStarted = true;
    }

    bool Settled()
    {
        if (_modalDepth > 0) return false;
        if (DateTime.Now < _notBefore) return false;
        if (_pendingStart && (DateTime.Now - _sentAt).TotalSeconds < 2) return false; // wait for the command to actually start
        if (_pendingStart && _clicked is { } clicked) { _clicked = null; if (TypeAfterSilentClick(clicked)) return false; }
        _pendingStart = false;
        var inProgress = _doc.CommandInProgress;
        // our own RETRACEREPLAY command has already returned (the timer runs after it), so anything in progress is replayed work
        var quiet = string.IsNullOrEmpty(inProgress) && _doc.Editor.IsQuiescent && _lispRunning == 0;
        if (!_afterOpen) return quiet;
        if (!quiet) { _quietSince = DateTime.MinValue; return false; }
        if (_quietSince == DateTime.MinValue) { _quietSince = DateTime.Now; return false; }
        if ((DateTime.Now - _quietSince).TotalSeconds < 1) return false;
        _afterOpen = false;
        return true;
    }

    void Tick()
    {
        if (!_running) return;
        // (a palette open that is still pending decides the drawing itself: TickAwaitOpen, bounded by its own timeout)
        if (_docGone && (_suite == null || _awaitOpen == null))
        {
            if (_suite != null && ReplayRules.NothingLeftToRun(_steps, _i)) { Finish(true, false, "finished"); return; }   // the last step closed its drawing (and the dialogs it showed)
            if (AcApp.DocumentManager.MdiActiveDocument is not { } active || ReferenceEquals(active, _doc))   // between drawings, or none left (CLOSEALL)
            {
                if (_clock is { } gone && gone.Expired(DateTime.Now)) Finish(false, true, "no drawing to continue in");
                return;
            }
            if (RefusedProtected(active)) return;
            _docGone = false;
            _doc = active; Hook(_doc);
            Log.Info($"replay continues in {active.Name}: the drawing it ran in was closed");
        }
        if (_eventError != null) { Finish(false, true, _eventError); return; }
        // AutoCAD's security prompt (an untrusted dll or LISP file is being loaded) is a decision for a person, never a test step:
        // stop at once with what to do, in both modes, and leave the prompt open for the person to answer.
        if (Native.OpenSecurityPrompt() is { } prompt) { StopAtSecurityPrompt(prompt); return; }
        if (_suite != null && CheckStepTimeout()) return;
        // interactive: the step that opens a dialog was sent; wait for the dialog and the person
        if (_interactive && !_dialog.Active && _i < _steps.Count && _steps[_i].Kind == "dialog") 
        {
            _clock?.Begin(_i + 1, DateTime.Now);
            _dialog.Begin(DateTime.Now, _modalDepth > 0, _steps[_i].ClosedWith == "cancel");
            _announced = false; _cancelSent = false;
            _fill = null; _fillTried = false; _fillAnnounced = false;
            // the dialog came and went between two ticks: count it as handled
            if (_modalDepth == 0 && _modalsSeen > _modalsAtStepStart) { _dialog.ModalEntered(); _dialog.ModalLeft(0); }
        }
        if (DateTime.Now >= _cursorBackAt) CursorBack();
        if (_modalDepth > 0 && Native.ForegroundOfThisProcess() is var dlg && dlg != IntPtr.Zero && dlg != AcApp.MainWindow.Handle && dlg != _win.Handle)
        {
            var d = PaletteReveal.RectOf(dlg);
            _win.Avoid(d.Left, d.Top, d.Right, d.Bottom);   // keep the replay window off the dialog being filled
        }
        if (PaletteFill.TakePressFailure() is { } pressFailed) { Finish(false, true, pressFailed); return; }
        if (_paletteCommand != null) { TickPaletteCommand(); return; }
        if (_dialog.Active) { TickDialog(); return; }
        if (_modalDepth > 0)
        {
            if (_mode == ReplayMode.Auto && Unattended)
            {
                // a dialog needs a person; give transient ones a moment to close by themselves, and a progress window (no button to
                // press: AutoCAD Electrical's "Updating..." while a native palette switches projects) up to two minutes
                if (_modalSince == DateTime.MinValue) _modalSince = DateTime.Now;
                var waited = (DateTime.Now - _modalSince).TotalSeconds;
                if (waited > 2 && (waited > 120 || !Native.IsProgressWindow(Native.ForegroundOfThisProcess())))
                {
                    if (_suite != null) { StopAtUnexpectedDialog(); return; }
                    var title = _steps.Skip(Math.Max(0, _i - 1)).FirstOrDefault(x => x.Kind == "dialog")?.DialogTitle;
                    var di = _steps.FindIndex(Math.Max(0, _i - 1), x => x.Kind == "dialog");
                    if (di >= 0) _result.Steps.Add(new StepResult { Index = di + 1, Kind = "dialog", Title = _steps[di].Title, Status = "needs-person" });
                    Finish(false, false, $"needs a person: dialog {(string.IsNullOrWhiteSpace(title) ? "(untitled)" : title)}", needsPerson: true);
                    return;
                }
            }
            _win.Show($"Step {_i}/{_steps.Count}: dialog open", "Fill in the dialog (the replay continues after it closes).", false);
            return;
        }
        _modalSince = DateTime.MinValue;
        if (_awaitOpen != null) { TickAwaitOpen(); return; }
        if (!Settled()) return;
        RestoreView();   // the step is done: the person's view comes back
        if (_i >= _steps.Count) { Finish(true, false, "finished"); return; }

        var s = _steps[_i];
        _clock?.Begin(_i + 1, DateTime.Now);   // suite: this step runs from now (it is sent next) until the next one starts
        if (!_shown)
        {
            _shown = true; _stepShownAt = DateTime.Now; _modalsAtStepStart = _modalsSeen; _userNext = false;
            _win.Show($"Step {_i + 1}/{_steps.Count}", Describe(s), _mode == ReplayMode.Step);
            Say($"\n[replay {_i + 1}/{_steps.Count}] {s.Title}\n");
        }
        if (_mode == ReplayMode.Step && !_userNext) return;
        if (_tdoc != null && !_tdoc.BeforeStep(_i, s, DateTime.Now)) return;   // Tdoc: the picture of the step is taken first

        if (s.Kind == "palette") { TickPalette(s); return; }

        // a dialog step needs a person; unattended (Auto) the run cannot go on
        if (s.Kind == "dialog" && _mode == ReplayMode.Auto)
        {
            _result.Steps.Add(new StepResult { Index = _i + 1, Kind = "dialog", Title = s.Title, Status = "needs-person" });
            Finish(false, false, $"needs a person: dialog {(string.IsNullOrWhiteSpace(s.DialogTitle) ? "(untitled)" : s.DialogTitle)}", needsPerson: true);
            return;
        }

        if (!Execute(s)) return;
        _tdoc?.AfterStep(_i, s, _result);   // Tdoc: a checkpoint's result goes into the manifest
        _result.Steps.Add(new StepResult { Index = _i + 1, Kind = s.Kind, Title = s.Title });
        _i++;
        _shown = false;
        if (_notBefore < DateTime.Now.AddMilliseconds(400)) _notBefore = DateTime.Now.AddMilliseconds(400);
    }

    /// <summary>The title of the dialog in front now; "" when it is AutoCAD's own window or there is none.</summary>
    static string ForegroundDialogTitle()
    {
        var fg = Native.ForegroundOfThisProcess();
        return fg != IntPtr.Zero && fg != AcApp.MainWindow.Handle ? (Native.TitleOf(fg) ?? "").Trim() : "";
    }

    /// <summary>Suite run: a dialog nothing in the recording opens is showing. The step that was sent (the last one started) gets the needs-person
    /// status on its own result line and the dialog's real title goes in the message.</summary>
    void StopAtUnexpectedDialog()
    {
        var title = ForegroundDialogTitle();
        var shown = title.Length == 0 ? "(untitled)" : title;
        if (_i == 0)
        {
            // before any step was sent: nothing to blame
            var first = $"a dialog appeared before the first step: {shown}";
            _result.Steps.Add(new StepResult { Index = 0, Kind = "start", Title = "before the first step", Status = "needs-person", Message = first });
            Finish(false, false, $"needs a person: {first}", needsPerson: true);
            return;
        }
        var n = Math.Clamp(_i, 1, Math.Max(1, _steps.Count));   // _i is already past the step that was sent
        var step = _steps.Count >= n ? _steps[n - 1] : null;
        var text = $"step {n} opened a dialog the recording does not have: {shown}";
        var row = _result.Steps.LastOrDefault(x => x.Index == n);
        if (row != null) { row.Status = "needs-person"; row.Message = text; }
        else _result.Steps.Add(new StepResult { Index = n, Kind = step?.Kind ?? "", Title = step?.Title ?? "", Status = "needs-person", Message = text });
        Finish(false, false, $"needs a person: {text}", needsPerson: true);
    }

    /// <summary>Suite run: the step that was sent (or is being waited for) has run longer than the limit. ^C^C, the test is ERROR "step N timed out".
    /// True when the run ended.</summary>
    bool CheckStepTimeout()
    {
        var now = DateTime.Now;
        var clock = _clock!;
        if (clock.Step == 0) clock.Begin(1, now);   // the start-up wait counts for the first step
        if (!clock.Expired(now)) return false;
        var n = Math.Clamp(clock.Step, 1, Math.Max(1, _steps.Count));
        var secs = (int)clock.Limit.TotalSeconds;
        try { _doc.SendStringToExecute("\u0003\u0003", true, false, true); } catch (Exception ex) { Log.Error("suite cancel", ex); }
        var step = _steps.Count >= n ? _steps[n - 1] : null;
        var text = $"step {n} timed out after {secs} s";
        // a step that was sent has a result already (ok): that line becomes the timed-out one
        var existing = _result.Steps.LastOrDefault(x => x.Index == n);
        if (existing != null) { existing.Status = "timed-out"; existing.Message = text; }
        else _result.Steps.Add(new StepResult { Index = n, Kind = step?.Kind ?? "", Title = step?.Title ?? "", Status = "timed-out", Message = text });
        Finish(false, true, $"step {n} timed out");
        return true;
    }

    void StopAtSecurityPrompt(string title)
    {
        var msg = SecurityPrompt.Message(title);
        // the step that loaded the file is the last one started (_i was advanced past it); 1 if none had started yet
        _result.Steps.Add(new StepResult { Index = Math.Max(1, Math.Min(_i, _steps.Count)), Kind = "security-prompt", Title = title.Trim(), Status = "blocked", Message = msg });
        _alert = false;   // an alert box on top of the open prompt would only be a second dialog to answer; the command line and the result file say it
        Finish(false, false, msg, needsPerson: true);
    }

    /// <summary>Interactive dialog pause: see DialogWait. Runs instead of the normal step logic while a dialog step is pending.</summary>
    void TickDialog()
    {
        var d = _dialog;
        d.Tick(DateTime.Now);
        var step = _steps[Math.Min(_i, _steps.Count - 1)];
        switch (d.Phase)
        {
            case DialogWaitPhase.WaitingForOpen:
                _win.Show($"Step {_i + 1}/{_steps.Count}: waiting for the dialog", $"The replayed command should open \"{step.DialogTitle}\" (waits up to {DialogWait.OpenTimeoutSeconds} s).", false);
                return;
            case DialogWaitPhase.WaitingForPerson:
            {
                if (!_fillTried) { _fillTried = true; _fill = MakeFiller(step); }
                if (_fill != null && _fill.State != DialogAutoFiller.Phase.GaveUp)
                {
                    _fill.Tick(DateTime.Now);
                    if (_fill.State != DialogAutoFiller.Phase.GaveUp)
                    {
                        var what = $"Step {_i + 1}: filling the dialog {step.DialogTitle}...";
                        if (!_fillAnnounced) { _fillAnnounced = true; Say("\n" + what + "\n"); }
                        _win.Show($"Step {_i + 1}/{_steps.Count}: filling the dialog", what, false);
                        return;
                    }
                    Log.Info($"autofill gave up on \"{step.DialogTitle}\": {_fill.Note ?? string.Join("; ", _fill.Results.Where(r => r.Status != FieldFillResult.Applied).Select(r => r.Label + " " + r.Status + (r.Message != null ? " (" + r.Message + ")" : "")))}");
                    _tdoc?.EnsureShown();
                }
                if (_suite != null)
                {
                    // unattended: nobody is there to fill it in
                    var actual = ForegroundDialogTitle();
                    var recorded = string.IsNullOrWhiteSpace(step.DialogTitle) ? "" : step.DialogTitle.Trim();
                    var title = actual.Length == 0 ? (recorded.Length == 0 ? "(untitled)" : recorded)
                        : recorded.Length > 0 && !string.Equals(actual, recorded, StringComparison.OrdinalIgnoreCase) ? $"{actual} (recorded: {recorded})" : actual;
                    _result.Steps.Add(new StepResult { Index = _i + 1, Kind = "dialog", Title = step.Title, Status = "needs-person", Message = $"the dialog needs a person: {title}" });
                    Finish(false, false, $"needs a person: dialog {title}", needsPerson: true);
                    return;
                }
                var msg = DialogMessage(step);
                if (!_announced) { _announced = true; Say("\n" + msg + "\n"); }
                _win.Show($"Step {_i + 1}/{_steps.Count}: dialog open", msg, false);
                return;
            }
        }
        // Done: the person confirmed the dialog; now type the answers the command asks for after it (once)
        // (a WinForms dialog's Cancel usually ends its command normally: give it a moment, then type only if the command still runs)
        if (d.Phase == DialogWaitPhase.Done && _deferred != null && _modalDepth == 0)
        {
            if (_doneAt == DateTime.MinValue) { _doneAt = DateTime.Now; return; }
            if ((DateTime.Now - _doneAt).TotalMilliseconds < 500) return;
            if (!string.IsNullOrEmpty(_doc.CommandInProgress))
            {
                _doc.SendStringToExecute(_deferred, true, false, true);
                _notBefore = DateTime.Now.AddMilliseconds(400);
            }
            else d.FailAfterClose("the command ended when the dialog closed (was it cancelled?)");
            _deferred = null; _doneAt = DateTime.MinValue;
            if (d.Phase == DialogWaitPhase.Done) return;
        }
        if (d.Phase == DialogWaitPhase.Failed) _deferred = null;
        // Done or Failed. A failed wait may leave the command running (it never opened the dialog): cancel it so the replay can go on.
        if (d.Phase == DialogWaitPhase.Failed && !_cancelSent && !string.IsNullOrEmpty(_doc.CommandInProgress))
        {
            _cancelSent = true;
            _doc.SendStringToExecute("\u0003\u0003", true, false, true);
            _notBefore = DateTime.Now.AddMilliseconds(400);
        }
        // Done, and the next recorded step is a dialog that is open already: the command showed it right after this one closed (a LISP alert
        // after its DCL dialog). This step is done; the next one starts with its dialog open, so it is filled or waited for as its own step.
        // Waiting here for the command to finish would wait for that dialog, which no step handles.
        // Only when the open dialog is the one recorded next (its title): an unexpected warning keeps the old wait.
        bool chained = d.Phase == DialogWaitPhase.Done && _modalDepth > 0 && _deferred == null && _i + 1 < _steps.Count && _steps[_i + 1].Kind == "dialog"
            && Native.ForegroundOfThisProcess() is var open && open != IntPtr.Zero && FillMatcher.TitleMatches(_steps[_i + 1].DialogTitle, Native.TitleOf(open));
        if (!chained && (_modalDepth > 0 || !Settled())) return;   // Done: wait until the command that owned the dialog has finished (a cancel arrives as an event first)
        var failed = d.Phase == DialogWaitPhase.Failed;
        var auto = !failed && _fill?.State == DialogAutoFiller.Phase.Clicked;   // the replay filled it and closed it: nobody waited
        var first = true;
        while (_i < _steps.Count && _steps[_i].Kind == "dialog" && (first || !chained))
        {
            _result.Steps.Add(new StepResult
            {
                Index = _i + 1, Kind = "dialog", Title = _steps[_i].Title, Status = failed ? "failed" : "ok", WaitedForPerson = d.PersonWaited && !auto, Message = failed ? d.FailReason : null,
                AutoFilled = first && auto,
                Fields = first && _fill is { Mode: AutoMode.Ok } && _fill.Results.Count > 0 ? _fill.Results : null,
            });
            first = false;
            _i++;
        }
        if (failed) Say($"\nDialog step failed: {d.FailReason}. Continuing with the next steps.\n");
        d.Reset(); _doneAt = DateTime.MinValue;
        _fill = null; _fillTried = false; _fillAnnounced = false;
        _announced = false; _cancelSent = false; _shown = false;
        _notBefore = DateTime.Now.AddMilliseconds(chained ? 0 : 400);
    }

    /// <summary>"A dialog opens: X. Fill it in and confirm (see s.md, step N), then close it with OK." Step N is read from the script the engine wrote.</summary>
    string DialogMessage(Step step)
    {
        ScriptPosition(out var file, out var n);
        if (_fill != null) return FillMatcher.FallbackMessage(step.DialogTitle, file, n, step.Values, _fill.Results, step.ClosedWith, _fill.Note);
        return ScriptRef.Message(step.DialogTitle, file, n, step.Values, step.ValuesRead, step.ClosedWith);
    }

    /// <summary>The auto-filler for this dialog step, or null when the setting is off or the recording says too little to go on.</summary>
    DialogAutoFiller? MakeFiller(Step step)
    {
        if (!_autoFillOn) return null;
        var mode = FillMatcher.Decide(step.Values != null, step.Values?.Count ?? 0, step.ValuesRead, step.ClosedWith);
        if (mode == AutoMode.None) return null;
        var main = IntPtr.Zero;
        try { main = Autodesk.AutoCAD.ApplicationServices.Application.MainWindow.Handle; } catch (Exception ex) { Log.Error("autofill main window", ex); }
        var tdoc = _tdoc;
        var at = _i;
        return new DialogAutoFiller(mode, step.Values, step.DialogTitle, step.ValuesSource, main)
        {
            BeforeClose = tdoc == null ? null : shot => tdoc.DialogReady(at, shot, DateTime.Now),   // Tdoc: the picture of the filled dialog comes before OK
        };
    }

    void ScriptPosition(out string? file, out int? n)
    {
        n = null; file = null;
        try
        {
            var md = Path.ChangeExtension(_sessionPath, ".md");
            if (File.Exists(md))
            {
                file = Path.GetFileName(md);
                var ordinal = _steps.Take(_i).Count(x => x.Kind == "dialog") + 1;
                n = ScriptRef.DialogStepNumber(File.ReadAllText(md), ordinal);
            }
        }
        catch (Exception ex) { Log.Error("dialog script ref", ex); }
    }

    /// <summary>How long a palette step is retried while it cannot be done yet: results of an async call still loading, a button still disabled.</summary>
    public static readonly TimeSpan PaletteWait = TimeSpan.FromSeconds(15);

    /// <summary>A palette step: Retrace sets the values and clicks the button (<see cref="PaletteFill"/>), retrying for up to
    /// <see cref="PaletteWait"/>; when it still cannot, a person does it and clicks Next, and an unattended run stops there. A button that started
    /// a command (<see cref="Step.Command"/>) gets the recorded answers once that command runs (<see cref="TickPaletteCommand"/>).</summary>
    void TickPalette(Step s)
    {
        var now = DateTime.Now;
        if (_paletteForPerson)
        {
            if (!_userNext) return;
            _result.Steps.Add(new StepResult { Index = _i + 1, Kind = "palette", Title = s.Title, WaitedForPerson = true });
            NextPaletteStep();
            return;
        }
        if (_showUi && !_revealDone && !Reveal(s, now)) return;
        if (_paletteSince == DateTime.MinValue) _paletteSince = now;
        if (now < _paletteRetryAt) return;
        var problem = s.Native != null
            ? NativePalette.Run(s, (x, y) => { if (_cursorHome == null && PaletteReveal.GetCursorPos(out var home)) _cursorHome = home; MoveCursor(new PaletteReveal.POINT { X = x, Y = y }); })
            : PaletteFill.Run(s);
        if (problem == null)
        {
            Say($"\n  Palette \"{s.DialogTitle}\": done.\n");
            _result.Steps.Add(new StepResult { Index = _i + 1, Kind = "palette", Title = s.Title });
            // a right-click in a native palette: its menu is not replayed, the command picked from it is typed (the item is selected now)
            if (s.Native is { Action: "rclick" } && s.Command != null)
            {
                var typed = Plan.InputText(new Step { Kind = "command", Command = s.Command, Answers = s.Answers }, handleExists: HandleExists);
                if (!string.IsNullOrEmpty(typed)) { _doc.SendStringToExecute(typed, true, false, true); _pendingStart = true; _sentAt = now; }
                NextPaletteStep();
                return;
            }
            if (s.Drawing != null) { _awaitOpen = s; _awaitSince = now; return; }
            // a command a native action starts with nothing to answer is the palette's own doing (AEMTRELOAD after a project switch; not run
            // at all when the project was active already): nothing to wait for. One with answers is waited for, from the step's first try.
            if (s.Command != null && !(s.Native != null && s.Answers.Count == 0))
            { _paletteCommand = s; _paletteCommandStarted = false; _paletteClickedAt = s.Native != null ? _paletteSince : now; return; }
            NextPaletteStep();
            return;
        }
        if (now - _paletteSince < PaletteWait) { _paletteRetryAt = now.AddMilliseconds(500); return; }
        if (_mode == ReplayMode.Auto && Unattended)
        {
            _result.Steps.Add(new StepResult { Index = _i + 1, Kind = "palette", Title = s.Title, Status = "needs-person", Message = problem });
            Finish(false, false, $"needs a person: palette {s.DialogTitle} ({problem})", needsPerson: true);
            return;
        }
        Say($"\n  Palette \"{s.DialogTitle}\": {problem}. Do it by hand, then click Next.\n");
        _win.Show($"Step {_i + 1}/{_steps.Count}: palette", $"{s.Title}\nRetrace could not do it: {problem}.\nDo it in the palette, then click Next.", true);
        _userNext = false;
        _paletteForPerson = true;
    }

    /// <summary>The palette button was clicked and should start <see cref="Step.Command"/>: once it runs, type the recorded answers into it (the
    /// command started from the button's own SendStringToExecute, after the posted click).</summary>
    void TickPaletteCommand()
    {
        var s = _paletteCommand!;
        bool running = string.Equals(_doc.CommandInProgress, s.Command, StringComparison.OrdinalIgnoreCase);
        // started (and perhaps ended) while the native palette action ran, before this step waited for it
        if (s.Native != null && _lastStartedAt >= _paletteClickedAt && string.Equals(_lastStarted, s.Command, StringComparison.OrdinalIgnoreCase)) _paletteCommandStarted = true;
        if (running || _paletteCommandStarted)
        {
            var text = running ? WithPick(pick => Plan.InputText(s, includeStart: false, handleExists: HandleExists, pick: pick), r => !string.IsNullOrEmpty(r)) : null;
            if (running && !string.IsNullOrEmpty(text)) _doc.SendStringToExecute(text, true, false, true);
            _paletteCommand = null;
            NextPaletteStep();
            _pendingStart = true; _sentAt = DateTime.Now;   // Settled waits until the command is done
            return;
        }
        if (DateTime.Now - _paletteClickedAt > TimeSpan.FromSeconds(30))   // as long as the recorder links a command to a click (an awaited call first)
        {
            _paletteCommand = null;
            Finish(false, true, $"the {s.Button} button in the {s.DialogTitle} palette did not start {s.Command}");
        }
    }

    /// <summary>Opens a rolled-up or auto-hidden palette before it is driven: rests the mouse on its title strip or tab (trying the likely spots,
    /// 1.2 s each) until it is shown, then keeps the mouse inside it and Retrace's window off it. False while still waiting. When it does not
    /// open, the step is done with the palette hidden (as with showDrivenUi off).</summary>
    bool Reveal(Step s, DateTime now)
    {
        var page = PaletteFill.FindPage(s);
        if (page == IntPtr.Zero) { _revealDone = true; return true; }   // not open yet: PaletteFill says so and retries
        _cursorBackAt = DateTime.MaxValue;   // the restore of the previous palette waits: this one may need the mouse
        if (!PaletteReveal.IsShown(page) && PaletteReveal.SetIsOpen(page)) { _revealDone = true; return true; }   // another tab of an open set: hovering changes nothing
        if (PaletteReveal.IsShown(page))
        {
            if (_cursorHome != null) { var inside = PaletteReveal.Inside(page); MoveCursor(inside); }
            var r = PaletteReveal.Screen(page);
            _win.Avoid(r.Left, r.Top, r.Right, r.Bottom);
            _revealDone = true;
            return true;
        }
        if (_revealTry < 0 || now - _revealAt > TimeSpan.FromMilliseconds(1200))
        {
            var points = PaletteReveal.HoverPoints(page);
            if (++_revealTry >= points.Count) { Log.Info($"replay: could not open the {s.DialogTitle} palette; driving it hidden"); _revealDone = true; return true; }
            if (_cursorHome == null && PaletteReveal.GetCursorPos(out var home)) _cursorHome = home;
            MoveCursor(points[_revealTry]);
            _revealAt = now;
        }
        return false;
    }

    void MoveCursor(PaletteReveal.POINT p) { PaletteReveal.SetCursorPos(p.X, p.Y); _cursorSet = p; }

    /// <summary>Puts the mouse back where it was before a palette was opened for the replay, unless the user has moved it since.</summary>
    void CursorBack()
    {
        if (_cursorHome is { } h && _cursorSet is { } set && PaletteReveal.GetCursorPos(out var now) && Math.Abs(now.X - set.X) <= 3 && Math.Abs(now.Y - set.Y) <= 3)
            PaletteReveal.SetCursorPos(h.X, h.Y);
        _cursorHome = null; _cursorSet = null;
        _cursorBackAt = DateTime.MaxValue;
    }

    void NextPaletteStep()
    {
        // the next step in the same palette keeps it open; otherwise the mouse goes back once the press has had time to show its result
        var next = _i + 1 < _steps.Count ? _steps[_i + 1] : null;
        var cur = _steps[_i];
        if (_cursorHome != null && !(next is { Kind: "palette" } && next.DialogTitle == cur.DialogTitle && next.Panel == cur.Panel))
            _cursorBackAt = DateTime.Now.AddMilliseconds(900);
        _revealDone = false; _revealTry = -1;
        _i++;
        _shown = false;
        _paletteForPerson = false;
        _paletteSince = DateTime.MinValue; _paletteRetryAt = DateTime.MinValue;
        _notBefore = DateTime.Now.AddMilliseconds(800);   // a clicked button runs from the palette's own loop, after this tick
    }

    static string Describe(Step s)
    {
        var t = s.Title;
        if (s.Kind is "ribbon" or "command" or "lisp" && s.Answers.Count > 0)
            t += "\nThen answer: " + string.Join(" | ", s.Answers.Select(a => $"{a.Message ?? a.Kind} -> {Summ(a)}"));
        return t;
    }

    static string Summ(Answer a) => a.Status switch
    {
        "OK" => a.Value?.ToString() ?? "",
        "Keyword" => a.Keyword ?? "keyword",
        "None" => "Enter",
        _ => a.Status,
    };

    /// <summary>Runs one step; false when the run ended in it.</summary>
    bool Execute(Step s)
    {
        switch (s.Kind)
        {
            case "open":
                return OpenDrawing(s);
            case "tab":
                ActivateTab(s.Tab);
                break;
            case "mark":
                Checkpoint(s);
                break;
            case "dialog":
                Say($"\n  Dialog \"{s.DialogTitle}\": fill it in by hand.\n");
                break;
            case "ribbon" or "command" or "lisp":
                return RunStep(s);
        }
        return true;
    }

    /// <summary>Compares the drawing with the digest recorded at this checkpoint.</summary>
    void Checkpoint(Step s)
    {
        var cp = new CheckpointResult { Index = ++_checks, Note = s.Note ?? "", Expected = s.State };
        _result.Checkpoints.Add(cp);
        var label = string.IsNullOrEmpty(cp.Note) ? "" : $" ({cp.Note})";
        var emptyChecks = s.Checks is { IsEmpty: true };
        if (s.State == null && (s.Checks == null || emptyChecks))
        {
            if (s.Checks != null) cp.ApplyChecks(s.Checks, new BehaviorSnapshot());   // records the mode; verifies nothing
            cp.Differences.Add("no state recorded for this checkpoint");
            Say($"\n  CHECK {cp.Index} SKIPPED{label}: no recorded state (older recording). EXPECTED: {s.Note}\n");
            _win.Show("Check " + cp.Index, "No recorded state. Expected: " + s.Note, false);
            return;
        }
        if (s.State != null)
        {
            // an older mark without checks keeps its layer-state compare, so it reads the layer state too
            cp.Actual = DrawingState.Read(_doc, withLayerState: s.Checks == null && s.State.LayerState != null);
            if (cp.Actual == null) { cp.Pass = false; cp.Differences.Add("could not read the drawing"); }
            else { cp.Differences = DrawingDigest.Compare(s.State, cp.Actual, compareLayerState: s.Checks == null); cp.Pass = cp.Differences.Count == 0; }
        }
        var checkLines = new List<string>();
        if (s.Checks != null && emptyChecks) cp.ApplyChecks(s.Checks, new BehaviorSnapshot());   // nothing to verify: no capture, cannot fail
        else if (s.Checks != null)
        {
            // only the behaviors the checks name are read
            var actual = BehaviorCapture.Read(_doc, RetraceSettings.Load(), s.Checks.Behaviors.Keys.ToList());
            if (actual == null)
            {
                cp.Pass = false;
                cp.CheckMode = s.Checks.Mode;
                cp.CheckedBehaviors = s.Checks.Behaviors.Keys.ToList();
                cp.CheckDifferences = new List<string> { "could not read the drawing's behaviors" };
            }
            else cp.ApplyChecks(s.Checks, actual);
            checkLines = cp.CheckDifferences ?? checkLines;
        }
        var all = cp.Differences.Concat(checkLines).ToList();
        var line = cp.Pass == true ? $"CHECK {cp.Index} PASS" : $"CHECK {cp.Index} FAIL: {string.Join("; ", all)}";
        Say($"\n  {line}{label}\n");
        _win.Show("Check " + cp.Index + (cp.Pass == true ? " PASS" : " FAIL"), (cp.Note.Length > 0 ? cp.Note + "\n" : "") + string.Join("\n", all), false);
    }

    // ---- the view of picks and clicks (PickReplay): shown before the step's points are typed, the person's view put back once the step has settled ----
    bool _userStopped;   // the Stop button ended the run (the only ending, with an error, that cancels a running command)
    Autodesk.AutoCAD.DatabaseServices.ViewTableRecord? _savedView;
    Document? _savedViewDoc;

    PickReplay NewPick()
    {
        var checker = new PickChecker(_doc);   // reads the view, the UCS and the pickbox once, and lists the space once, for this step
        return new() { ViewProblem = checker.ViewProblem, PickProblem = checker.Problem };
    }

    /// <summary>Builds the text of a step with pick points and views allowed, shows the view the points need, and, when that view cannot be shown, builds the text again without
    /// them (handles, as before): a typed point is only used with its view.</summary>
    T WithPick<T>(Func<PickReplay?, T> build, Func<T, bool> hasText)
    {
        var pick = NewPick();
        var result = build(pick);
        if (!hasText(result)) return result;
        if (ShowPickView(pick)) return result;
        var again = build(null);
        Say("\n  The recorded view could not be shown: the picks and clicks of this step use handles.\n");
        Log.Info("replay pick: the recorded view could not be shown, handles used");
        return again;
    }

    /// <summary>Says what the pick decisions of this step were (what fell back to a handle, and why), and shows the view the typed points need. False when a view was needed and could not be shown.</summary>
    bool ShowPickView(PickReplay pick)
    {
        foreach (var n in pick.Notes) { Say($"\n  {n}\n"); Log.Info("replay pick: " + n); }
        if (pick.View is not { } view) return true;
        var saved = ViewControl.Apply(_doc, view);
        if (saved == null) return false;
        if (_savedView == null) { _savedView = saved; _savedViewDoc = _doc; }   // the person's own view, kept from the first change of the step
        else saved.Dispose();
        Log.Info($"replay view shown: {view}");
        return true;
    }

    /// <summary>Puts the person's view back once the step has settled: only when the drawing is quiet (no command, no queued input, no dialog); else the saved view is kept for the next tick.
    /// A restore that fails keeps the view here too, to be tried again after the next step settles: while the run goes on it is never handed to <see cref="ViewRestorer"/>, and it stays the
    /// person's own view (a later step does not save over it). Only a drawing that is gone has nothing to restore.</summary>
    void RestoreView()
    {
        var saved = _savedView; var doc = _savedViewDoc;
        if (saved == null) return;
        if (doc == null || !AcApp.DocumentManager.Cast<Document>().Contains(doc)) { _savedView = null; _savedViewDoc = null; saved.Dispose(); return; }
        if (!ViewRestorer.Quiet(doc)) return;
        if (ViewControl.Restore(doc, saved)) { _savedView = null; _savedViewDoc = null; Log.Info("replay view restored"); }
        else Log.Info("the view the person had could not be put back yet, tried again after the next step");   // Restore keeps the view on failure
    }

    /// <summary>The run ends: the person's view is handed to <see cref="ViewRestorer"/>, which sets it once nothing is running, no input is queued and no dialog is open (and drops it if the
    /// person changed the view meanwhile). A running command is cancelled (Esc Esc) only when <paramref name="cancel"/>: Stop, or an error. A "needs a person" ending, a security prompt and
    /// the unloading of the plugin leave the command and its dialogs to the person, and nothing is sent to AutoCAD then or when the replay's drawing is not the active one.</summary>
    void HandOffView(bool cancel)
    {
        var saved = _savedView; var doc = _savedViewDoc;
        _savedView = null; _savedViewDoc = null;
        if (saved == null) return;
        if (ViewRestorer.IsShutdown || doc == null || !AcApp.DocumentManager.Cast<Document>().Contains(doc)) { saved.Dispose(); return; }
        if (cancel && !ViewRestorer.Quiet(doc) && ReferenceEquals(doc, AcApp.DocumentManager.MdiActiveDocument))
        {
            try { doc.SendStringToExecute("\u0003\u0003", false, false, false); } catch (Exception ex) { Log.Error("cancel before restoring the view", ex); }
        }
        ViewRestorer.Hand(doc, saved);
    }

    /// <summary>Handle fallback of a recorded selection: the handle must exist in this drawing and hold the recorded kind of object.</summary>
    bool HandleExists(string handle, string? dxf)
    {
        try { return HandleIds.Exists(_doc.Database, handle, dxf); }
        catch (Exception ex) { Log.Error("handle check", ex); return false; }
    }

    /// <summary>Starts a command step; false when the run ended in it (a step Retrace cannot type: a person must do it).</summary>
    bool RunStep(Step s)
    {
        if (Plan.Problem(s) is { } problem)
        {
            // typing it would put text where it does not belong; an unattended run must not end as PASS without it
            Say($"\n  WARNING: {problem}\n");
            _deferred = null;
            _result.Steps.Add(new StepResult { Index = _i + 1, Kind = s.Kind, Title = s.Title, Status = "needs-person", Message = problem });
            Finish(false, false, $"needs a person: {problem}", needsPerson: true);
            return false;
        }
        _clicked = null;
        if (s.Kind == "ribbon" && s.Answers.Count == 0 && !s.HadDialog)
        {
            // armed before the handler runs: a command it starts at once clears this in OnCmdStart and is neither waited for nor typed again
            _clicked = s; _clickedIndex = _i + 1; _pendingStart = true; _sentAt = DateTime.Now; _deferred = null;
            if (TryClick(s)) return true;
            _clicked = null; _pendingStart = false;
        }
        // interactive, and this step's command opens a dialog: hold back the answers given after the dialog opened
        var dialog = _interactive && _i + 1 < _steps.Count && _steps[_i + 1].Kind == "dialog" ? _steps[_i + 1] : null;
        string? text;
        // the view the points were made in is shown before they are typed and put back after the step; without it the step uses handles
        if (dialog != null) (text, _deferred) = WithPick(pick => Plan.InputAroundDialog(s, dialog.T, HandleExists, pick), r => !string.IsNullOrEmpty(r.First));
        else { text = WithPick(pick => Plan.InputText(s, handleExists: HandleExists, pick: pick), r => !string.IsNullOrEmpty(r)); _deferred = null; }
        if (string.IsNullOrEmpty(text)) { Say("\n  (nothing replayable in this step)\n"); return true; }
        Type(s, text);
        return true;
    }

    /// <summary>Types a step's text. SendStringToExecute: queued, runs as typed input when AutoCAD is next quiescent; "\n" is Enter.</summary>
    void Type(Step s, string text)
    {
        if (s.Kind == "ribbon") ActivateTab(s.Tab);
        _doc.SendStringToExecute(text, true, false, true);
        _pendingStart = true; _sentAt = DateTime.Now;
    }

    /// <summary>Invoke the live RibbonItem the way a click does: its CommandHandler.Execute(item). AutoCAD's own buttons (a RibbonCommandButton,
    /// its own handler, the macro in its Tag) and code-built buttons (a RibbonButton with an ICommand) both start their command this way from a
    /// timer tick (checked on AutoCAD 2026, 2026-11-04). The log names the item's type, handler and parameter, so an item that does nothing
    /// from code can be told apart from one that is gone.</summary>
    bool TryClick(Step s)
    {
        try
        {
            var ribbon = ComponentManager.Ribbon;
            if (ribbon == null || string.IsNullOrEmpty(s.ItemId)) return false;
            var item = ribbon.FindItem(s.ItemId, false, true);
            string why;
            if (item is not RibbonCommandItem ci) why = item == null ? "not in the ribbon" : $"a {item.GetType().Name}, not a command item";
            else if (!ci.IsEnabled) why = "disabled";
            else if (ci.CommandHandler is not { } handler) why = "without a handler";
            else if (!handler.CanExecute(ci)) why = "a handler that cannot execute now";
            else
            {
                var parameter = ci.CommandParameter is string m ? $"macro '{m}'" : ci.CommandParameter?.GetType().FullName ?? "none";
                Log.Info($"replay click: {s.ItemId} is a {ci.GetType().Name}, handler {handler.GetType().FullName}, parameter {parameter}, tag {ci.Tag?.GetType().Name ?? "none"}");
                ActivateTab(s.Tab);
                handler.Execute(ci);
                return true;
            }
            Log.Info($"replay click: {s.ItemId} is {why}; typing the macro instead");
            return false;
        }
        catch (Exception ex) { Log.Error("TryClick", ex); return false; }
    }

    /// <summary>The live item was invoked 2 s ago and no command started. When the recording has a command for that click, the handler did
    /// nothing from code (say a toggle that reads its own state first, or one that needs the click's own events), and the recorded macro or
    /// command is typed instead, as for an item that is gone; the result and the log say so. A click that recorded no command, or that opened
    /// a dialog, did its work without one, and nothing is typed. False when nothing was typed.</summary>
    bool TypeAfterSilentClick(Step s)
    {
        if (_modalsSeen > _modalsAtStepStart) { Log.Info($"replay click: {s.ItemId} started no command within 2 s but opened a dialog; nothing typed"); return false; }
        if (s.Command == null) { Log.Info($"replay click: {s.ItemId} started no command within 2 s; its recording has none either, nothing typed"); return false; }
        var text = Plan.InputText(s, handleExists: HandleExists);
        var row = _result.Steps.LastOrDefault(x => x.Index == _clickedIndex);
        if (string.IsNullOrEmpty(text))
        {
            var none = $"the click started no command within 2 s ({s.Command} when recorded), and the step has nothing to type instead";
            Say($"\n  WARNING: {none}\n");
            Log.Info($"replay click: {s.ItemId}: {none}");
            _warnings.Add($"step {_clickedIndex}: {none}");
            if (row != null) row.Message = none;
            return false;
        }
        var typed = $"the click started no command within 2 s ({s.Command} when recorded); its macro was typed instead";
        Say($"\n  {typed}\n");
        Log.Info($"replay click: {s.ItemId}: {typed}: '{text.Replace("\u0003", "^C").Replace("\u001b", "^C").Replace("\n", ";")}'");
        _warnings.Add($"step {_clickedIndex}: {typed}");
        if (row != null) row.Message = typed;
        Type(s, text);
        return true;
    }

    internal static void ActivateTab(string? title)
    {
        if (string.IsNullOrEmpty(title)) return;
        try
        {
            var ribbon = ComponentManager.Ribbon;
            var tab = ribbon?.Tabs.FirstOrDefault(t => t.Title == title || t.Name == title);
            if (tab != null && ribbon != null) ribbon.ActiveTab = tab; // setter assumed (see README)
        }
        catch (Exception ex) { Log.Error("ActivateTab", ex); }
    }
}
