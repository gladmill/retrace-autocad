using System.Windows.Threading;
using Retrace.Engine;
using Retrace.Engine.Recordings;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Retrace.AutoCad;

/// <summary>
/// A Tdoc run: replays a list of tests one after another and writes the training document at the end. Each test goes through the RETRACERUN code
/// path (<see cref="Commands.Test"/>: a new drawing, <see cref="Replayer"/> interactive with dialog auto-fill): the run types `_RETRACERUN` into the
/// active drawing, Test() asks <see cref="TakeExpected"/> for the recording instead of asking a person, and <see cref="CaptureFor"/> hands the
/// replayer its <see cref="TdocCapture"/>. When the replayer finishes it calls <see cref="OnFinished"/>, which starts the next test (posted, not inside
/// the replayer's Finish). After the last test, or Stop, the manifest is written at once and the Word file by the engine in the background.
/// One run at a time. UI thread only (the engine's callback marshals back).
/// </summary>
sealed class TdocRun
{
    public static TdocRun? Current { get; private set; }

    /// <summary>A test that was sent to RETRACERUN must start within this time, else it is skipped (its command failed before the replay began).</summary>
    static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(90);

    enum State { Idle, Sent, Running }

    readonly string _title, _folder, _manifestPath, _docxPath;
    readonly IReadOnlyList<string> _recordings;
    readonly RetraceSettings _settings;
    readonly TrainingManifest _manifest = new();
    readonly List<TdocCapture> _captures = new();
    readonly List<string> _summary = new();
    readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromSeconds(2) };
    int _index = -1;
    State _state = State.Idle;
    string? _sentPath;
    bool _taken, _stopped;
    DateTime _sentAt;

    TdocRun(string title, IReadOnlyList<string> recordings, RetraceSettings settings)
    {
        _title = string.IsNullOrWhiteSpace(title) ? "Training" : title.Trim();
        _recordings = recordings; _settings = settings;
        var root = Path.Combine(settings.Docs, TdocNaming.TrainingDir);
        _folder = TestFolders.UniqueFolder(root, TdocNaming.FolderName(DateTime.Now, _title));
        _manifestPath = Path.Combine(_folder, TdocNaming.ManifestFile);
        _docxPath = TdocNaming.DocxPath(_folder);
        _watch.Tick += (_, _) => Log.Safe("tdoc watch", Watch);
    }

    /// <summary>Starts a run over the recordings (each a recording.jsonl). False with the reason in <paramref name="message"/>.</summary>
    public static bool Start(string title, IReadOnlyList<string> recordings, out string message)
    {
        if (Current != null) { message = "A Tdoc run is already active."; return false; }
        if (Replayer.Current != null) { message = "A replay is already running."; return false; }
        if (Recorder.Instance.IsRecording) { message = "Stop the recording first (RETRACESTOP): a test opens a new drawing and replays into it."; return false; }
        if (recordings.Count == 0) { message = "No tests chosen."; return false; }
        if (AcApp.DocumentManager.MdiActiveDocument == null) { message = "Open a drawing first: the first test is started from it."; return false; }
        var s = RetraceSettings.Load();
        TdocRun run;
        try { run = new TdocRun(title, recordings, s); }
        catch (Exception ex) { Log.Error("Tdoc start", ex); message = "Tdoc could not start: " + ex.Message; return false; }
        Current = run;
        message = $"Tdoc: {recordings.Count} test(s), written to {run._folder} (as {Path.GetFileName(run._docxPath)} beside it).";
        run.Say(message);
        if (!s.AutoFillDialogs) run.Say("Tdoc: the setting autoFillDialogs is off, so dialogs are not filled in and get no picture.");
        run._watch.Start();
        run.Next();
        return true;
    }

    /// <summary>For <see cref="Commands.Test"/>: the recording of the test the run just sent to RETRACERUN (once), else null.</summary>
    public static string? TakeExpected()
    {
        var run = Current;
        if (run == null || run._state != State.Sent || run._taken) return null;
        run._taken = true;
        return run._sentPath;
    }

    /// <summary>For <see cref="Commands.Test"/>, which has read the steps of <paramref name="path"/>: the capture for the replayer, or null when the
    /// run did not ask for this test.</summary>
    public static TdocCapture? CaptureFor(string path, List<Step> steps)
    {
        var run = Current;
        if (run == null || run._state != State.Sent || !string.Equals(path, run._sentPath, StringComparison.OrdinalIgnoreCase)) return null;
        var test = new TrainingTest { Name = TestList.NameOf(TestFolders.NameOf(path)), Folder = Path.GetDirectoryName(path) ?? "" };
        var capture = new TdocCapture(test, steps, run._folder, run._index + 1, run._recordings.Count, run._settings);
        run._captures.Add(capture);
        return capture;
    }

    /// <summary>The replayer began: the chapter goes into the manifest.</summary>
    public static void Started(TdocCapture capture)
    {
        var run = Current;
        if (run == null || !run._captures.Contains(capture)) return;
        run._state = State.Running;
        run._manifest.Tests.Add(capture.Test);
        run.Say($"{capture.Label}: {capture.Test.Name} ({capture.Test.Steps.Count} steps)");
    }

    /// <summary>The test that was sent to RETRACERUN could not be started (the recording is gone, the new drawing never became active...): skip it.</summary>
    public static void StartFailed(string why)
    {
        if (Current is { _state: State.Sent } run) run.Skip(why);
    }

    /// <summary>The replayer ended the test (from its Finish, after the result file is written).</summary>
    public static void Finished(TdocCapture capture, ReplayResult r)
    {
        var run = Current;
        if (run == null || !run._captures.Contains(capture)) { capture.Dispose(); return; }
        capture.Test.Verdict = r.Verdict;
        capture.Test.Message = string.IsNullOrWhiteSpace(r.Message) ? null : r.Message;
        run._summary.Add($"{capture.Test.Name}: {r.Verdict}, {capture.PictureCount} picture(s)");
        run.Say($"{capture.Label}: {capture.Test.Name} {r.Verdict}, {capture.PictureCount} picture(s)");
        capture.Dispose();
        if (capture.StopRequested) run._stopped = true;
        run._state = State.Idle;
        run.Post(run.Next);
    }

    /// <summary>RETRACETDOC while a run is active: between two tests it ends the run; during a test the replay window's Stop does.</summary>
    public static string StopOrReport()
    {
        var run = Current;
        if (run == null) return "No Tdoc run is active.";
        if (Replayer.Current != null) return $"A Tdoc run is active (test {run._index + 1} of {run._recordings.Count}). Press Stop in the replay window to end it.";
        // the next test was sent to RETRACERUN and is about to start: ending now would leave that RETRACERUN without its test (a file picker)
        if (run._state == State.Sent) return $"A Tdoc run is starting test {run._index + 1} of {run._recordings.Count}. Press Stop in the replay window when it shows.";
        run._stopped = true;
        run.Complete();
        return "Tdoc run stopped.";
    }

    /// <summary>Shutdown: nothing more is started; the manifest of what was done is written (the Word file is not: the engine is shutting down).</summary>
    public static void Abort()
    {
        var run = Current;
        if (run == null) return;
        Current = null;
        run._watch.Stop();
        foreach (var c in run._captures) c.Dispose();
        run._stopped = true;
        Log.Safe("tdoc abort manifest", () => { if (run.WriteManifest()) run.Say($"Tdoc stopped; the manifest is {run._manifestPath}"); });
    }

    // ---- the queue --------------------------------------------------------------------------------------------------------------------

    void Say(string message) => Commands.Print(message.StartsWith("Tdoc", StringComparison.Ordinal) ? message : "Tdoc: " + message);

    void Post(Action a) => _ui.BeginInvoke(DispatcherPriority.Background, () => Log.Safe("tdoc next", a));

    void Next()
    {
        if (Current != this) return;
        if (_stopped || ++_index >= _recordings.Count) { Complete(); return; }
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc == null) { Say("no drawing is open; the run ends here"); Complete(); return; }
        _sentPath = _recordings[_index]; _taken = false; _sentAt = DateTime.Now; _state = State.Sent;
        // Test() asks TakeExpected for the recording; this is what the Run test button types, ^C^C first: the test before may have left a
        // prompt open (a step that failed), and the text would answer it instead of starting the next test
        doc.SendStringToExecute("\u0003\u0003_RETRACERUN ", true, false, false);
    }

    void Skip(string why)
    {
        var name = TestList.NameOf(TestFolders.NameOf(_sentPath ?? ""));
        _summary.Add($"{name}: not run ({why})");
        Say($"test {_index + 1}/{_recordings.Count} ({name}) was not run: {why}");
        _state = State.Idle;
        Post(Next);
    }

    void Watch()
    {
        if (_state == State.Sent && Replayer.Current == null && DateTime.Now - _sentAt > StartTimeout) Skip("it did not start within 90 s");
    }

    // ---- the end ------------------------------------------------------------------------------------------------------------------------

    bool WriteManifest()
    {
        _manifest.Title = _title + (_stopped ? " (incomplete)" : "");
        _manifest.CreatedUtc = DateTime.UtcNow.ToString("o");
        _manifest.RetraceVersion = BuildInfo.Version;
        try { _manifest.Write(_manifestPath); return true; }
        catch (Exception ex) { Log.Error("tdoc manifest", ex); Say("the manifest could not be written: " + ex.Message); return false; }
    }

    void Complete()
    {
        if (Current != this) return;
        Current = null;
        _watch.Stop();
        foreach (var line in _summary) Say("  " + line);
        if (_manifest.Tests.Count == 0) { Say("no test ran; no document written."); return; }
        if (!WriteManifest()) return;
        var captures = _captures.ToList();
        var manifest = _manifestPath; var docx = _docxPath;
        var wait = EngineJobs.IsRunning;
        if (!EngineJobs.Start(_ =>
            {
                foreach (var c in captures) c.Drain();   // the last pictures are still being written
                return EngineRunner.WriteTrainingDoc(manifest, docx);
            }, r => _ui.BeginInvoke(() => Log.Safe("tdoc done", () => Done(r))), out var err))
        { Say("the document could not be queued: " + err); return; }
        Say((_stopped ? "stopped; " : "") + (wait ? "another Retrace job is running; the Word file is written right after it..." : "writing the Word file in the background...") + $"\n  {_manifestPath}");
    }

    void Done(EngineResult r)
    {
        if (r.Success && r.Docx != null)
        {
            Say("done. Word file: " + r.Docx);
            foreach (var w in r.Warnings.Take(10)) Say("  warning: " + w);
        }
        else if (r.Cancelled) Say("cancelled; the manifest and pictures are kept: " + _folder);
        else Say($"the Word file could not be written: {r.Error}. The manifest and pictures are kept: {_folder}");
    }
}
