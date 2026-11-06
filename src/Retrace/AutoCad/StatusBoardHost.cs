using System.Windows.Threading;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Retrace.AutoCad;

/// <summary>
/// Writes Retrace's state to the AutoCAD status board (`retrace-&lt;acad pid&gt;.json`, see <see cref="StatusBoard"/> and the CONTRACT.md of the AcadStatus project).
/// Thin on purpose: the texts, the JSON and the file are in Core (<see cref="StatusBoard"/>, <see cref="StatusWriter"/>); this class only collects
/// what Retrace is doing from the recorder, the replayer and the suite / Tdoc runs, on the UI thread.
/// - A <see cref="DispatcherTimer"/> on the UI thread rewrites the file every 3 s (the file goes stale after 15 s). A hung UI thread stops the timer, so the
///   file goes stale: that is the point (the status board then shows AutoCAD as busy).
/// - <see cref="Changed"/> writes at once when what the board shows differs from the file (the replayer calls it on every tick; a repeat writes nothing).
/// - Nothing here throws, and nothing in a handler calls it (handlers only record data); a failed write is logged once by the writer, which also waits
///   10 s before it tries again, so an unwritable folder never slows a replay.
/// - Setting `statusBoard` off: nothing is written; a file written before the setting was turned off is set to "stopped" (retried until it is written).
/// </summary>
static class StatusBoardHost
{
    static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(3);
    /// <summary>RETRACERUN starts its replay when its new drawing is ready (it waits up to 60 s), after the command returned: the owner decided at its start is kept this long
    /// from the moment the command returns (not from its start: the person may take any time in its file picker). The other commands start their run inside the command, so no
    /// window applies to them.</summary>
    static readonly TimeSpan DeferredOwnerWindow = TimeSpan.FromSeconds(65);

    static readonly StartOrigin Origin = new();
    static StatusWriter? _writer;
    static DispatcherTimer? _timer;
    static bool _started, _stopped, _wasEnabled, _stopPending, _loggedFacts;   // _loggedFacts: the last failure to work out the entry is logged; cleared by the next one that works
    static string? _replayOwner, _recordOwner;   // who started the replay / the recording that runs now ("user"), else null; a replay ending leaves the recording's
    static string? _nextOwner;                   // decided when RETRACERECORD / RETRACEREPLAY / RETRACERUN / RETRACESTEP started; taken by the run it starts
    static DateTime? _nextOwnerDeadline;         // only for RETRACERUN
    static StatusBoard.StatusEnd? _end;          // how the last run ended
    // Dialogs open now (EnterModal / LeaveModal): a run that ended at one keeps saying needs-person until it closes. Not an existing counter because none is always on: the
    // replayer's _modalDepth and the suite's _modal live only while a run does, and the recorder's only while it records; the run that just ended has dropped its own.
    static int _modal;
    static DateTime _settingChecked = DateTime.MinValue, _settingWritten = DateTime.MinValue, _settingBrokenLogged = DateTime.MinValue;
    static bool _enabled = true;

    // ---- start / stop -------------------------------------------------------------------------------------------------------------------

    public static void Start()
    {
        try
        {
            if (_started) return;
            _started = true;
            var folder = StatusBoard.Folder(Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
            _writer = new StatusWriter(folder, Environment.ProcessId, StatusBoard.WriterName(BuildInfo.Version), ex => Log.Error("status board write", ex), pidStart: ProcessStart());
            AcApp.EnterModal += OnEnterModal;
            AcApp.LeaveModal += OnLeaveModal;
            _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = Heartbeat };
            _timer.Tick += (_, _) => Publish(force: true);
            _timer.Start();
            Publish(force: true);
        }
        catch (Exception ex) { Log.Error("status board start", ex); }
    }

    /// <summary>When acad.exe started (UTC), read once for `pidStart`: Windows reuses process ids, and readers use it to tell a reused pid from this writer. Null when it cannot be read.</summary>
    static DateTime? ProcessStart()
    {
        try { using var p = System.Diagnostics.Process.GetCurrentProcess(); return p.StartTime.ToUniversalTime(); }
        catch (Exception ex) { Log.Error("status board process start time", ex); return null; }
    }

    /// <summary>Terminate: stop the timer and say "stopped" (the file stays; the status board reads it as the writer being gone). Tried even inside the writer's backoff.</summary>
    public static void Shutdown()
    {
        if (_stopped) return;
        _stopped = true;
        Log.Safe("status board shutdown", () =>
        {
            _timer?.Stop();
            AcApp.EnterModal -= OnEnterModal;
            AcApp.LeaveModal -= OnLeaveModal;
            if (_wasEnabled || _stopPending) _writer?.Stop(final: true);
        });
    }

