using System.Windows.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Retrace.Engine.Recordings;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Retrace.AutoCad;

/// <summary>What the <see cref="Replayer"/> does differently in a suite run (null for every other replay, and then nothing below applies): the per-step
/// timeout, no waiting for a person, a drawing the person had open is never replayed into, and Stop ends the whole suite.</summary>
sealed class SuiteHook
{
    public required TimeSpan StepTimeout { get; init; }
    /// <summary>The replay began.</summary>
    public required Action Started { get; init; }
    /// <summary>True for a drawing that was open before the suite started: a test's "open" step must not touch it.</summary>
    public required Func<Document, bool> IsProtected { get; init; }
    /// <summary>A copy of a drawing file in a temp folder (same file name) that the test opens instead of a protected drawing.</summary>
    public required Func<string, string> CopyOf { get; init; }
    /// <summary>The document mode (SDI) the suite read once at its start.</summary>
    public bool Single { get; init; }
    /// <summary>Set by the replay window's Stop: the suite ends after this test.</summary>
    public bool StopRequested { get; set; }
}

/// <summary>
/// A suite run (RETRACESUITE): every test of a list through the <see cref="TestQueue"/> (RETRACERUN's code path, unattended), keeping going past
/// FAIL / INCOMPLETE / ERROR / needs-person, and one summary at the end (<see cref="SuiteResult"/>). Between two tests the drawings the test left
/// open are closed without saving; the drawings that were open when the suite started are never closed or saved. If AutoCAD stays blocked by a
/// dialog or security prompt, or a drawing cannot be closed, for 30 s, the suite stops and the summary says what ran. UI thread only.
/// </summary>
sealed class SuiteRun : ITestQueueOwner
{
    public static SuiteRun? Current { get; private set; }

    static readonly TimeSpan BlockedLimit = TimeSpan.FromSeconds(30);

    readonly TestQueue _queue;
    readonly RetraceSettings _settings;
    readonly SuiteResult _result = new();
    readonly HashSet<Document> _baseline;
    // SDI (single document interface): there is one Document object for every drawing, so what was open before the suite cannot be told by object. The suite then
    // goes by the file of the drawing the person had open, which the first test replaces (it is never replaced with unsaved changes, see SdiGuard) and the end puts back.
    readonly bool _sdi;
    internal bool Single => _sdi;   // the document mode the suite read once at its start
    readonly string? _sdiOriginal;   // the full name of the drawing that was open at the start, when it is a saved file
    readonly SuiteHook _hookTemplate;
    readonly string _tempCopies = Path.Combine(Path.GetTempPath(), SdiGuard.SuiteCopyFolderPrefix + Guid.NewGuid().ToString("N"));
    DateTime? _closingSince, _blockedSince;
    Document? _home;   // a blank drawing the suite opens to run from when every drawing that was open before it is gone. Protected (no test continues in it); closed at the end only if it is unchanged since it was created
    string? _homeSnap; // what _home held when it was created (see Snapshot)
    int _modal;
    bool _done;

    SuiteRun(IReadOnlyList<string> recordings, RetraceSettings settings)
    {
        _settings = settings;
        _queue = new TestQueue(recordings, this);
        _baseline = AcApp.DocumentManager.Cast<Document>().ToHashSet();
        _sdi = SdiDocs.IsSingleNow();
        if (_sdi && AcApp.DocumentManager.MdiActiveDocument is { } start)
            try { _sdiOriginal = start.IsNamedDrawing && File.Exists(start.Name) && !SdiGuard.IsOwnTestDrawing(start.Name) ? start.Name : null; } catch (Exception) { }
        SdiGuard.Own.SuiteStarted();
        CleanStaleCopies();
        _result.Stamp = TestFolders.Stamp(DateTime.Now);
        _result.StartedUtc = DateTime.UtcNow.ToString("o");
        _result.CoreVersion = BuildInfo.Version;
        _result.StepTimeoutSeconds = settings.SuiteStepTimeoutSeconds;
        _hookTemplate = new SuiteHook
        {
            StepTimeout = TimeSpan.FromSeconds(settings.SuiteStepTimeoutSeconds),
            Started = () => _queue.MarkRunning(),
            IsProtected = d => _sdi ? IsSdiOriginal(d) : _baseline.Contains(d) || ReferenceEquals(d, _home),
            CopyOf = CopyFile,
            Single = _sdi,
        };
    }

