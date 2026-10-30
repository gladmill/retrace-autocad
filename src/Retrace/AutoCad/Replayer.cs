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
///    if the item is gone or has no handler, send the recorded macro instead;
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
        };
        _win.NextClicked += () => _userNext = true;
        _win.StopClicked += () =>
        {
            if (_running && _tdoc != null) _tdoc.StopRequested = true;   // Stop ends a Tdoc run's whole queue (a normal end closes the window too: only while running)
            Finish(false, false, "stopped by the user");
        };
    }

    /// <summary>Stops a running replay (timer, event handlers, panel). Used when the plugin is shut down.</summary>
    public static void StopCurrent()
    {
        var c = Current; if (c == null) return;
        c._alert = false;
        if (c._tdoc != null) c._tdoc.StopRequested = true;
        c.Finish(false, false, "stopped (core shutdown)");
    }

    /// <summary>Starts a replay in the active drawing (call from the RETRACEREPLAY command). <paramref name="initial"/> is the session's
    /// initial drawing state (null for older sessions).</summary>
    /// <param name="tdoc">a Tdoc run's pictures for this test (RETRACETDOC); null for a plain replay</param>
    /// <param name="finished">called once at the end of the run with its result (a Tdoc run starts the next test from it)</param>
    public static bool Start(List<Step> steps, ReplayMode mode, string sessionPath, DrawingDigest? initial, out string message, bool alert = false, bool interactive = false,
        TdocCapture? tdoc = null, Action<ReplayResult>? finished = null)
    {
        if (Current != null) { message = "A replay is already running (close its window or press Stop)."; return false; }
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc == null) { message = "No active drawing."; return false; }
        var r = new Replayer(steps, mode, doc, sessionPath, interactive) { _alert = alert, _tdoc = tdoc, _finished = finished };
        Current = r;
        r.Begin(initial);
        message = $"Replaying {steps.Count} steps ({mode}).";
        return true;
    }

    void Begin(DrawingDigest? initial)
    {
        _result.Session = TestFolders.NameOf(_sessionPath);
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
        _win.Show();
        _win.Show("Retrace replay", "Starting...", false);
        _timer.Start();
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
            if (doc != null && !(bool)((dynamic)doc.GetAcadDocument()).Saved)
            {
                Finish(false, true, $"{Path.GetFileName(path)} is open with unsaved changes: save or close it, then run the test again");
                return false;
            }
            if (doc == null)
            {
                if (!File.Exists(path)) { Finish(false, true, $"drawing not found: {path}"); return false; }
                doc = dm.Open(path, false);
            }
            Follow(doc);
            Say($"\n  Opened {path}\n");
            return true;
        }
        catch (Exception ex) { Log.Error("OpenDrawing", ex); Finish(false, true, $"could not open {path}: {ex.Message}"); return false; }
    }

    /// <summary>Replays the rest in <paramref name="doc"/>, a drawing just opened. Its start-up (acaddoc.lsp, S::STARTUP, AutoCAD Electrical's
    /// project setup) runs after this, partly on the drawing's first idle: the next step waits until it has been quiet (no command, no
    /// LISP) for a second.</summary>
    void Follow(Document doc)
    {
        var dm = AcApp.DocumentManager;
        if (dm.MdiActiveDocument != doc) dm.MdiActiveDocument = doc;
        if (!ReferenceEquals(doc, _doc)) { Log.Safe("Replay unhook", () => Unhook(_doc)); _doc = doc; Hook(_doc); }
        _lispRunning = 0; _afterOpen = true; _quietSince = DateTime.MinValue;
        _notBefore = DateTime.Now.AddMilliseconds(500);
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
            Follow(doc);
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
        var now = DrawingState.Read(_doc);
        if (now == null) { _result.StartStateMatches = false; _warnings.Add("start state could not be read"); ed.WriteMessage("\nWARNING: could not read the drawing to compare with the start state.\n"); return; }
        var diffs = DrawingDigest.Compare(initial, now);
        _result.StartStateMatches = diffs.Count == 0;
        if (diffs.Count == 0) { ed.WriteMessage("\nStart state matches the recording.\n"); return; }
        var text = "start state differs from the recording (" + string.Join("; ", diffs) + ")";
        _warnings.Add(text);
        ed.WriteMessage($"\nWARNING: {text}. Replaying anyway; later checkpoints may fail because of this.\n");
    }

    /// <summary>Ends the run: detaches everything, writes the result file and prints the one-line summary.</summary>
    void Finish(bool completed, bool errored, string message)
    {
        if (!_running) return;
        _running = false;
        CursorBack();
        _timer.Stop();
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
        _pendingStart = false;
        _lastStarted = e.GlobalCommandName; _lastStartedAt = DateTime.Now;
        // a command without prompts can start and end between two ticks: remember that the palette button's command started
        if (_paletteCommand != null && string.Equals(e.GlobalCommandName, _paletteCommand.Command, StringComparison.OrdinalIgnoreCase)) _paletteCommandStarted = true;
    }

    bool Settled()
    {
        if (_modalDepth > 0) return false;
        if (DateTime.Now < _notBefore) return false;
        if (_pendingStart && (DateTime.Now - _sentAt).TotalSeconds < 2) return false; // wait for the command to actually start
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
        if (_docGone)
        {
            if (AcApp.DocumentManager.MdiActiveDocument is not { } active || ReferenceEquals(active, _doc)) return;   // between drawings
            _docGone = false;
            _doc = active; Hook(_doc);
            Log.Info($"replay continues in {active.Name}: the drawing it ran in was closed");
        }
        if (_eventError != null) { Finish(false, true, _eventError); return; }
        // AutoCAD's security prompt (an untrusted dll or LISP file is being loaded) is a decision for a person, never a test step:
        // stop at once with what to do, in both modes, and leave the prompt open for the person to answer.
        if (Native.OpenSecurityPrompt() is { } prompt) { StopAtSecurityPrompt(prompt); return; }
        // interactive: the step that opens a dialog was sent; wait for the dialog and the person
        if (_interactive && !_dialog.Active && _i < _steps.Count && _steps[_i].Kind == "dialog") 
        {
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
            if (_mode == ReplayMode.Auto && !_interactive)
            {
                // a dialog needs a person; give transient ones a moment to close by themselves, and a progress window (no button to
                // press: AutoCAD Electrical's "Updating..." while a native palette switches projects) up to two minutes
                if (_modalSince == DateTime.MinValue) _modalSince = DateTime.Now;
                var waited = (DateTime.Now - _modalSince).TotalSeconds;
                if (waited > 2 && (waited > 120 || !Native.IsProgressWindow(Native.ForegroundOfThisProcess())))
                {
                    var title = _steps.Skip(Math.Max(0, _i - 1)).FirstOrDefault(x => x.Kind == "dialog")?.DialogTitle;
                    var di = _steps.FindIndex(Math.Max(0, _i - 1), x => x.Kind == "dialog");
                    if (di >= 0) _result.Steps.Add(new StepResult { Index = di + 1, Kind = "dialog", Title = _steps[di].Title, Status = "needs-person" });
                    Finish(false, false, $"needs a person: dialog {(string.IsNullOrWhiteSpace(title) ? "(untitled)" : title)}");
                    return;
                }
            }
            _win.Show($"Step {_i}/{_steps.Count}: dialog open", "Fill in the dialog (the replay continues after it closes).", false);
            return;
        }
        _modalSince = DateTime.MinValue;
        if (_awaitOpen != null) { TickAwaitOpen(); return; }
        if (!Settled()) return;
        if (_i >= _steps.Count) { Finish(true, false, "finished"); return; }

        var s = _steps[_i];
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
            Finish(false, false, $"needs a person: dialog {(string.IsNullOrWhiteSpace(s.DialogTitle) ? "(untitled)" : s.DialogTitle)}");
            return;
        }

        if (!Execute(s)) return;
        _tdoc?.AfterStep(_i, s, _result);   // Tdoc: a checkpoint's result goes into the manifest
        _result.Steps.Add(new StepResult { Index = _i + 1, Kind = s.Kind, Title = s.Title });
        _i++;
        _shown = false;
        if (_notBefore < DateTime.Now.AddMilliseconds(400)) _notBefore = DateTime.Now.AddMilliseconds(400);
    }

    void StopAtSecurityPrompt(string title)
    {
        var msg = SecurityPrompt.Message(title);
        // the step that loaded the file is the last one started (_i was advanced past it); 1 if none had started yet
        _result.Steps.Add(new StepResult { Index = Math.Max(1, Math.Min(_i, _steps.Count)), Kind = "security-prompt", Title = title.Trim(), Status = "blocked", Message = msg });
        _alert = false;   // an alert box on top of the open prompt would only be a second dialog to answer; the command line and the result file say it
        Finish(false, false, msg);
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
                var typed = Plan.InputText(new Step { Kind = "command", Command = s.Command, Answers = s.Answers });
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
        if (_mode == ReplayMode.Auto && !_interactive)
        {
            _result.Steps.Add(new StepResult { Index = _i + 1, Kind = "palette", Title = s.Title, Status = "needs-person", Message = problem });
            Finish(false, false, $"needs a person: palette {s.DialogTitle} ({problem})");
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
            var text = Plan.InputText(s, includeStart: false);
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
                RunStep(s);
                break;
        }
        return true;
    }

    /// <summary>Compares the drawing with the digest recorded at this checkpoint.</summary>
    void Checkpoint(Step s)
    {
        var cp = new CheckpointResult { Index = ++_checks, Note = s.Note ?? "", Expected = s.State };
        _result.Checkpoints.Add(cp);
        var label = string.IsNullOrEmpty(cp.Note) ? "" : $" ({cp.Note})";
        if (s.State == null)
        {
            cp.Differences.Add("no state recorded for this checkpoint");
            Say($"\n  CHECK {cp.Index} SKIPPED{label}: no recorded state (older recording). EXPECTED: {s.Note}\n");
            _win.Show("Check " + cp.Index, "No recorded state. Expected: " + s.Note, false);
            return;
        }
        cp.Actual = DrawingState.Read(_doc);
        if (cp.Actual == null) { cp.Pass = false; cp.Differences.Add("could not read the drawing"); }
        else { cp.Differences = DrawingDigest.Compare(s.State, cp.Actual); cp.Pass = cp.Differences.Count == 0; }
        var line = cp.Pass == true ? $"CHECK {cp.Index} PASS" : $"CHECK {cp.Index} FAIL: {string.Join("; ", cp.Differences)}";
        Say($"\n  {line}{label}\n");
        _win.Show("Check " + cp.Index + (cp.Pass == true ? " PASS" : " FAIL"), (cp.Note.Length > 0 ? cp.Note + "\n" : "") + string.Join("\n", cp.Differences), false);
    }

    void RunStep(Step s)
    {
        if (s.Kind == "ribbon" && s.Answers.Count == 0 && !s.HadDialog && TryClick(s)) { _pendingStart = true; _sentAt = DateTime.Now; return; }
        // interactive, and this step's command opens a dialog: hold back the answers given after the dialog opened
        var dialog = _interactive && _i + 1 < _steps.Count && _steps[_i + 1].Kind == "dialog" ? _steps[_i + 1] : null;
        string? text;
        if (dialog != null) (text, _deferred) = Plan.InputAroundDialog(s, dialog.T);
        else { text = Plan.InputText(s); _deferred = null; }
        if (string.IsNullOrEmpty(text)) { Say("\n  (nothing replayable in this step)\n"); return; }
        if (s.Kind == "ribbon") ActivateTab(s.Tab);
        // SendStringToExecute: queued, runs as typed input when AutoCAD is next quiescent; "\n" is Enter
        _doc.SendStringToExecute(text, true, false, true);
        _pendingStart = true; _sentAt = DateTime.Now;
    }

    /// <summary>Invoke the live RibbonItem the way a click does: its CommandHandler.Execute(item).</summary>
    bool TryClick(Step s)
    {
        try
        {
            var ribbon = ComponentManager.Ribbon;
            if (ribbon == null || string.IsNullOrEmpty(s.ItemId)) return false;
            var item = ribbon.FindItem(s.ItemId, false, true);
            if (item is not RibbonCommandItem ci || !ci.IsEnabled) return false;
            var handler = ci.CommandHandler;
            if (handler == null || !handler.CanExecute(ci)) return false;
            ActivateTab(s.Tab);
            handler.Execute(ci);
            return true;
        }
        catch (Exception ex) { Log.Error("TryClick", ex); return false; }
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
