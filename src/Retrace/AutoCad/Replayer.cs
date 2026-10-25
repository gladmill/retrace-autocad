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
    readonly Document _doc;
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

    Replayer(List<Step> steps, ReplayMode mode, Document doc, string sessionPath, bool interactive)
    {
        _steps = steps; _mode = mode; _doc = doc; _sessionPath = sessionPath; _interactive = interactive;
        _autoFillOn = interactive && RetraceSettings.Load().AutoFillDialogs;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) =>
        {
            try { Tick(); }
            catch (Exception ex) { Log.Error("Replay tick", ex); Finish(false, true, "exception: " + ex.Message); }
        };
        _win.NextClicked += () => _userNext = true;
        _win.StopClicked += () => Finish(false, false, "stopped by the user");
    }

    /// <summary>Stops a running replay (timer, event handlers, panel). Used when the plugin is shut down.</summary>
    public static void StopCurrent() { var c = Current; if (c == null) return; c._alert = false; c.Finish(false, false, "stopped (core shutdown)"); }

    /// <summary>Starts a replay in the active drawing (call from the RETRACEREPLAY command). <paramref name="initial"/> is the session's
    /// initial drawing state (null for older sessions).</summary>
    public static bool Start(List<Step> steps, ReplayMode mode, string sessionPath, DrawingDigest? initial, out string message, bool alert = false, bool interactive = false)
    {
        if (Current != null) { message = "A replay is already running (close its window or press Stop)."; return false; }
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc == null) { message = "No active drawing."; return false; }
        var r = new Replayer(steps, mode, doc, sessionPath, interactive) { _alert = alert };
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
        _doc.CommandWillStart += OnCmdStart;
        _doc.CommandFailed += OnCmdFailed;
        _doc.UnknownCommand += OnUnknown;
        _doc.CommandEnded += OnCmdEnded;
        _doc.CommandCancelled += OnCmdCancelled;
        _win.Show();
        _win.Show("Retrace replay", "Starting...", false);
        _timer.Start();
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
        _timer.Stop();
        Log.Safe("Replay stop", () =>
        {
            AcApp.EnterModal -= OnEnterModal;
            AcApp.LeaveModal -= OnLeaveModal;
            _doc.CommandWillStart -= OnCmdStart;
            _doc.CommandFailed -= OnCmdFailed;
            _doc.UnknownCommand -= OnUnknown;
            _doc.CommandEnded -= OnCmdEnded;
            _doc.CommandCancelled -= OnCmdCancelled;
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
            var ed = _doc.Editor;
            ed.WriteMessage($"\nRetrace replay ended at step {_result.StepsRun}/{_steps.Count}: {message}\n");
            ed.WriteMessage($"\n{_result.Summary()}\n" + (written != null ? $"Result: {written}\n" : "Result file could not be written (see retrace.log).\n"));
            if (_alert) ShowAlert(written);
        });
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
    void OnCmdStart(object? s, CommandEventArgs e) { if (!OwnCommands.IsOwnCommand(e.GlobalCommandName)) _pendingStart = false; }

    bool Settled()
    {
        if (_modalDepth > 0) return false;
        if (DateTime.Now < _notBefore) return false;
        if (_pendingStart && (DateTime.Now - _sentAt).TotalSeconds < 2) return false; // wait for the command to actually start
        _pendingStart = false;
        var inProgress = _doc.CommandInProgress;
        // our own RETRACEREPLAY command has already returned (the timer runs after it), so anything in progress is replayed work
        return string.IsNullOrEmpty(inProgress) && _doc.Editor.IsQuiescent;
    }

    void Tick()
    {
        if (!_running) return;
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
        if (_dialog.Active) { TickDialog(); return; }
        if (_modalDepth > 0)
        {
            if (_mode == ReplayMode.Auto && !_interactive)
            {
                // a dialog needs a person; give transient ones a moment to close by themselves
                if (_modalSince == DateTime.MinValue) _modalSince = DateTime.Now;
                if ((DateTime.Now - _modalSince).TotalSeconds > 2)
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
        if (!Settled()) return;
        if (_i >= _steps.Count) { Finish(true, false, "finished"); return; }

        var s = _steps[_i];
        if (!_shown)
        {
            _shown = true; _stepShownAt = DateTime.Now; _modalsAtStepStart = _modalsSeen; _userNext = false;
            _win.Show($"Step {_i + 1}/{_steps.Count}", Describe(s), _mode == ReplayMode.Step);
            _doc.Editor.WriteMessage($"\n[replay {_i + 1}/{_steps.Count}] {s.Title}\n");
        }
        if (_mode == ReplayMode.Step && !_userNext) return;

        // a dialog step needs a person; unattended (Auto) the run cannot go on
        if (s.Kind == "dialog" && _mode == ReplayMode.Auto)
        {
            _result.Steps.Add(new StepResult { Index = _i + 1, Kind = "dialog", Title = s.Title, Status = "needs-person" });
            Finish(false, false, $"needs a person: dialog {(string.IsNullOrWhiteSpace(s.DialogTitle) ? "(untitled)" : s.DialogTitle)}");
            return;
        }

        Execute(s);
        _result.Steps.Add(new StepResult { Index = _i + 1, Kind = s.Kind, Title = s.Title });
        _i++;
        _shown = false;
        _notBefore = DateTime.Now.AddMilliseconds(400);
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
                        if (!_fillAnnounced) { _fillAnnounced = true; _doc.Editor.WriteMessage("\n" + what + "\n"); }
                        _win.Show($"Step {_i + 1}/{_steps.Count}: filling the dialog", what, false);
                        return;
                    }
                    Log.Info($"autofill gave up on \"{step.DialogTitle}\": {_fill.Note ?? string.Join("; ", _fill.Results.Where(r => r.Status != FieldFillResult.Applied).Select(r => r.Label + " " + r.Status + (r.Message != null ? " (" + r.Message + ")" : "")))}");
                }
                var msg = DialogMessage(step);
                if (!_announced) { _announced = true; _doc.Editor.WriteMessage("\n" + msg + "\n"); }
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
        if (_modalDepth > 0 || !Settled()) return;   // Done: wait until the command that owned the dialog has finished (a cancel arrives as an event first)
        var failed = d.Phase == DialogWaitPhase.Failed;
        var auto = !failed && _fill?.State == DialogAutoFiller.Phase.Clicked;   // the replay filled it and closed it: nobody waited
        var first = true;
        while (_i < _steps.Count && _steps[_i].Kind == "dialog")
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
        if (failed) _doc.Editor.WriteMessage($"\nDialog step failed: {d.FailReason}. Continuing with the next steps.\n");
        d.Reset(); _doneAt = DateTime.MinValue;
        _fill = null; _fillTried = false; _fillAnnounced = false;
        _announced = false; _cancelSent = false; _shown = false;
        _notBefore = DateTime.Now.AddMilliseconds(400);
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
        return new DialogAutoFiller(mode, step.Values, step.DialogTitle, step.ValuesSource, main);
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

    void Execute(Step s)
    {
        switch (s.Kind)
        {
            case "tab":
                ActivateTab(s.Tab);
                break;
            case "mark":
                Checkpoint(s);
                break;
            case "dialog":
                _doc.Editor.WriteMessage($"\n  Dialog \"{s.DialogTitle}\": fill it in by hand.\n");
                break;
            case "ribbon" or "command" or "lisp":
                RunStep(s);
                break;
        }
    }

    /// <summary>Compares the drawing with the digest recorded at this checkpoint.</summary>
    void Checkpoint(Step s)
    {
        var ed = _doc.Editor;
        var cp = new CheckpointResult { Index = ++_checks, Note = s.Note ?? "", Expected = s.State };
        _result.Checkpoints.Add(cp);
        var label = string.IsNullOrEmpty(cp.Note) ? "" : $" ({cp.Note})";
        if (s.State == null)
        {
            cp.Differences.Add("no state recorded for this checkpoint");
            ed.WriteMessage($"\n  CHECK {cp.Index} SKIPPED{label}: no recorded state (older recording). EXPECTED: {s.Note}\n");
            _win.Show("Check " + cp.Index, "No recorded state. Expected: " + s.Note, false);
            return;
        }
        cp.Actual = DrawingState.Read(_doc);
        if (cp.Actual == null) { cp.Pass = false; cp.Differences.Add("could not read the drawing"); }
        else { cp.Differences = DrawingDigest.Compare(s.State, cp.Actual); cp.Pass = cp.Differences.Count == 0; }
        var line = cp.Pass == true ? $"CHECK {cp.Index} PASS" : $"CHECK {cp.Index} FAIL: {string.Join("; ", cp.Differences)}";
        ed.WriteMessage($"\n  {line}{label}\n");
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
        if (string.IsNullOrEmpty(text)) { _doc.Editor.WriteMessage("\n  (nothing replayable in this step)\n"); return; }
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

    static void ActivateTab(string? title)
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