    public static bool Start(IReadOnlyList<string> recordings, out string message)
    {
        if (Current != null) { message = "A suite is already running."; return false; }
        if (TestQueue.Active != null) { message = "A Tdoc run is active."; return false; }
        if (Replayer.Current != null) { message = "A replay is already running."; return false; }
        if (Recorder.Instance.IsRecording) { message = "Stop the recording first (-RETRACESTOP): a test opens a new drawing and replays into it."; return false; }
        if (recordings.Count == 0) { message = "No tests chosen."; return false; }
        if (AcApp.DocumentManager.MdiActiveDocument == null) { message = "Open a drawing first: the first test is started from it."; return false; }
        SuiteRun run;
        try { run = new SuiteRun(recordings, RetraceSettings.Load()); }
        catch (Exception ex) { Log.Error("suite start", ex); message = "The suite could not start: " + ex.Message; return false; }
        Current = run;
        AcApp.EnterModal += run.OnEnterModal;
        AcApp.LeaveModal += run.OnLeaveModal;
        message = $"Suite: {recordings.Count} test(s), step timeout {run._settings.SuiteStepTimeoutSeconds} s. The summary goes to {run._settings.Results}.";
        run.Say(message);
        StatusBoardHost.RunStarted();
        run._queue.Begin();
        return true;
    }

    /// <summary>For the status board: the queue (which test of how many).</summary>
    public TestQueue Queue => _queue;

    /// <summary>For the status board: why the suite waits for a person right now (a dialog or security prompt has stayed open for a few seconds
    /// between two tests), else null. The suite gives up on it after 30 s.</summary>
    public string? NeedsPersonReason => _blockedSince is { } since && DateTime.Now - since > TimeSpan.FromSeconds(3) ? _blockedReason : null;
    string? _blockedReason;

    void OnEnterModal(object? s, EventArgs e) => _modal++;
    void OnLeaveModal(object? s, EventArgs e) { if (_modal > 0) _modal--; }

    /// <summary>For <see cref="Commands.Test"/>, which has read the steps of <paramref name="path"/>: the hook for the replayer, or null when the suite
    /// did not ask for this test.</summary>
    public static SuiteHook? HookFor(string path)
    {
        var run = Current;
        if (run == null || !run._queue.IsSent(path)) return null;
        return new SuiteHook
        {
            StepTimeout = run._hookTemplate.StepTimeout, Started = run._hookTemplate.Started, IsProtected = run._hookTemplate.IsProtected, CopyOf = run._hookTemplate.CopyOf, Single = run._sdi,
        };
    }

    /// <summary>The replayer ended the test (after its result file is written).</summary>
    public static void Finished(SuiteHook hook, string path, ReplayResult r)
    {
        var run = Current;
        if (run == null || run._done) return;
        var seconds = (DateTime.Now - run._queue.SentAt).TotalSeconds;
        var t = SuiteTest.From(path, r, seconds);
        run._result.Tests.Add(t);
        run.Say($"{run._queue.Index + 1}/{run._queue.Recordings.Count} {t.Name}: {t.Verdict} ({t.DurationSeconds:0.#} s)" + (t.Verdict == ReplayResult.Pass || t.Message.Length == 0 ? "" : ": " + t.Message));
        if (hook.StopRequested) run._queue.Stopped = true;
        run._queue.Finished();
    }