    // handlers: plain field writes
    static void OnEnterModal(object? s, EventArgs e) => _modal++;
    static void OnLeaveModal(object? s, EventArgs e) { if (_modal > 0) _modal--; }

    // ---- what happened ------------------------------------------------------------------------------------------------------------------

    /// <summary>The state may have changed: writes the file if what it shows is different. Cheap when nothing changed.</summary>
    public static void Changed() => Publish(force: false);

    /// <summary>A person clicked a Retrace button (<paramref name="macro"/>, e.g. "^C^C_RETRACERUN "): the command it types is the user's, if it starts at once.</summary>
    public static void UserClicked(string? macro) => Log.Safe("status board click", () => Origin.Click(macro, DateTime.UtcNow));

    /// <summary>A Retrace command started (RetraceApp.Run). The commands that start a run decide who owns it here, once per invocation: "user" when the click that typed
    /// it is the last click, else nobody. A click is used up by the invocation it starts, so a command typed or sent over COM later never gets it.</summary>
    public static void CommandStarted(string name)
    {
        try
        {
            if (name is not ("RETRACERECORD" or "RETRACEREPLAY" or "RETRACERUN" or "RETRACESTEP")) return;
            _nextOwner = Origin.Take(DateTime.UtcNow, name) ? "user" : null;
            _nextOwnerDeadline = null;   // RETRACERUN's window starts when the command returns (CommandEnded)
        }
        catch (Exception ex) { Log.Error("status board command", ex); }
    }

    /// <summary>A Retrace command returned. RETRACERECORD, RETRACEREPLAY and RETRACESTEP start their run inside the command, so an owner not taken by now is for nothing.
    /// Any other command (RETRACESTATUS, a transparent 'RETRACEMARK) leaves it alone, and RETRACERUN's waits for its replay, for <see cref="DeferredOwnerWindow"/> from now.</summary>
    public static void CommandEnded(string name)
    {
        if (name is "RETRACERECORD" or "RETRACEREPLAY" or "RETRACESTEP") _nextOwner = null;
        else if (name == "RETRACERUN" && _nextOwner != null) _nextOwnerDeadline = DateTime.UtcNow + DeferredOwnerWindow;
    }

    /// <summary>A suite or Tdoc run began.</summary>
    public static void RunStarted()
    {
        _end = null; _replayOwner = null; _nextOwner = null;
        Changed();
    }

    public static void RecordingStarted()
    {
        _end = null;
        _recordOwner = TakeNextOwner();
        Publish(force: true);
    }

    /// <summary>A replay began. <paramref name="queued"/>: a suite or Tdoc run started it (the run owns the board entry then).</summary>
    public static void ReplayStarted(bool queued)
    {
        if (!queued)
        {
            _end = null;
            _replayOwner = TakeNextOwner();
        }
        else _nextOwner = null;
        Publish(force: true);
    }

    static string? TakeNextOwner()
    {
        var owner = _nextOwner != null && (_nextOwnerDeadline is not { } deadline || DateTime.UtcNow <= deadline) ? _nextOwner : null;
        _nextOwner = null; _nextOwnerDeadline = null;
        return owner;
    }

    /// <summary>A replay ended (its result is written). <paramref name="stoppedForPerson"/>: it stopped because a step needs a person. A test of a suite or Tdoc run only
    /// moves that run on; its end says the counts.</summary>
    public static void ReplayEnded(ReplayResult r, bool queued, bool stoppedForPerson)
    {
        try
        {
            if (!queued)
            {
                _replayOwner = null;
                _end = StatusBoard.EndOfReplay(r, stoppedForPerson, DialogOpen());
            }
        }
        catch (Exception ex) { Log.Error("status board replay end", ex); }
        Publish(force: true);
    }

    /// <summary>A suite ended. <paramref name="blockedByDialog"/>: it stopped because a dialog or security prompt stayed open between two tests.</summary>
    public static void SuiteEnded(SuiteResult r, bool blockedByDialog)
    {
        try { _end = StatusBoard.EndOfSuite(r, blockedByDialog, DialogOpen()); _replayOwner = null; }
        catch (Exception ex) { Log.Error("status board suite end", ex); }
        Publish(force: true);
    }

    public static void TdocEnded(IReadOnlyList<string> verdicts, int pictures, bool stopped)
    {
        try { _end = StatusBoard.EndOfTdoc(verdicts, pictures, stopped); _replayOwner = null; }
        catch (Exception ex) { Log.Error("status board tdoc end", ex); }
        Publish(force: true);
    }

