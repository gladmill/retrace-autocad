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
    bool _beganRun;   // BeginRun was called: Finish calls EndRun
    readonly bool _sdi;   // the document mode, read once per run (by the caller of Start)
    IntPtr _hookedDb;   // the drawing the document held when it was hooked (SDI replaces it inside the same object)
    string? _eventError;            // set by Document events (a plain field write; no AutoCAD calls in handlers)
    readonly List<SdiDocs.SdiSend> _sends = new();   // this replay's own sends under SDI: Settled waits only for these
    int _lastActedIndex = -1;   // the step that last acted on AutoCAD (sent text, clicked, opened, filled a dialog, drove a palette): the one a drawing opened in between is blamed on
    void SendTracked(string text) { _lastActedIndex = _i; _sends.Add(SdiDocs.Send(_doc, text, true, false, true, SendFailed, _sdi, MarkDelivered)); }
    void MarkDelivered() => _sentAt = DateTime.Now;   // SDI: the text is typed a moment after the call; the 2 s wait for the command to start counts from then
    void SendFailed(string why) => _eventError ??= why;   // SDI: typed text that could not be delivered fails the step (the next tick ends the run)
    readonly UnknownCommandGrace _unknown = new();   // UnknownCommand for a command the replay sent: waits for its start before it is an error (fed by the handlers and NoteSent, read in Tick)
    bool _unknownWaiting;           // the last poll of _unknown said Waiting: no next step yet
    DateTime _modalSince = DateTime.MinValue;
    int _checks;
    int _i;
    bool _running, _userNext;
    // the current palette step: retried since _paletteSince; waiting for a person (Next); its button's command still to start
    bool _paletteForPerson;
    PaletteFill.FillRun _paletteFill = new();   // what the current palette step has set so far (tabs)
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
    readonly ModalState _modal = new();   // which modal states are dialogs (a dialog window showed) and which are a command's hidden interaction (Core/ModalWindows)
    bool _dlgWindowNow;                  // a dialog window of this process is open (looked at once per tick, while a modal state is open)
    int _modalsCountedAtStepStart;
    ChainDelivery? _clickDelivery; Step? _clickDeliveryStep; bool _clickDeliveryPausing; string? _clickUnsafe;   // a chain click with no dialog step after it: its answers are typed as the script asks
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
    // A script-chain click (Step.ChainClick) whose script asked for input a person gave while recording: each dialog of the chain is filled in turn, and where that input was given the replay
    // types it (ChainDelivery): in order, each answer when the command waits at a prompt of the same kind and message (PromptWatch, fed by the editor's Prompting / Prompted events).
    // What cannot be typed safely, or a prompt that does not come within 20 s, stops for a person (ReplayRules.ChainPauseMessage): they give it and press Next step.
    readonly PromptWatch _watch = new();
    readonly List<Action> _promptUnhook = new();
    DateTime _modalEnteredAt = DateTime.MinValue;   // when a modal dialog was entered last: what the command asked before is not what its answers wait for
    ChainDelivery? _delivery; int _deliveryFor = -1; DateTime _deliveryNoted = DateTime.MinValue;
    readonly HashSet<int> _netSkip = new();   // steps after a chain's dialogs that its script already ran (ReplayRules.LaterScriptCommands)
    Step? _chainStep;
    int _chainPausedFor = -1, _chainWaitFor = -1;   // dialog step index (0-based): paused for a person after it, waiting at it
    bool _chainPausing;
    DateTime _scriptBusySince = DateTime.MinValue;
    int _runBits;     // CMDACTIVE script / LISP bits already set when the replay started (a replay run under a script or LISP wrapper): never waited on
    int _clickBits;   // the same when the last click was sent: only a script or LISP that started after it counts as the click's
    DateTime _chainWaitSince; bool _chainWaitSaid;
    string? _deferred;   // answers recorded after the dialog opened: typed once the person confirmed it (see Plan.InputAroundDialog)
    // Tdoc run (RETRACETDOC): pictures for the training document; null for every other replay, and then none of the hooks does anything
    TdocCapture? _tdoc;
    SuiteHook? _suite;   // suite run (RETRACESUITE): step timeout, no waiting for a person; null for every other replay
    StepClock? _clock;   // the suite's step timeout: which step runs and since when
    /// <summary>Nobody is there to help: RETRACEREPLAY, and a suite run (which fills dialogs itself but never waits for a person).</summary>
    bool Unattended => !_interactive || _suite != null;
    Action<ReplayResult>? _finished;   // called at the end of Finish, after the result file is written

    Replayer(List<Step> steps, ReplayMode mode, Document doc, string sessionPath, bool interactive, bool single)
    {
        _sdi = single;
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
        TdocCapture? tdoc = null, Action<ReplayResult>? finished = null, SuiteHook? suite = null, bool? single = null)
    {
        if (Current != null) { message = "A replay is already running (close its window or press Stop)."; return false; }
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc == null) { message = "No active drawing."; return false; }
        var r = new Replayer(steps, mode, doc, sessionPath, interactive, single ?? SdiDocs.IsSingleNow()) { _alert = alert, _tdoc = tdoc, _finished = finished, _suite = suite };
        Current = r;
        r.Begin(initial);
        message = $"Replaying {steps.Count} steps ({mode}).";
        return true;
    }

    void Begin(DrawingDigest? initial)
    {
        PaletteFill.Reset();   // no menu state of an earlier replay
        if (_sdi && SdiGuard.IsOwnTestDrawing(_doc.Name)) { _afterOpen = true; _quietSince = DateTime.MinValue; }   // a drawing just opened is waited for until it has been quiet for a second
        _result.Session = TestFolders.NameOf(_sessionPath);
        _testName = TestList.NameOf(_result.Session);
        _result.StartedUtc = DateTime.UtcNow.ToString("o");
        _result.CoreVersion = typeof(Replayer).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "unknown";
        _result.StepsTotal = _steps.Count;
        _result.Interactive = _interactive;
        // a test folder keeps its history; only results/latest.json is replaced, when this run ends
        CompareStart(initial);
        _running = true; _runBits = Ask.ScriptOrLispBits();   // set when the replay started: a replay run under a script or LISP wrapper has them for the whole run
        if (_sdi)
        {
            // an own test drawing that already holds unsaved changes holds a person's (from an earlier run): it stops counting as Retrace's own
            var modified = SdiDocs.IsModified(_doc);
            string? beginName; try { beginName = _doc.Name; } catch (Exception) { beginName = null; }
            SdiGuard.Own.BeginRun(beginName, modified); _beganRun = true;   // the unsaved changes in the test's drawing are the run's until it ends (Finish: EndRunLater)
        }
        AcApp.EnterModal += OnEnterModal;
        AcApp.LeaveModal += OnLeaveModal;
        Hook(_doc);
        AcApp.DocumentManager.DocumentToBeDestroyed += OnDocGone;
        AcApp.DocumentManager.DocumentCreated += OnDocCreated;
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

    bool _hooked;   // the events are on _doc now (Hook sets it, Unhook clears it)

    void Hook(Document d)
    {
        _hooked = true;
        _hookedDb = SdiDocs.DbId(d);
        d.CommandWillStart += OnCmdStart;
        d.CommandFailed += OnCmdFailed;
        d.UnknownCommand += OnUnknown;
        d.CommandEnded += OnCmdEnded;
        d.CommandCancelled += OnCmdCancelled;
        d.LispWillStart += OnLispStart;
        d.LispEnded += OnLispEnd;
        d.LispCancelled += OnLispEnd;
        HookPrompts(d.Editor);
    }

    /// <summary>Tells <see cref="_watch"/> which prompt the command asked for last: the kind and message of every Prompting event. (Only the message: reading more of the options made AutoCAD reject picks.
    /// No Prompted event: a jig raises one for its first sample at once and then waits, so a result does not say that the prompt is answered.)</summary>
    void HookPrompts(Autodesk.AutoCAD.EditorInput.Editor ed)
    {
        foreach (var u in _promptUnhook) { try { u(); } catch { } }
        _promptUnhook.Clear();
        Autodesk.AutoCAD.EditorInput.PromptPointOptionsEventHandler pt = (s, e) => _watch.Prompting("point", e.Options?.Message, DateTime.Now); ed.PromptingForPoint += pt; _promptUnhook.Add(() => ed.PromptingForPoint -= pt);
        Autodesk.AutoCAD.EditorInput.PromptPointOptionsEventHandler co = (s, e) => _watch.Prompting("corner", e.Options?.Message, DateTime.Now); ed.PromptingForCorner += co; _promptUnhook.Add(() => ed.PromptingForCorner -= co);
        Autodesk.AutoCAD.EditorInput.PromptDistanceOptionsEventHandler di = (s, e) => _watch.Prompting("distance", e.Options?.Message, DateTime.Now); ed.PromptingForDistance += di; _promptUnhook.Add(() => ed.PromptingForDistance -= di);
        Autodesk.AutoCAD.EditorInput.PromptAngleOptionsEventHandler an = (s, e) => _watch.Prompting("angle", e.Options?.Message, DateTime.Now); ed.PromptingForAngle += an; _promptUnhook.Add(() => ed.PromptingForAngle -= an);
        Autodesk.AutoCAD.EditorInput.PromptDoubleOptionsEventHandler db = (s, e) => _watch.Prompting("double", e.Options?.Message, DateTime.Now); ed.PromptingForDouble += db; _promptUnhook.Add(() => ed.PromptingForDouble -= db);
        Autodesk.AutoCAD.EditorInput.PromptIntegerOptionsEventHandler it = (s, e) => _watch.Prompting("integer", e.Options?.Message, DateTime.Now); ed.PromptingForInteger += it; _promptUnhook.Add(() => ed.PromptingForInteger -= it);
        Autodesk.AutoCAD.EditorInput.PromptStringOptionsEventHandler st = (s, e) => _watch.Prompting("string", e.Options?.Message, DateTime.Now); ed.PromptingForString += st; _promptUnhook.Add(() => ed.PromptingForString -= st);
        Autodesk.AutoCAD.EditorInput.PromptKeywordOptionsEventHandler kw = (s, e) => _watch.Prompting("keyword", e.Options?.Message, DateTime.Now); ed.PromptingForKeyword += kw; _promptUnhook.Add(() => ed.PromptingForKeyword -= kw);
    }

    void Unhook(Document d)
    {
        _hooked = false;
        d.CommandWillStart -= OnCmdStart;
        d.CommandFailed -= OnCmdFailed;
        d.UnknownCommand -= OnUnknown;
        d.CommandEnded -= OnCmdEnded;
        d.CommandCancelled -= OnCmdCancelled;
        d.LispWillStart -= OnLispStart;
        d.LispEnded -= OnLispEnd;
        d.LispCancelled -= OnLispEnd;
        foreach (var u in _promptUnhook) { try { u(); } catch { } }
        _promptUnhook.Clear();
    }

    void OnLispStart(object? s, LispWillStartEventArgs e) { _lispRunning++; if (_clicked is { ChainClick: true }) { _pendingStart = false; _clicked = null; } }   // a LISP started: the click did something (its LISP may run for a while, and runs commands later)

    /// <summary>The drawing being replayed in is closing: AutoCAD Electrical closes an untouched new drawing when it opens a project drawing or
    /// switches projects. Unhooked now; the next tick goes on in the active drawing (<see cref="_docGone"/>).</summary>
    void OnDocGone(object? s, DocumentCollectionEventArgs e)
    {
        if (!ReferenceEquals(e.Document, _doc)) return;
        Log.Safe("Replay doc gone", () => Unhook(_doc));
        _docGone = true;
    }
    /// <summary>SDI: opening a drawing (a typed OPEN or NEW, RECOVER, a palette click, another plugin) replaces the drawing in the Document object the replay uses:
    /// DocumentToBeDestroyed, then DocumentCreated for the same object. The handler only notes it (a plain field write); <see cref="TickRecreated"/> does the work. With
    /// SDI = 0 a new drawing is a new object and this does nothing.</summary>
    void OnDocCreated(object? s, DocumentCollectionEventArgs e) { if (_running && _sdi) _docRecreated = true; }   // any drawing: RECOVER, for one, comes in a Document object of its own
    bool _docRecreated;
    string? _expectedOpen;   // the file Retrace's own open step is opening (SDI): the only open it expects

    /// <summary>SDI: may the replay go on in <paramref name="doc"/>, a drawing that has just replaced the one it ran in? A drawing that was open before the suite ends the test as
    /// ERROR; one that is neither the file Retrace's own open step opened nor one of the test's own copies stops the run for a person (the replay must not change the person's file).
    /// False when the run ended. The expected open is used up.</summary>
    bool SdiSwitchAllowed(Document doc)
    {
        string name; try { name = doc.Name; } catch (Exception) { name = ""; }
        var expected = _expectedOpen; _expectedOpen = null;
        if (_suite != null && _suite.IsProtected(doc)) { Finish(false, true, $"the test switched to {Path.GetFileName(name)}, which was open before the suite (or is the suite's own start drawing): not touched; close it and re-run"); return false; }
        if (!(expected != null && SdiGuard.Own.SamePath(name, expected)) && !SdiGuard.IsOwnTestDrawing(name))
        {
            var why = $"a step opened {Path.GetFileName(name)}; under SDI Retrace stops here so it does not change your file. Use SDI = 0 for this test.";
            // the drawing opened while the step that last sent text ran, which may be an earlier one than the step the replay is at now
            var idx = _lastActedIndex >= 0 && _lastActedIndex < _steps.Count ? _lastActedIndex : Math.Min(_i, Math.Max(0, _steps.Count - 1));
            StopForPerson("open", idx < _steps.Count ? _steps[idx].Title : "", why, step: idx);
            return false;
        }
        return true;
    }

    /// <summary>The tick after a drawing was opened in the replay's document (SDI). Hooks the document again; a drawing that was open before the suite ends the test as ERROR (as
    /// with SDI = 0); any drawing that is neither the one Retrace's own open step opened nor one of the test's own copies stops the run for a person, so that the replay does not go on
    /// in, and change, the person's file (RECOVER, XOPEN, vla-open, AutoCAD Electrical...); else the replay waits for the drawing to be quiet, as <see cref="Follow"/> does. False when the run ended.</summary>
    bool TickRecreated()
    {
        // the drawing that is open now: the same Document object with another drawing in it, or (RECOVER, XOPEN) a new one
        var doc = AcApp.DocumentManager.MdiActiveDocument ?? _doc;
        if (!SdiSwitchAllowed(doc)) return false;
        string name; try { name = doc.Name; } catch (Exception) { name = ""; }
        if (!ReferenceEquals(doc, _doc)) { Log.Safe("Replay unhook", () => Unhook(_doc)); _doc = doc; Hook(_doc); }
        else Log.Safe("Replay doc re-created", RehookIfReplaced);
        if (_docGone) { _docGone = false; Log.Info($"replay continues in {Path.GetFileName(name)}: SDI replaced the drawing in the document the replay ran in"); }
        _lispRunning = 0; _afterOpen = true; _quietSince = DateTime.MinValue;
        _notBefore = DateTime.Now.AddMilliseconds(500);
        return true;
    }
    /// <summary>The events of the document die with the drawing in it (SDI): hooks it again when the drawing is not the one it was hooked for.</summary>
    void RehookIfReplaced()
    {
        if (!SdiDocs.NeedsRehook(_hooked, SdiDocs.DbId(_doc), _hookedDb)) return;   // (not hooked now, e.g. after OnDocGone: always)
        Unhook(_doc); Hook(_doc);
    }
    void OnLispEnd(object? s, EventArgs e) { if (_lispRunning > 0) _lispRunning--; }

    /// <summary>An "open" step: opens the recorded drawing and replays the rest in it, the way the person went on in the drawing they
    /// opened. Already open and unchanged, it switches to it; open with unsaved changes it stops (they are not what the file holds, and
    /// a test never discards someone's changes). False when it could not: the run has finished with the reason.</summary>
    bool OpenDrawing(Step s)
    {
        _lastActedIndex = _i;
        var path = s.Drawing ?? "";
        var dm = AcApp.DocumentManager;
        try
        {
            var doc = OpenDocument(path);
            var ownCopy = false;
            // suite run: a drawing the person had open is never replayed into (or saved, or closed): the test gets a copy of the file instead,
            // one copy per file for the whole test (a second open of the same file goes back to it)
            if (SdiGuard.OpenStepNeedsCopy(_sdi, path))
            {
                // SDI, every mode: opening the file would replace the drawing that is open and leave the person's file changed for the next open: a temp copy, one per file
                if (!_copies.TryGetValue(path, out var sdiCopy))
                {
                    if (!File.Exists(path)) { Finish(false, true, $"drawing not found: {path}"); return false; }
                    sdiCopy = _suite != null ? _suite.CopyOf(path) : SdiDocs.TempCopyOf(path); _copies[path] = sdiCopy;
                    Say($"\n  SDI is on: the test opens a copy of {Path.GetFileName(path)}.\n");
                }
                path = sdiCopy; doc = OpenDocument(path); ownCopy = true;
            }
            else if (doc != null && _suite != null && _suite.IsProtected(doc))
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
                // SDI: the drawing that is open is replaced: never one with a person's unsaved changes; the test's own earlier drawing is saved first (it is a temp file)
                if (SdiDocs.PrepareForOpen(_sdi) is { } refusal)
                {
                    // e.g. RETRACEREPLAY: the replay changed the person's drawing, and the open would replace it. Not a failure of the test: it stops for a person
                    StopForPerson("open", s.Title, refusal, $"needs a person: the step \"{s.Title}\" opens a drawing, and with SDI on that replaces the open one. {refusal}");
                    return false;
                }
                if (_sdi) _expectedOpen = path;
                doc = SdiDocs.Open(path, _sdi);   // (SDI: through COM, see SdiDocs)
                if (_sdi && !SdiGuard.Own.SamePath(doc.Name, path))
                {
                    StopForPerson("open", s.Title, $"the drawing did not open: {Path.GetFileName(path)} was to be opened, {Path.GetFileName(doc.Name)} is open.");
                    return false;
                }
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
        // SDI: the drawing just opened is in the same Document object as the one replayed in, whose events died with the old drawing: hook it again
        var sameObject = ReferenceEquals(doc, _doc);
        if (!sameObject) { Log.Safe("Replay unhook", () => Unhook(_doc)); _doc = doc; Hook(_doc); }
        else if (_sdi) Log.Safe("Replay rehook", RehookIfReplaced);   // (the DocumentCreated handler did it already: then nothing is done twice)
        if (_suite != null || (sameObject && _sdi)) _docGone = false;
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

    Document? OpenDocument(string path) =>
        AcApp.DocumentManager.Cast<Document>().FirstOrDefault(d => d.IsNamedDrawing && (string.Equals(d.Name, path, StringComparison.OrdinalIgnoreCase) || (_sdi && SdiGuard.Own.SamePath(d.Name, path))));   // (SDI: canonical long paths, 8.3 or long form)

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
        PaletteFill.Abandon();   // never leave a context menu open when the replay ends
        _fill?.Abandon();
        if (!_running) return;
        _running = false;
        _stoppedForPerson = needsPerson;
        CursorBack();
        _timer.Stop();
        Log.Safe("Replay restore view", () => HandOffView(cancel: _userStopped || errored));
        Log.Safe("Replay SDI end", () =>
        {
            if (ViewRestorer.IsShutdown) return;   // the plug-in is unloading: no COM save, no timer
            foreach (var t in _sends) t.Cancel();   // text this replay typed but that is not typed yet must not be typed after it ended
            _sends.Clear();
            if (!_beganRun) return;
            _beganRun = false;
            SdiDocs.EndRunLater(_doc, _suite != null || _tdoc != null);   // saves the test's own drawing once nothing runs in it; a failed save disowns a RETRACERUN's drawing
            if (_suite == null) SdiDocs.NoticeTempDrawing(_doc);
        });
        Log.Safe("Replay stop", () =>
        {
            AcApp.EnterModal -= OnEnterModal;
            AcApp.LeaveModal -= OnLeaveModal;
            AcApp.DocumentManager.DocumentToBeDestroyed -= OnDocGone;
            AcApp.DocumentManager.DocumentCreated -= OnDocCreated;
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
            if (_alert) { SdiDocs.VerdictBoxOpen = true; try { ShowAlert(written); } finally { SdiDocs.VerdictBoxOpen = false; } }   // (SDI: the end-of-run save may happen with Retrace's own box open)
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
    // A modal state feeds the dialog wait only once a dialog window showed in it (Tick: LookAtModalWindows) or, in a recording from before windowless states were marked, it was over before a window could be told apart (ModalState.QuickMs): a command's hidden modal state is no dialog.
    void OnEnterModal(object? s, EventArgs e) { _modalDepth++; _modal.Entered(DateTime.Now, _modal.Depth == 0 ? Native.VisibleHandles() : null); _modalEnteredAt = DateTime.Now; _modalsSeen++; Log.Info($"replay: modal entered, depth {_modalDepth}"); }
    void OnLeaveModal(object? s, EventArgs e)
    {
        if (_modalDepth > 0) _modalDepth--;
        // a recording from before the recorder told windowless states apart: a short modal state may have been the dialog, over between two ticks
        var quick = _steps.Skip(Math.Max(0, _i)).FirstOrDefault(x => x.Kind == "dialog") is { WindowMarked: false };
        var (countedNow, wasDialog) = _modal.Left(DateTime.Now, quick);
        if (countedNow) _dialog.ModalEntered();
        if (wasDialog || _modalDepth > 0) _dialog.ModalLeft(_modalDepth);
        Log.Info($"replay: modal left, depth {_modalDepth}{(wasDialog || _modalDepth > 0 ? "" : " (no dialog window showed in it)")}");
    }

    /// <summary>Once per tick while a modal state is open: is a dialog window of this process open (enumerated, never from the foreground window), and the first time one is, the state counts as a dialog.</summary>
    void LookAtModalWindows()
    {
        _dlgWindowNow = false;
        if (_modalDepth <= 0) return;
        var main = AcApp.MainWindow.Handle;
        // a new window (not one that was open when the modal state started: a floating palette is no dialog), or the window in front when it is really shown: the safe side is the old wait
        _dlgWindowNow = Native.ShownForegroundOfThisProcess(main, _win.Handle) != IntPtr.Zero || Native.DialogWindow(main, _win.Handle, _modal.Before) != IntPtr.Zero;
        if (_modal.Observe(_dlgWindowNow, DateTime.Now)) _dialog.ModalEntered();
    }
    void OnCmdEnded(object? s, CommandEventArgs e) { if (!OwnCommands.IsOwnCommand(e.GlobalCommandName)) _dialog.CommandEnded(false, DateTime.Now); }
    void OnCmdCancelled(object? s, CommandEventArgs e) { if (!OwnCommands.IsOwnCommand(e.GlobalCommandName)) _dialog.CommandEnded(true, DateTime.Now); }
    void OnCmdFailed(object? s, CommandEventArgs e) { if (!OwnCommands.IsOwnCommand(e.GlobalCommandName)) _eventError ??= $"command {e.GlobalCommandName} failed"; }
    void OnUnknown(object? s, UnknownCommandEventArgs e) => _unknown.Unknown(e.GlobalCommandName, DateTime.Now);

    /// <summary>The run stops for a person: <paramref name="why"/> goes on the step's result line and, after "needs a person: ", is the run's message.</summary>
    void StopForPerson(string kind, string title, string why, string? message = null, int? step = null, List<SegmentResult>? segments = null)
    {
        var index = (step ?? _i) + 1;
        var row = _result.Steps.LastOrDefault(x => x.Index == index);   // a step that was sent has its row already: that row says it, a second one for the same step would contradict it
        if (row != null) { row.Status = "needs-person"; row.Message = why; if (segments != null) row.Segments = segments; }
        else _result.Steps.Add(new StepResult { Index = index, Kind = kind, Title = title, Status = "needs-person", Message = why, Segments = segments });
        Finish(false, false, message ?? "needs a person: " + why, needsPerson: true);
    }

    /// <summary>The replay sends <paramref name="command"/> (typed, clicked, or started by a palette click; never something a person typed): AutoCAD may report it
    /// as unknown a moment before it runs it (<see cref="UnknownCommandGrace"/>). Null for a step with no command (a LISP call). Answers typed into a running
    /// command (the deferred ones after a dialog, a palette command's) start no command and are not noted.</summary>
    void NoteSent(string? command) => _unknown.Sent(command, DateTime.Now);

    /// <summary>AutoCAD reports some commands as unknown a moment before it runs them (UNITS on a fresh start): a command the replay sent gets
    /// <see cref="UnknownCommandGrace.Window"/> to start before that is an ERROR. Any other unknown command is an ERROR at once.</summary>
    void CheckUnknown()
    {
        var p = _unknown.Poll(DateTime.Now);
        _unknownWaiting = p.Verdict == UnknownCommandVerdict.Waiting;
        switch (p.Verdict)
        {
            case UnknownCommandVerdict.Failed: _eventError ??= p.Message; break;
            case UnknownCommandVerdict.Recovered:
                Log.Info($"replay: AutoCAD reported {p.Command} as unknown, then started it {(int)p.Waited.TotalMilliseconds} ms later: carrying on");
                break;
        }
    }

    void OnCmdStart(object? s, CommandEventArgs e)
    {
        if (OwnCommands.IsOwnCommand(e.GlobalCommandName)) return;
        _unknown.Started(e.GlobalCommandName, DateTime.Now);
        _pendingStart = false; _clicked = null;
        _lastStarted = e.GlobalCommandName; _lastStartedAt = DateTime.Now;
        // a command without prompts can start and end between two ticks: remember that the palette button's command started
        if (_paletteCommand != null && string.Equals(e.GlobalCommandName, _paletteCommand.Command, StringComparison.OrdinalIgnoreCase)) _paletteCommandStarted = true;
    }

    bool Settled()
    {
        _sends.RemoveAll(x => !x.Pending);
        if (_sends.Count > 0) return false;   // SDI: text this replay typed is not delivered yet (_pendingStart stays armed)
        if (_modalDepth > 0 && !ModalWithoutWindow()) return false;   // a command's hidden modal state is not a dialog: its command still running keeps the step from starting
        if (DateTime.Now < _notBefore) return false;
        if (_pendingStart && (DateTime.Now - _sentAt).TotalSeconds < 2) return false; // wait for the command to actually start
        if (_pendingStart && _clicked is { } clicked) { _clicked = null; if (TypeAfterSilentClick(clicked)) return false; }
        _pendingStart = false;
        // AutoCAD says a script (bit 4) or AutoLISP (bit 32) is active: a script that waits longer than any gap between its commands is still running, so the next step is not typed in between
        // (a replay started from a script itself would never end: after 60 s the bits are ignored)
        if (ReplayRules.NewScriptBits(Ask.ActiveBits(), _runBits) != 0)
        {
            if (_scriptBusySince == DateTime.MinValue) _scriptBusySince = DateTime.Now;
            if ((DateTime.Now - _scriptBusySince).TotalSeconds < 60) return false;
        }
        else _scriptBusySince = DateTime.MinValue;
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
        LookAtModalWindows();
        if (_docRecreated) { _docRecreated = false; if (!TickRecreated()) return; }   // SDI, before anything that reads the document
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
            if (_sdi && !SdiSwitchAllowed(active)) return;   // another drawing replaced the one the replay ran in, outside Retrace's own open step
            _docGone = false;
            _doc = active; Hook(_doc);
            Log.Info($"replay continues in {active.Name}: the drawing it ran in was closed");
        }
        CheckUnknown();
        if (_eventError != null) { Finish(false, true, _eventError); return; }
        // AutoCAD's security prompt (an untrusted dll or LISP file is being loaded) is a decision for a person, never a test step:
        // stop at once with what to do, in both modes, and leave the prompt open for the person to answer.
        if (Native.OpenSecurityPrompt() is { } prompt) { StopAtSecurityPrompt(prompt); return; }
        if (_suite != null && CheckStepTimeout()) return;
        // interactive: a dialog opened while the next step is not a dialog step (an older recording planned a click's script chain as steps): skip the steps the open dialog's chain ran
        if (_interactive && !_dialog.Active && _modalDepth > 0 && _i > 0 && _i < _steps.Count && _steps[_i].Kind != "dialog" && _steps[_i - 1].Kind == "ribbon"   // cheap checks first: no Win32 call otherwise
            && Native.ForegroundOfThisProcess() is var openDlg && openDlg != IntPtr.Zero && openDlg != AcApp.MainWindow.Handle && openDlg != _win.Handle
            && ReplayRules.DialogAfterChain(_steps, _i, Native.TitleOf(openDlg), Native.ClassOf(openDlg), Native.TitleOf(AcApp.MainWindow.Handle)) is { } dialogAt)
        {
            Log.Info($"replay: dialog \"{Native.TitleOf(openDlg)}\" is open while step {_i + 1} is next; steps {_i + 1}-{dialogAt} were run by the click's script chain: skipping to the dialog");
            _warnings.Add($"steps {_i + 1}-{dialogAt}: the click's LISP and script already ran them (the dialog is open): not typed again");
            var tail = ReplayRules.LaterScriptCommands(_steps, dialogAt);
            if (tail.Unsafe != null)
            {
                StopForPerson(_steps[_i].Kind, _steps[_i].Title, tail.Unsafe);
                return;
            }
            foreach (var later in tail.Skip) _netSkip.Add(later);
            if (tail.Skip.Count > 0) _warnings.Add($"steps {string.Join(", ", tail.Skip.Select(x => x + 1))}: typed by the click's script after the dialog: not typed again");
            for (; _i < dialogAt; _i++) _result.Steps.Add(new StepResult { Index = _i + 1, Kind = _steps[_i].Kind, Title = _steps[_i].Title, Message = "run by the click's script chain" });
            _shown = false;
        }
        // interactive: the step that opens a dialog was sent; wait for the dialog and the person
        if (_interactive && !_dialog.Active && _i < _steps.Count && _steps[_i].Kind == "dialog") 
        {
            _clock?.Begin(_i + 1, DateTime.Now);
            _dialog.Begin(DateTime.Now, _modalDepth > 0 && _modal.WindowSeen, _steps[_i].ClosedWith == "cancel");   // a modal state with no dialog window in it is not the dialog
            _announced = false; _cancelSent = false;
            _fill?.Cancel(); _fill = null; _fillTried = false; _fillAnnounced = false;
            // the dialog came and went between two ticks: count it as handled
            if (_modalDepth == 0 && _modal.Counted > _modalsCountedAtStepStart) { _dialog.ModalEntered(); _dialog.ModalLeft(0); }
        }
        if (DateTime.Now >= _cursorBackAt) CursorBack();
        if (_modalDepth > 0 && Native.ForegroundOfThisProcess() is var dlg && dlg != IntPtr.Zero && dlg != AcApp.MainWindow.Handle && dlg != _win.Handle)
        {
            var d = PaletteReveal.RectOf(dlg);
            _win.Avoid(d.Left, d.Top, d.Right, d.Bottom);   // keep the replay window off the dialog being filled
        }
        if (PaletteFill.TakePressFailure() is { } pressFailed) { Finish(false, true, pressFailed); return; }
        if (_paletteCommand != null) { TickPaletteCommand(); return; }
        bool dialogWindowOpen = _modalDepth > 0 && !ModalWithoutWindow();
        // the answers a person gave between the click and the chain's first dialog are the click's: typed as the script asks, whether a dialog step follows or not
        if ((_clickDelivery != null || _clickDeliveryPausing || _clickUnsafe != null) && !dialogWindowOpen && TickClickDelivery()) return;
        if (_dialog.Active) { TickDialog(); return; }
        if (dialogWindowOpen)
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
        if (_unknownWaiting) return;   // AutoCAD called a command the replay sent unknown; it may still start (CheckUnknown decides)
        if (!Settled()) return;
        RestoreView();   // the step is done: the person's view comes back
        if (_i >= _steps.Count) { Finish(true, false, "finished"); return; }

        if (_netSkip.Remove(_i))
        {
            // a command the click's script typed after its dialog (the safety net for a recording planned the old way): not typed again
            _result.Steps.Add(new StepResult { Index = _i + 1, Kind = _steps[_i].Kind, Title = _steps[_i].Title, Message = "run by the click's script chain" });
            _i++; _shown = false;
            return;
        }
        var s = _steps[_i];
        if (_chainStep != null && s.Kind != "dialog" && !ReferenceEquals(s, _chainStep)) { _chainStep = null; _chainPausedFor = _chainWaitFor = -1; _chainPausing = false; _delivery = null; _deliveryFor = -1; }   // a step that is no part of the chain (a palette step too): the chain is over
        _clock?.Begin(_i + 1, DateTime.Now);   // suite: this step runs from now (it is sent next) until the next one starts
        if (!_shown)
        {
            _shown = true; _stepShownAt = DateTime.Now; _modalsAtStepStart = _modalsSeen; _modalsCountedAtStepStart = _modal.Counted; _userNext = false;
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
        try { SendTracked("\u0003\u0003"); } catch (Exception ex) { Log.Error("suite cancel", ex); }
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
        _lastActedIndex = Math.Min(_i, _steps.Count - 1);   // the dialog fill acts for this step
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
                    StopForPerson("dialog", step.Title, $"the dialog needs a person: {title}", $"needs a person: dialog {title}",
                        segments: _fill is { UsedUia: false } ? _fill.SegmentResults() : null);   // how far the replay got: the values set, the buttons pressed, the dialog that stopped it
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
        // a chain: where the recording has input a person gave (after this dialog opened, until the next one opened), the script the click started asks for it now
        if (d.Phase == DialogWaitPhase.Done && _chainStep != null && (_modalDepth == 0 || ModalWithoutWindow()) && _chainPausedFor != _i)
        {
            var due = ReplayRules.ChainAnswersAfter(_chainStep.Answers, _steps, _i);
            if (_deliveryFor != _i)
            {
                // the first look after this dialog: type what the command asks for next, or say why it cannot be typed
                _deliveryFor = _i; _delivery = null;
                if (due.Count > 0)
                {
                    if (ChainDelivery.Unsafe(due) is { } unsafeWhy) Log.Info("replay: the chain's answers are not typed: " + unsafeWhy);
                    else _delivery = new ChainDelivery(due, DateTime.Now, _modalEnteredAt);
                }
            }
            string? pauseMessage = null;
            if (_delivery != null)
            {
                var tick = _delivery.Tick(DateTime.Now, _watch, !string.IsNullOrEmpty(_doc.CommandInProgress));
                switch (tick.Kind)
                {
                    case DeliveryKind.Type:
                        Log.Info($"replay: the script's command waits at \"{tick.Answer!.Message}\" ({tick.Answer.Kind}): typing {ReplayRules.AnswerText(tick.Answer)}");
                        Say($"\n  typing {ReplayRules.AnswerText(tick.Answer)}\n");
                        SendTracked(tick.Text!);
                        _win.Show($"Step {_i + 1}/{_steps.Count}: typing the script's input", ReplayRules.AnswerText(tick.Answer), false);
                        return;
                    case DeliveryKind.Wait:
                        if ((DateTime.Now - _deliveryNoted).TotalSeconds >= 4) { _deliveryNoted = DateTime.Now; Log.Info($"replay: waiting to type {ReplayRules.AnswerText(tick.Answer!)}; the newest prompt is {(_watch.Last is { } lp ? $"{lp.Kind} \"{lp.Message}\"" : "none")}, command in progress: \"{_doc.CommandInProgress}\""); }
                        _win.Show($"Step {_i + 1}/{_steps.Count}: waiting for the script's prompt", $"the command should ask for {ReplayRules.AnswerText(tick.Answer!)} (waits up to {(int)ChainDelivery.WaitSeconds} s).", false);
                        return;
                    case DeliveryKind.Done:
                        _delivery = null; _chainPausedFor = _i; _notBefore = DateTime.Now.AddMilliseconds(400);
                        break;
                    case DeliveryKind.TimedOut:
                        pauseMessage = _delivery.TimedOutMessage(_chainStep);
                        _delivery = null;
                        break;
                }
            }
            else if (due.Count > 0) pauseMessage = ReplayRules.ChainPauseMessage(_chainStep, due);
            if (due.Count == 0) _chainPausedFor = _i;
            else if (pauseMessage != null)
            {
                _chainPausedFor = _i;
                if (_suite != null)
                {
                    StopForPerson("dialog", _steps[_i].Title, pauseMessage);
                    return;
                }
                Say($"\n  {pauseMessage}\n");
                Log.Info("replay: " + pauseMessage);
                _win.Show($"Step {_i + 1}/{_steps.Count}: input needed", pauseMessage, true);
                _userNext = false; _chainPausing = true;
                return;
            }
        }
        if (_chainPausing)
        {
            if (!_userNext) return;
            _chainPausing = false; _userNext = false;
            _notBefore = DateTime.Now.AddMilliseconds(400);
        }
        if (d.Phase == DialogWaitPhase.Done && _deferred != null && _modalDepth == 0)
        {
            if (_doneAt == DateTime.MinValue) { _doneAt = DateTime.Now; return; }
            if ((DateTime.Now - _doneAt).TotalMilliseconds < 500) return;
            if (!string.IsNullOrEmpty(_doc.CommandInProgress))
            {
                SendTracked(_deferred);
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
            SendTracked("\u0003\u0003");
            _notBefore = DateTime.Now.AddMilliseconds(400);
        }
        // Done, and the next recorded step is a dialog that is open already: the command showed it right after this one closed (a LISP alert
        // after its DCL dialog). This step is done; the next one starts with its dialog open, so it is filled or waited for as its own step.
        // Waiting here for the command to finish would wait for that dialog, which no step handles.
        // Only when the open dialog is the one recorded next (its title): an unexpected warning keeps the old wait.
        bool chained = d.Phase == DialogWaitPhase.Done && _deferred == null && NextDialogOpen();
        if (!chained && ((_modalDepth > 0 && !ModalWithoutWindow()) || !Settled())) { NoteChainWait(); return; }   // Done: wait until the command that owned the dialog has finished (a cancel arrives as an event first)
        // the next dialog opened before the answers recorded after this one could be typed (a person closed this one while the command went on to the next): say so, do not drop them silently
        string? droppedNote = null;
        if (chained && _chainStep != null && _chainPausedFor != _i && ReplayRules.ChainAnswersAfter(_chainStep.Answers, _steps, _i).Count > 0)
        {
            droppedNote = $"input a person gave after \"{_steps[_i].DialogTitle}\" were not typed: \"{_steps[_i + 1].DialogTitle}\" opened first";
            Say($"\n  WARNING: {droppedNote}\n");
            Log.Info("replay: " + droppedNote);
            _warnings.Add($"step {_i + 1}: {droppedNote}");
        }
        var failed = d.Phase == DialogWaitPhase.Failed;
        var auto = !failed && _fill?.Completed == true;   // the replay filled it and closed it: nobody waited
        var first = true;
        while (_i < _steps.Count && _steps[_i].Kind == "dialog" && (first || !chained))
        {
            _result.Steps.Add(new StepResult
            {
                Index = _i + 1, Kind = "dialog", Title = _steps[_i].Title, Status = failed ? "failed" : "ok", WaitedForPerson = d.PersonWaited && !auto, Message = failed ? d.FailReason : first ? droppedNote : null,
                AutoFilled = first && auto,
                Fields = first && _fill is { Mode: AutoMode.Ok } && _fill.Results.Count > 0 ? _fill.Results : null,
                Segments = first && _fill is { UsedUia: false } && (_fill.Completed || _fill.State == DialogAutoFiller.Phase.GaveUp) ? _fill.SegmentResults(personFinished: !failed && !auto) : null,   // a person finished what the replay gave up on: those segments are "person"
            });
            first = false;
            _i++;
        }
        if (_chainStep != null && !IsDialogStep(_i)) { _chainStep = null; _chainPausedFor = _chainWaitFor = -1; _chainPausing = false; _delivery = null; _deliveryFor = -1; }   // the chain's last dialog is done
        if (failed) Say($"\nDialog step failed: {d.FailReason}. Continuing with the next steps.\n");
        d.Reset(); _doneAt = DateTime.MinValue;
        _fill?.Cancel(); _fill = null; _fillTried = false; _fillAnnounced = false;
        _announced = false; _cancelSent = false; _shown = false;
        _notBefore = DateTime.Now.AddMilliseconds(chained ? 0 : 400);
    }

    /// <summary>AutoCAD is in a modal state in which no dialog window showed or is open (<see cref="ModalState.Windowless"/>: none for <see cref="ModalState.GraceMs"/> ms, so a slow dialog is not mistaken for it): a command's hidden
    /// interaction. It is no dialog: nothing waits for a person, and the script's prompts can be answered. Decided from the process's windows, not from which window is in front.</summary>
    bool ModalWithoutWindow() => _modal.Windowless(DateTime.Now, _dlgWindowNow);

    /// <summary>The next recorded step is a dialog and it is the modal in front now (<see cref="DialogChain.IsNext"/>; AutoCAD's own window and the replay window are no dialog).</summary>
    bool NextDialogOpen()
    {
        if (_modalDepth <= 0 || _i + 1 >= _steps.Count) return false;
        var open = Native.ForegroundOfThisProcess();
        return open != IntPtr.Zero && open != AcApp.MainWindow.Handle && open != _win.Handle && DialogChain.IsNext(_modalDepth, _steps[_i + 1], Native.TitleOf(open));
    }

    /// <summary>"A dialog opens: X. Fill it in and confirm (see s.md, step N), then close it with OK." Step N is read from the script the engine wrote.</summary>
    string DialogMessage(Step step)
    {
        ScriptPosition(out var file, out var n);
        if (_fill != null) return DialogSegments.FallbackMessage(step.DialogTitle, file, n, _fill.Segments, _fill.Progress, step.ClosedWith, _fill.Note);
        if (step.Clicks.Count > 0) return DialogSegments.ManualMessage(step.DialogTitle, file, n, step);
        return ScriptRef.Message(step.DialogTitle, file, n, step.Values, step.ValuesRead, step.ClosedWith);
    }

    /// <summary>The auto-filler for this dialog step, or null when the setting is off or the recording says too little to go on.</summary>
    DialogAutoFiller? MakeFiller(Step step)
    {
        if (!_autoFillOn) return null;
        var mode = DialogSegments.ModeOf(step);   // the clicks count as work
        if (mode == AutoMode.None) return null;
        var main = IntPtr.Zero;
        try { main = Autodesk.AutoCAD.ApplicationServices.Application.MainWindow.Handle; } catch (Exception ex) { Log.Error("autofill main window", ex); }
        var tdoc = _tdoc;
        var at = _i;
        return new DialogAutoFiller(mode, step, main, ignore: _win.Handle)
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
        _lastActedIndex = _i;
        var now = DateTime.Now;
        if (_sdi && s.Drawing != null)
        {
            // AutoCAD itself opens the file at the double-click: it can not be redirected to a copy, and it would replace the drawing the test runs in. Stop before the palette is touched.
            var why = $"the step \"{s.Title}\" opens {Path.GetFileName(s.Drawing)} by a palette double-click; with SDI on that replaces the open drawing with your file. Run the test with SDI = 0.";
            StopForPerson("palette", s.Title, why);
            return;
        }
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
        NoteSent(s.Command);   // the click may start the palette button's command at once (and a retry refreshes the time)
        var problem = s.Native != null
            ? NativePalette.Run(s, (x, y) => { if (_cursorHome == null && PaletteReveal.GetCursorPos(out var home)) _cursorHome = home; MoveCursor(new PaletteReveal.POINT { X = x, Y = y }); })
            : PaletteFill.Run(s, _paletteFill);
        if (problem == null)
        {
            Say($"\n  Palette \"{s.DialogTitle}\": done.\n");
            _result.Steps.Add(new StepResult { Index = _i + 1, Kind = "palette", Title = s.Title });
            // a right-click in a native palette: its menu is not replayed, the command picked from it is typed (the item is selected now)
            if (s.Native is { Action: "rclick" } && s.Command != null)
            {
                var typed = Plan.InputText(new Step { Kind = "command", Command = s.Command }.WithAnswers(s.Answers), handleExists: HandleExists);
                if (!string.IsNullOrEmpty(typed)) { NoteSent(s.Command); SendTracked(typed); _pendingStart = true; _sentAt = now; }
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
        bool menuFinal = PaletteFill.TakeMenuFinal();   // always asked: a final failure must not leak into the next step
        if (now - _paletteSince < PaletteWait && !menuFinal) { _paletteRetryAt = now.AddMilliseconds(500); return; }
        PaletteFill.Abandon();   // a context menu the step opened is closed before a person gets it
        if (_mode == ReplayMode.Auto && Unattended)
        {
            StopForPerson("palette", s.Title, problem, $"needs a person: palette {s.DialogTitle} ({problem})");
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
            if (running && !string.IsNullOrEmpty(text)) SendTracked(text);
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
        _paletteSince = DateTime.MinValue; _paletteRetryAt = DateTime.MinValue; _paletteFill = new();
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
        if (_savedView == null) { _savedView = saved; _savedViewDoc = _doc; _savedViewDb = SdiDocs.DbId(_doc); }   // the person's own view, kept from the first change of the step
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
        if (doc == null || !AcApp.DocumentManager.Cast<Document>().Contains(doc) || !SameDrawing(doc, _savedViewDb)) { _savedView = null; _savedViewDoc = null; saved.Dispose(); return; }   // (SDI: the Document object stays when the drawing in it is replaced: the Database tells)
        if (!ViewRestorer.Quiet(doc)) return;
        if (ViewControl.Restore(doc, saved)) { _savedView = null; _savedViewDoc = null; Log.Info("replay view restored"); }
        else Log.Info("the view the person had could not be put back yet, tried again after the next step");   // Restore keeps the view on failure
    }

    /// <summary>^C^C into the replay's drawing when it is the active one and something runs in it.</summary>
    void SdiCancelRunning()
    {
        try
        {
            var doc = _doc;
            if (ViewRestorer.IsShutdown || !AcApp.DocumentManager.Cast<Document>().Contains(doc)) return;
            if (!ViewRestorer.Quiet(doc) && ReferenceEquals(doc, AcApp.DocumentManager.MdiActiveDocument)) SdiDocs.Send(doc, "\u0003\u0003", false, false, false, null, true);
        }
        catch (Exception ex) { Log.Error("SDI cancel at the end of the run", ex); }
    }

    /// <summary>The run ends: the person's view is handed to <see cref="ViewRestorer"/>, which sets it once nothing is running, no input is queued and no dialog is open (and drops it if the
    /// person changed the view meanwhile). A running command is cancelled (Esc Esc) only when <paramref name="cancel"/>: Stop, or an error. A "needs a person" ending, a security prompt and
    /// the unloading of the plugin leave the command and its dialogs to the person, and nothing is sent to AutoCAD then or when the replay's drawing is not the active one.</summary>
    IntPtr _savedViewDb;   // the drawing the saved view belongs to
    static bool SameDrawing(Document d, IntPtr db) => SdiDocs.SameDb(SdiDocs.DbId(d), db);

    void HandOffView(bool cancel)
    {
        // SDI: Stop or an error cancels what runs in the drawing before anything can return early (the end-of-run save waits for it; the view of an own temp drawing is not restored)
        if (_sdi && cancel) SdiCancelRunning();
        var saved = _savedView; var doc = _savedViewDoc; var db = _savedViewDb;
        _savedView = null; _savedViewDoc = null; _savedViewDb = IntPtr.Zero;
        if (saved == null) return;
        if (ViewRestorer.IsShutdown || doc == null || !AcApp.DocumentManager.Cast<Document>().Contains(doc) || !SameDrawing(doc, db)) { saved.Dispose(); return; }
        // SDI: the run's own test drawing is saved at the end of the run (EndRun): a view set after that would be a change after the save, and there is no person's view in a temp copy
        if (_sdi && SdiGuard.IsOwnTestDrawing(doc.Name)) { saved.Dispose(); return; }
        if (!_sdi && cancel && !ViewRestorer.Quiet(doc) && ReferenceEquals(doc, AcApp.DocumentManager.MdiActiveDocument))
        {
            try { SdiDocs.Send(doc, "\u0003\u0003", false, false, false); } catch (Exception ex) { Log.Error("cancel before restoring the view", ex); }
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
        if (Plan.Problem(s.ChainClick ? s.WithAnswers(s.Answers.Where(a => a.T < s.ChainFromT)) : s) is { } problem)   // a chain click types only what came before its script
        {
            // typing it would put text where it does not belong; an unattended run must not end as PASS without it
            Say($"\n  WARNING: {problem}\n");
            _deferred = null;
            StopForPerson(s.Kind, s.Title, problem);
            return false;
        }
        if (_sdi && SdiGuard.ClosesDrawingCommand(s.Command))
        {
            var why = $"the step \"{s.Title}\" types {s.Command}; closing the only drawing ends AutoCAD under SDI. Run the test with SDI = 0.";
            StopForPerson(s.Kind, s.Title, why);
            return false;
        }
        if (_sdi && SdiGuard.OpensDrawingCommand(s.Command))
        {
            // typed, it would open the person's file (or a new drawing) in place of the test's drawing, and the replay can not point it at a copy
            var why = $"the step \"{s.Title}\" types {s.Command}; with SDI on that opens a file in place of the open drawing (a copy cannot be substituted in a typed command). Run the test with SDI = 0.";
            StopForPerson(s.Kind, s.Title, why);
            return false;
        }
        _clicked = null;
        // a script-chain click with a person's answers: the same per-dialog delivery whether the live button is clicked or its macro / LISP is typed (below)
        _chainStep = s.ChainClick && _interactive && s.ChainAnswers.Any() && _i + 1 < _steps.Count && _steps[_i + 1].Kind == "dialog" ? s : null;
        _clickDelivery = null; _clickDeliveryStep = null; _clickDeliveryPausing = false; _clickUnsafe = null;
        // a chain click with a person's answers and no dialog step after it (its modal state had no dialog window): the answers are typed as its script asks for them, after the click (TickClickDelivery)
        var afterClick = ReplayRules.ChainAnswersAfterClick(_steps, _i);
        if (afterClick.Count > 0)
        {
            _clickDeliveryStep = s;
            if (ChainDelivery.Unsafe(afterClick) is { } clickWhy)   // the replay cannot type them: a person gives them (as after a dialog)
                _clickUnsafe = $"the script \"{ReplayRules.ButtonName(s)}\" started asks for input a person gave while recording ({string.Join("; ", afterClick.Select(ReplayRules.AnswerText))}), which Retrace cannot type ({clickWhy}); give it, then press Next step";
            else _clickDelivery = new ChainDelivery(afterClick, DateTime.Now);
        }
        _chainPausedFor = _chainWaitFor = -1; _chainPausing = false; _delivery = null; _deliveryFor = -1;
        // RETRACEREPLAY (Step / Auto) has no person confirming dialogs, so answers given after a chain's dialog cannot be typed: stop before the click and name them (as it does for a dialog step)
        if (!_interactive && ReplayRules.ChainAnswersNeedPerson(s, _i + 1 < _steps.Count && _steps[_i + 1].Kind == "dialog") is { } lost)
        {
            Say($"\n  WARNING: {lost}\n");
            _deferred = null;
            StopForPerson(s.Kind, s.Title, lost);
            return false;
        }
        NoteSent(s.Command);   // before the click: the command it starts may be reported as unknown at once
        if (s.Kind == "ribbon" && (s.ChainClick || (s.Answers.Count == 0 && !s.HadDialog)))
        {
            // armed before the handler runs: a command it starts at once clears this in OnCmdStart and is neither waited for nor typed again
            _clickBits = Ask.ScriptOrLispBits(); _clicked = s; _clickedIndex = _i + 1; _pendingStart = true; _sentAt = DateTime.Now; _deferred = null;
            if (TryClick(s)) return true;
            _clicked = null; _pendingStart = false;
        }
        // interactive, and this step's command opens a dialog: hold back the answers given after the dialog opened
        var dialog = _interactive && _i + 1 < _steps.Count && _steps[_i + 1].Kind == "dialog" ? _steps[_i + 1] : null;
        string? text;
        // the view the points were made in is shown before they are typed and put back after the step; without it the step uses handles
        if (dialog != null)
        {
            (text, _deferred) = WithPick(pick => Plan.InputAroundDialog(s, dialog.T, HandleExists, pick), r => !string.IsNullOrEmpty(r.First));
            if (_chainStep != null) _deferred = null;   // a chain's input is a person's (_chainStep), not typed
        }
        else { text = WithPick(pick => Plan.InputText(s, handleExists: HandleExists, pick: pick), r => !string.IsNullOrEmpty(r)); _deferred = null; }
        if (string.IsNullOrEmpty(text)) { Say("\n  (nothing replayable in this step)\n"); return true; }
        Type(s, text);
        return true;
    }

    /// <summary>The answers of a chain click with no dialog step after it, typed as the click's commands ask for them (<see cref="ChainDelivery"/>). True when this tick is used up.</summary>
    bool TickClickDelivery()
    {
        var step = _clickDeliveryStep;
        if (step == null) { _clickDelivery = null; _clickDeliveryPausing = false; _clickUnsafe = null; return false; }
        if (_clickUnsafe != null) { var m = _clickUnsafe; _clickUnsafe = null; return PauseClickDelivery(step, m); }
        if (_clickDeliveryPausing)
        {
            if (!_userNext) return true;
            _clickDeliveryPausing = false; _userNext = false; _clickDeliveryStep = null;
            _notBefore = DateTime.Now.AddMilliseconds(400);
            return false;
        }
        var delivery = _clickDelivery!;
        var tick = delivery.Tick(DateTime.Now, _watch, !string.IsNullOrEmpty(_doc.CommandInProgress));
        switch (tick.Kind)
        {
            case DeliveryKind.Type:
                Log.Info($"replay: the script's command waits at \"{tick.Answer!.Message}\" ({tick.Answer.Kind}): typing {ReplayRules.AnswerText(tick.Answer)}");
                Say($"\n  typing {ReplayRules.AnswerText(tick.Answer)}\n");
                SendTracked(tick.Text!);
                _win.Show($"Step {_i}/{_steps.Count}: typing the script's input", ReplayRules.AnswerText(tick.Answer), false);
                return true;
            case DeliveryKind.Wait:
                if (!Settled()) { _win.Show($"Step {_i}/{_steps.Count}: waiting for the script's prompt", $"the command should ask for {ReplayRules.AnswerText(tick.Answer!)} (waits up to {(int)ChainDelivery.WaitSeconds} s).", false); return true; }
                // everything the click started is over and the prompt never came
                _clickDelivery = null;
                return PauseClickDelivery(step, $"the script \"{ReplayRules.ButtonName(step)}\" started ended without asking for {ReplayRules.AnswerText(tick.Answer!)}; give it, then press Next step");
            case DeliveryKind.TimedOut:
                _clickDelivery = null;
                return PauseClickDelivery(step, delivery.TimedOutMessage(step));
            default:   // Done
                _clickDelivery = null; _clickDeliveryStep = null; _notBefore = DateTime.Now.AddMilliseconds(400);
                return false;
        }
    }

    bool PauseClickDelivery(Step step, string message)
    {
        if (_suite != null || (_mode == ReplayMode.Auto && Unattended)) { StopForPerson("ribbon", step.Title, message); return true; }   // nobody to press Next
        Say($"\n  {message}\n");
        Log.Info("replay: " + message);
        _win.Show($"Step {_i}/{_steps.Count}: input needed", message, true);
        _userNext = false; _clickDeliveryPausing = true;
        return true;
    }

    /// <summary>A chain click's script is still running and nothing happens (it has not opened the next dialog, or waits for an answer): after 10 s of this, say so instead of sitting silent.</summary>
    void NoteChainWait()
    {
        if (_chainStep == null || !IsDialogStep(_i)) return;
        if (_chainWaitFor != _i) { _chainWaitFor = _i; _chainWaitSince = DateTime.Now; _chainWaitSaid = false; }
        if (_chainWaitSaid || (DateTime.Now - _chainWaitSince).TotalSeconds < 10) return;
        _chainWaitSaid = true;
        var why = "it has not opened the next dialog, or is waiting for an answer";
        if (_modalDepth > 0)
        {
            var open = Native.ForegroundOfThisProcess();
            var openTitle = open != IntPtr.Zero ? Native.TitleOf(open) : "";
            var expected = _i + 1 < _steps.Count && _steps[_i + 1].Kind == "dialog" ? _steps[_i + 1].DialogTitle : null;
            why = expected != null ? $"a window '{openTitle}' is open, expected '{expected}'" : $"a window '{openTitle}' is open";
        }
        var msg = $"paused: the click's script is still running and nothing has happened for 10 s ({why}). Open the next dialog or answer by hand; the replay goes on after it.";
        Say("\n  " + msg + "\n");
        Log.Info("replay: " + msg);
        _warnings.Add($"step {_i + 1}: {msg}");
        _win.Show($"Step {_i + 1}/{_steps.Count}: paused", msg, false);
    }

    bool IsDialogStep(int i) => i >= 0 && i < _steps.Count && _steps[i].Kind == "dialog";

    /// <summary>Types a step's text. SendStringToExecute: queued, runs as typed input when AutoCAD is next quiescent; "\n" is Enter.</summary>
    void Type(Step s, string text)
    {
        if (s.Kind == "ribbon") ActivateTab(s.Tab);
        NoteSent(s.Command);
        SendTracked(text);
        _pendingStart = true; _sentAt = DateTime.Now;
    }

    /// <summary>Invoke the live RibbonItem the way a click does: its CommandHandler.Execute(item). AutoCAD's own buttons (a RibbonCommandButton,
    /// its own handler, the macro in its Tag) and code-built buttons (a RibbonButton with an ICommand) both start their command this way from a
    /// timer tick (checked on AutoCAD 2026, 2026-11-04). The log names the item's type, handler and parameter, so an item that does nothing
    /// from code can be told apart from one that is gone.</summary>
    bool TryClick(Step s)
    {
        _lastActedIndex = _i;
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
        // its LISP is still running (it runs commands later), or AutoCAD says AutoLISP / a script is active: something started
        // a chain click's LISP and script run for a while: while one that started after the click is active, nothing is typed (another click does not wait: nothing it started can be told from what ran before)
        if (s.ChainClick && (_lispRunning > 0 || ReplayRules.NewScriptBits(Ask.ActiveBits(), _clickBits) != 0)) { Log.Info($"replay click: {s.ItemId} started no command within 2 s but its LISP or script is running; nothing typed"); return false; }
        // a chain click has no command of its own: it types its macro, else (blank macro) its LISP form
        var formName = !s.ChainClick ? "macro" : !string.IsNullOrWhiteSpace(s.Macro) ? "macro" : !string.IsNullOrWhiteSpace(s.Lisp) ? "LISP form" : null;
        if (s.Command == null && !(s.ChainClick && formName != null)) { Log.Info($"replay click: {s.ItemId} started no command within 2 s; its recording has none either, nothing typed"); return false; }
        // a chain click: its macro, else its LISP form (the old separate lisp step); the person's answers wait for the dialogs (_chainStep)
        var text = Plan.InputText(s.ChainClick ? s.WithAnswers(Array.Empty<Answer>()) : s, handleExists: HandleExists);
        var row = _result.Steps.LastOrDefault(x => x.Index == _clickedIndex);
        if (string.IsNullOrEmpty(text))
        {
            var none = $"the click started no command within 2 s ({s.Command ?? "no command"} when recorded), and the step has nothing to type instead (no macro or LISP form)";
            Say($"\n  WARNING: {none}\n");
            Log.Info($"replay click: {s.ItemId}: {none}");
            _warnings.Add($"step {_clickedIndex}: {none}");
            if (row != null) row.Message = none;
            return false;
        }
        var typed = $"the click started no command within 2 s ({s.Command ?? "no command"} when recorded); its {formName ?? "macro"} was typed instead";
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