    /// <summary>RETRACESUITE while a suite runs.</summary>
    public static string StopOrReport()
    {
        var run = Current;
        if (run == null) return "No suite is running.";
        var q = run._queue;
        if (Replayer.Current != null) return $"A suite is running (test {q.Index + 1} of {q.Recordings.Count}). Press Stop in the replay window to end it after this test.";
        if (q.Current == TestQueue.State.Sent) return $"A suite is starting test {q.Index + 1} of {q.Recordings.Count}. Press Stop in the replay window when it shows.";
        q.Stopped = true;
        run.Complete();
        return "Suite stopped.";
    }

    /// <summary>Shutdown: the summary of what ran is written.</summary>
    public static void Abort()
    {
        var run = Current;
        if (run == null) return;
        run.Finish("AutoCAD was closed or Retrace unloaded", shutdown: true);
    }

    // ---- the queue's owner ----------------------------------------------------------------------------------------------------------

    void Say(string message) => Commands.Print(message.StartsWith("Suite", StringComparison.Ordinal) ? message : "Suite: " + message);
    void ITestQueueOwner.Say(string message) => Say(message);

    void ITestQueueOwner.Skipped(int index, string path, string why)
    {
        _result.Tests.Add(SuiteTest.Skipped(path, why, (DateTime.Now - _queue.SentAt).TotalSeconds));
        Say($"{index + 1}/{_queue.Recordings.Count} {TestList.NameOf(TestFolders.NameOf(path))}: not run ({why})");
    }

    void ITestQueueOwner.Complete() => Complete();

    /// <summary>The queue has no drawing to type the next test into: the suite stops with that reason and the rest is listed as NOT-RUN.</summary>
    void ITestQueueOwner.Aborted(string reason) => Finish(reason);

    bool IsSdiOriginal(Document d)
    {
        try { return _sdiOriginal != null && SdiGuard.Own.SamePath(d.Name, _sdiOriginal); } catch (Exception) { return false; }
    }

    List<Document> Extras() => _sdi ? new List<Document>() : AcApp.DocumentManager.Cast<Document>().Where(d => !_baseline.Contains(d) && !ReferenceEquals(d, _home)).ToList();