    /// <summary>A dialog or AutoCAD's security prompt is open now.</summary>
    static bool DialogOpen() => _modal > 0 || Native.OpenSecurityPrompt() != null;

    // ---- the file -----------------------------------------------------------------------------------------------------------------------

    static void Publish(bool force)
    {
        var w = _writer;
        if (w == null || _stopped) return;
        try
        {
            if (!Enabled())
            {
                // turned off: say "stopped" once, and keep trying until it is written (the writer waits after repeated failures): a file nobody updates reads as AutoCAD hung
                if (_wasEnabled || _stopPending) { _wasEnabled = false; _stopPending = !w.Stop("Status board turned off"); }
                return;
            }
            _stopPending = false;
            _wasEnabled = true;
            StatusEntry entry;
            try { entry = StatusBoard.Compose(Facts()); _loggedFacts = false; }   // a good one re-arms the log
            catch (Exception ex)
            {
                if (!_loggedFacts) { _loggedFacts = true; Log.Error("status board", ex); }   // once: a tick every 200 ms would flood the log
                entry = StatusBoard.ErrorEntry(ex);   // the file must keep moving: a file nobody updates reads as AutoCAD hung
            }
            w.Publish(entry, force);
        }
        catch (Exception ex) { if (!_loggedFacts) { _loggedFacts = true; Log.Error("status board", ex); } }   // the writer and Enabled() do not throw; this is for what nobody foresaw
    }

    /// <summary>Setting `statusBoard`, read again when settings.json changed (checked at most every 2 s). A file that cannot be parsed (a hand edit, a write in progress) keeps
    /// the value from before (the defaults it would give are not the user's) and is not marked as seen, so it is tried again at every check until it reads.</summary>
    static bool Enabled()
    {
        var now = DateTime.UtcNow;
        if (now - _settingChecked < TimeSpan.FromSeconds(2)) return _enabled;
        _settingChecked = now;
        try
        {
            var file = Paths.SettingsFile();
            var written = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
            if (written != _settingWritten)
            {
                if (RetraceSettings.TryLoad(out var s)) { _settingWritten = written; _enabled = s.StatusBoard; }
                else if (_settingBrokenLogged != written)
                {
                    _settingBrokenLogged = written;   // once per version of the file
                    Log.Info("status board: settings.json cannot be read; keeping statusBoard " + (_enabled ? "on" : "off"));
                }
            }
        }
        catch (Exception ex) { Log.Error("status board setting", ex); }
        return _enabled;
    }

    /// <summary>What Retrace is doing now. A suite or Tdoc run comes first (its tests replay inside it), then a replay, then a recording.</summary>
    static StatusFacts Facts()
    {
        var rep = Replayer.Current?.Status();
        if (SuiteRun.Current is { } suite) return Queue(StatusMode.Suite, suite.Queue, rep, suite.NeedsPersonReason, "suite");
        if (TdocRun.Current is { } tdoc) return Queue(StatusMode.Tdoc, tdoc.Queue, rep, null, "tdoc");
        if (rep != null)
            return new StatusFacts { Mode = StatusMode.Replay, Test = rep.Test, Step = rep.Step, Steps = rep.Steps, StepText = rep.StepText, Note = rep.Note, NeedsPerson = rep.NeedsPerson, Owner = _replayOwner };
        var recorder = Recorder.Instance;
        if (recorder.IsRecording)
            return new StatusFacts { Mode = StatusMode.Record, Test = TestList.NameOf(recorder.Writer?.Name ?? ""), RecordedSteps = recorder.StepCount, Owner = _recordOwner };
        _recordOwner = null;
        if (_end is { NeedsPerson: true } && !DialogOpen()) _end = _end with { NeedsPerson = false, Reason = null };   // the person dealt with it
        return new StatusFacts { Mode = StatusMode.Idle, Last = _end?.Last, LastIsError = _end?.IsError ?? false, LastNeedsPerson = _end?.NeedsPerson ?? false, NeedsPerson = _end?.Reason };
    }

    static StatusFacts Queue(StatusMode mode, TestQueue queue, ReplayProgress? rep, string? blocked, string owner) => new()
    {
        Mode = mode, QueueIndex = queue.Position, QueueCount = queue.Total,
        Test = rep?.Test ?? "", Step = rep?.Step ?? 0, Steps = rep?.Steps ?? 0, StepText = rep?.StepText ?? "",
        Note = rep?.Note, NeedsPerson = rep?.NeedsPerson ?? blocked, Owner = owner,
    };
}