    /// <summary>Temp copies of earlier suites that were never cleaned up (AutoCAD was closed in between): deleted when older than a day.</summary>
    static void CleanStaleCopies()
    {
        try
        {
            var openNames = SdiDocs.OpenNames();   // taken here, on the UI thread: the walk below runs on a pool thread
            var tmp = Path.GetTempPath();
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    foreach (var d in Directory.EnumerateDirectories(tmp, SdiGuard.SuiteCopyFolderPrefix + "*"))
                    {
                        if (!SdiGuard.IsStaleTemp(Directory.GetCreationTimeUtc(d), Directory.GetLastWriteTimeUtc(d), DateTime.UtcNow)) continue;   // the cheap check first: most folders are young
                        if (SdiDocs.HasSidecar(d)) { SdiDocs.CleanStaleCopyFolder(d, openNames); continue; }   // an SDI session's copies: the record rule (a drawing saved by someone else stays), never one that is open
                        if (openNames.Any(o => SdiGuard.Own.IsUnder(o, d))) continue;
                        try { Directory.Delete(d, true); } catch (Exception) { }
                    }
                }
                catch (Exception) { }
            });
        }
        catch (Exception) { }
    }

    void Complete() => Finish(_queue.Stopped ? "stopped by the user" : "");

    /// <summary>Before each test and before the end: the drawings the last test left open are closed without saving. A prompt or dialog that stays
    /// open, or a drawing that will not close, for 30 s stops the suite.</summary>
    QueueGate ITestQueueOwner.Gate()
    {
        if (_done) return QueueGate.Halt;
        var now = DateTime.Now;
        var last = _result.Tests.LastOrDefault()?.Name ?? "the start";
        var prompt = Native.OpenSecurityPrompt();
        if (prompt != null || _modal > 0)
        {
            _blockedSince ??= now;
            _blockedReason = prompt != null ? $"a security prompt is open: {prompt.Trim()}" : $"a dialog is open after {last}";
            if (now - _blockedSince < BlockedLimit) return QueueGate.Wait;
            Finish(prompt != null
                ? $"AutoCAD did not answer: a security prompt (\"{prompt.Trim()}\") stayed open after {last}"
                : $"AutoCAD did not answer: a dialog stayed open after {last}", blockedByDialog: true);
            return QueueGate.Halt;
        }
        _blockedSince = null; _blockedReason = null;
        if (_sdi) { _closingSince = null; return QueueGate.Go; }   // SDI: the next test's open closes the last one's drawing itself (Commands.NewTestDocument)
        var docs = AcApp.DocumentManager.Cast<Document>().ToList();
        // AutoCAD closed every drawing that was open at the start (an untouched new drawing is closed when a file is opened): a blank drawing of the
        // suite's own is what the next tests start from. A test's drawing is never taken over for that, and never counts as the person's.
        // (only when a test remains to run)
        if (_queue.Index + 1 < _queue.Recordings.Count && !_queue.Stopped && !docs.Any(_baseline.Contains) && (_home == null || !docs.Contains(_home)))
        {
            try { _home = OpenHome(); AcApp.DocumentManager.MdiActiveDocument = _home; _homeSnap = Snapshot(_home); }
            catch (Exception ex) { Log.Error("suite home drawing", ex); Finish($"no drawing is left to run the next test from, and a new one could not be opened: {ex.Message}"); return QueueGate.Halt; }
            return QueueGate.Wait;
        }
        var extra = Extras();
        if (extra.Count == 0) { _closingSince = null; return QueueGate.Go; }
        _closingSince ??= now;
        if (now - _closingSince > BlockedLimit)
        {
            Finish($"a drawing could not be closed after {last}: {string.Join(", ", extra.Select(d => System.IO.Path.GetFileName(d.Name)))}");
            return QueueGate.Halt;
        }
        CloseExtras(extra);
        return QueueGate.Wait;
    }

    /// <summary>A new blank drawing: the default template, else acad.dwt, else a plain Add.</summary>
    static Document OpenHome()
    {
        var dm = AcApp.DocumentManager;
        var template = Commands.FindTemplate();
        try { return dm.Add(string.IsNullOrEmpty(template) ? "acad.dwt" : template); }
        catch (Exception) { return dm.Add(""); }
    }

    static bool IsOpen(Document d) => AcApp.DocumentManager.Cast<Document>().Any(x => ReferenceEquals(x, d));

    /// <summary>What a drawing holds: the next handle and the number of objects in model space and paper space. A drawing nothing was drawn in keeps it.</summary>
    static string Snapshot(Document d)
    {
        var db = d.Database;
        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        int Count(ObjectId id) => ((BlockTableRecord)tr.GetObject(id, OpenMode.ForRead)).Cast<ObjectId>().Count();
        return $"{db.Handseed.Value}/{Count(SymbolUtilityServices.GetBlockModelSpaceId(db))}/{Count(SymbolUtilityServices.GetBlockPaperSpaceId(db))}";
    }

    /// <summary>True when the home drawing is open, quiet and holds what it held when it was created. The decision goes to the log.</summary>
    bool HomeUnchanged()
    {
        if (_home == null || !IsOpen(_home)) return false;
        try
        {
            if (!string.IsNullOrEmpty(_home.CommandInProgress)) { Log.Info("suite: home drawing left open: busy"); return false; }
            if (Snapshot(_home) != _homeSnap) { Log.Info("suite: home drawing left open: changed"); return false; }
            Log.Info("suite: home drawing closed");
            return true;
        }
        catch (Exception ex) { Log.Info($"suite: home drawing left open: could not be checked ({ex.Message})"); return false; }
    }

    static void CloseExtras(List<Document> extra)
    {
        foreach (var d in extra)
        {
            try
            {
                if (!string.IsNullOrEmpty(d.CommandInProgress))
                {
                    if (AcApp.DocumentManager.MdiActiveDocument != d) AcApp.DocumentManager.MdiActiveDocument = d;
                    SdiDocs.Send(d, "\u0003\u0003", true, false, false);   // the next try closes it
                    continue;
                }
                d.CloseAndDiscard();
            }
            catch (Exception ex) { Log.Info($"suite: {System.IO.Path.GetFileName(d.Name)} not closed yet ({ex.Message})"); }
        }
    }

    string CopyFile(string path) => SdiDocs.CopyInto(Path.Combine(_tempCopies, Guid.NewGuid().ToString("N")[..8]), path, Path.GetFileName(path), track: _sdi);

    // ---- the end ----------------------------------------------------------------------------------------------------------------------

    /// <summary>Writes the summary. <paramref name="stopReason"/> "" = the suite ran to its end. A test that was started but has no result, and the tests
    /// not reached, are listed (INCOMPLETE / NOT-RUN). The drawings of the suite are closed from application context, retrying until they are gone;
    /// at shutdown (<paramref name="shutdown"/>, or Retrace's view restorer shutting down) only the summary is written.
    /// <paramref name="blockedByDialog"/>: the gate gave up because a dialog or security prompt stayed open for 30 s (the status board then says needs-person while it is open).</summary>
    void Finish(string stopReason, bool shutdown = false, bool blockedByDialog = false)
    {
        if (_done) return;
        _done = true;
        if (Current == this) Current = null;
        var inFlight = _queue.Current != TestQueue.State.Idle ? _queue.SentPath : null;
        var ran = _queue.Current == TestQueue.State.Running;
        _queue.End();
        Log.Safe("suite unhook", () => { AcApp.EnterModal -= OnEnterModal; AcApp.LeaveModal -= OnLeaveModal; });
        _result.StopReason = stopReason;
        _result.AddMissing(_queue.Recordings, inFlight, ran);
        _result.FinishedUtc = DateTime.UtcNow.ToString("o");
        StatusBoardHost.SuiteEnded(_result, blockedByDialog);
        // the exact drawings to close, taken now: what the person opens afterwards is never touched. The blank start drawing goes too, if it is still untouched.
        if (!shutdown && !ViewRestorer.IsShutdown)
        {
            var toClose = Extras();
            if (HomeUnchanged()) toClose.Add(_home!);
            // SDI: the one drawing, and only when it is a test copy of this suite: its command is cancelled, nothing is closed; the person's own drawing is never sent anything
            if (_sdi) toClose = AcApp.DocumentManager.MdiActiveDocument is { } last && SdiGuard.IsOwnTestDrawing(last.Name) ? new List<Document> { last } : new List<Document>();
            CancelBusy(toClose);   // once, while the suite still owns the focus
            if (_sdi) RestoreSdiLater(); else CloseLater(toClose);
        }
        else Log.Safe("suite copies", () => { if (Directory.Exists(_tempCopies)) Directory.Delete(_tempCopies, true); });
        Log.Safe("suite write", () =>
        {
            var path = _result.Write(_settings.Results);
            Say(_result.Summary());
            foreach (var t in _result.Tests) Say($"  {t.Name}: {t.Verdict}, {t.DurationSeconds:0.#} s");
            Say("summary: " + path);
        });
    }

    /// <summary>^C^C once in every drawing of the suite that has a command running. Called from Finish only: after that nothing is activated or cancelled.</summary>
    static void CancelBusy(List<Document> list)
    {
        foreach (var d in list)
            try
            {
                if (string.IsNullOrEmpty(d.CommandInProgress)) continue;
                if (AcApp.DocumentManager.MdiActiveDocument != d) AcApp.DocumentManager.MdiActiveDocument = d;
                SdiDocs.Send(d, "\u0003\u0003", true, false, false);
            }
            catch (Exception ex) { Log.Info($"suite: cancel in {System.IO.Path.GetFileName(d.Name)} failed ({ex.Message})"); }
    }

    /// <summary>Closes the drawings that are quiet, without activating them (CloseAndDiscard works on a drawing that is not the active one, from
    /// application context). One with a command still running is left for the next try.</summary>
    static void CloseQuiet(List<Document> list)
    {
        foreach (var d in list)
            try { if (string.IsNullOrEmpty(d.CommandInProgress)) d.CloseAndDiscard(); }
            catch (Exception ex) { Log.Info($"suite: {System.IO.Path.GetFileName(d.Name)} not closed yet ({ex.Message})"); }
    }

    /// <summary>SDI: the drawing the person had open is opened again in place of the last test's drawing (a temp file, saved first so that nothing asks), or a
    /// blank copy of the template when there was none. On a timer in application context, once nothing runs in the drawing; the temp copies go last,
    /// when no drawing holds them.</summary>
    void RestoreSdiLater()
    {
        var tries = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Log.Safe("suite sdi restore", () =>
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            var busy = Native.OpenSecurityPrompt() != null || (doc != null ? !SdiDocs.IsIdle(doc) : SdiDocs.IsModalOpen());
            if (busy && ++tries < 60) return;
            timer.Stop();
            SdiGuard.Own.SuiteEnded();   // a test drawing whose end-of-run save never happened is nobody's now: its unsaved changes count as a person's
            if (busy) { Log.Info("suite: SDI: the last drawing is still busy after 60 s: left as it is"); Say("left open (close it yourself): " + (doc == null ? "" : System.IO.Path.GetFileName(doc.Name))); return; }
            var restored = false;
            try
            {
                if (doc != null && IsSdiOriginal(doc)) restored = true;   // the person's drawing is open again already
                else if (SdiDocs.PrepareForOpen(true) is { } refusal) { Log.Info("suite: SDI: the last drawing is left open: " + refusal); Say("left open: " + refusal); }
                else
                {
                    if (_sdiOriginal != null) SdiDocs.Open(_sdiOriginal, true);
                    else
                    {
                        // a blank copy of Retrace's own, opened outside any run: start-up code (acaddoc.lsp) may change it as it opens, and nobody else has: it is saved a few seconds later,
                        // else the next test would find it "modified" and refuse to replace it
                        var blank = SdiDocs.OpenBlankCopy();
                        SdiGuard.Own.BeginRun(blank.Name, false);
                        SdiDocs.EndRunLater(blank, true, TimeSpan.FromSeconds(4));
                    }
                    restored = true;
                }
            }
            catch (Exception ex) { Log.Info($"suite: SDI: the drawing from before the suite was not opened again ({ex.Message})"); Say("the drawing from before the suite was not opened again: " + ex.Message); }
            // the copies go only when nothing open is one of them (the restore happened and the active drawing is not below the folder)
            try
            {
                var active = AcApp.DocumentManager.MdiActiveDocument;
                var inUse = active != null && SdiGuard.Own.IsUnder(active.Name, _tempCopies);
                if (restored && !inUse && Directory.Exists(_tempCopies)) Directory.Delete(_tempCopies, true);
                else if (Directory.Exists(_tempCopies)) Log.Info("suite: the suite's copy folder stays (a drawing in it is open, or the restore did not happen)");
            }
            catch (Exception ex) { Log.Info($"suite copies not deleted: {ex.Message}"); }
            SdiDocs.CleanStaleSdi();   // once at the end of a suite (and once at start-up)
        });
        timer.Start();
    }

    /// <summary>The drawings of the suite that are still open (a command was running in one, or a dialog blocked the close) are closed on a timer, in
    /// application context. When none is left the temp copies are deleted.</summary>
    void CloseLater(List<Document> toClose)
    {
        var tries = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Log.Safe("suite close", () =>
        {
            var extra = toClose.Where(IsOpen).ToList();
            if (extra.Count > 0 && ++tries < 60) { CloseQuiet(extra); return; }
            timer.Stop();
            if (extra.Count > 0)
            {
                var left = string.Join(", ", extra.Select(d => System.IO.Path.GetFileName(d.Name)));
                Log.Info("suite: left open after 60 s: " + left);
                Say("left open (close them yourself): " + left);
            }
            try { if (Directory.Exists(_tempCopies)) Directory.Delete(_tempCopies, true); } catch (Exception ex) { Log.Info($"suite copies not deleted: {ex.Message}"); }
        });
        timer.Start();
    }
}
