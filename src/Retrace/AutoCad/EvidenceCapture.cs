using System.Globalization;
using System.Windows.Threading;
using Retrace.Core;
using Retrace.Engine.Recordings;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Retrace.AutoCad;

/// <summary>
/// Evidence from a replay: after every step the replay has run (and AutoCAD has settled) a picture of AutoCAD's window is taken, and when the test ends a filled-in copy of the sign-off sheet is written beside steps.xlsx
/// (<c>steps-evidence_&lt;time&gt;.xlsx</c>: the picture after each step in its Evidence cell, Pass / Fail per step, a note for a failure, a line that says it was an automated run).
/// Only a replay started for evidence (RETRACEEVIDENCE's Run with evidence, -RETRACEEVIDENCE) holds one; every other replay holds null and none of the hooks does anything. Pictures go to
/// <c>&lt;test&gt;\evidence\auto_&lt;time&gt;\step-NN.png</c> (NN = the replay step). The replayer calls:
///  - <see cref="AfterPrevious"/> where the Tdoc hooks sit, at each point where the step before has run and settled (a gate: false until the picture is taken, so the 200 ms tick is never blocked). Every window of Retrace's own
///    (the replay window, an open Open test window) is hidden for the picture;
///  - <see cref="Failure"/> from Finish when the test ended before its last step (error, stopped for a person): a picture of what AutoCAD shows then;
///  - <see cref="Finished"/> with the result: the workbook is written in the background (the mapping from replay steps to sheet rows is <see cref="EvidenceMapping"/>).
/// A suite or Tdoc run never holds one.
/// </summary>
sealed class EvidenceCapture : IDisposable
{
    static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(700), IdleWait = TimeSpan.FromSeconds(3);
    const int HideWaitMs = 200;   // after Retrace's windows are hidden, the screen needs a moment before the picture

    readonly string _recording, _folder, _runDir;
    readonly List<Step> _steps;
    readonly string?[] _pics;
    readonly List<Task> _tasks = [];
    readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    readonly DateTime _started = DateTime.Now;
    Action<bool> _setReplayVisible = _ => { };
    IntPtr _main;
    DateTime _idleAt = DateTime.MinValue;
    int _key = -1; bool _ready; DateTime _begin;
    bool _disposed;
    int _endStep = -1;   // the step the test ended on when it ended early (Failure)
    EvidenceEnd _end = EvidenceEnd.Completed;
    int _written;

    /// <summary>Pictures that are on disk.</summary>
    public int PictureCount => Volatile.Read(ref _written);

    public EvidenceCapture(string recordingPath, List<Step> steps)
    {
        _recording = recordingPath; _steps = steps;
        _folder = Path.GetDirectoryName(Path.GetFullPath(recordingPath)) ?? "";
        _runDir = Path.Combine(EvidenceFiles.FolderOf(_folder), "auto_" + _started.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        _pics = new string?[steps.Count];
    }

    string PathOf(int step) => Path.Combine(_runDir, $"step-{step + 1:00}.png");

    public void Begin(Action<bool> setReplayVisible, IntPtr main)
    {
        _setReplayVisible = setReplayVisible; _main = main;
        AcApp.Idle += OnIdle;
    }

    void OnIdle(object? s, EventArgs e) => _idleAt = DateTime.Now;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Log.Safe("evidence idle unhook", () => AcApp.Idle -= OnIdle);
    }

    /// <summary>The replay is at step <paramref name="next"/> (or past the end) and the step before it has run and settled: its picture is taken. False until it is (the replayer asks again on its next tick). The picture itself is
    /// taken inside one call that hides Retrace's windows and always shows them again (finally): no tick can leave them hidden.</summary>
    public bool AfterPrevious(int next, DateTime now)
    {
        try
        {
            if (next <= 0 || next > _steps.Count) return true;
            if (_key != next)
            {
                _key = next; _ready = false; _begin = now;
                Native.Front(_main);
            }
            if (_ready) return true;
            if (now - _begin < Settle) return false;
            if (_idleAt < _begin && now - _begin < Settle + IdleWait) return false;   // an Idle event since the step settled (bounded)
            Snap(next - 1);
            _ready = true;
            return true;
        }
        catch (Exception ex) { Log.Error("evidence picture", ex); _ready = true; return true; }
    }

    /// <summary>Hides Retrace's windows, waits a moment, takes the picture and shows the windows again, whatever happens.</summary>
    void Snap(int step)
    {
        PictureWindows? hidden = null;
        try
        {
            _setReplayVisible(false);
            hidden = PictureWindows.Hide();
            Thread.Sleep(HideWaitMs);
            Shoot(step);
        }
        finally
        {
            hidden?.Restore();
            try { _setReplayVisible(true); } catch (Exception ex) { Log.Error("evidence show replay window", ex); }
        }
    }

    void Shoot(int step)
    {
        if (EvidenceShot.Take(_main) is not { } p) { Log.Info($"evidence: no picture after step {step + 1} (AutoCAD's window could not be captured)"); return; }
        var path = PathOf(step);
        var t = Task.Run(() =>
        {
            try { EvidenceShot.WritePng(path, p.Pixels, p.W, p.H); _pics[step] = path; Interlocked.Increment(ref _written); }
            catch (Exception ex) { Log.Error("evidence write", ex); }
        });
        lock (_tasks) { _tasks.RemoveAll(x => x.IsCompleted); _tasks.Add(t); }
    }

    /// <summary>The test ended before its last step: a picture of what is on the screen now, kept as the picture of step <paramref name="step"/> (0-based, the step that was being run) when that step has none. Waits a
    /// moment (UI thread, once) so Retrace's windows are off the screen. A Stop pressed by a person takes no picture, but the step is remembered.</summary>
    public void Failure(int step, EvidenceEnd end)
    {
        try
        {
            if (step < 0 || step >= _steps.Count) return;
            _endStep = step; _end = end;
            if (end == EvidenceEnd.Stopped || _pics[step] != null) return;
            Snap(step);
        }
        catch (Exception ex) { Log.Error("evidence failure picture", ex); }
    }

    /// <summary>Waits (bounded) for the pictures still being written. Pool thread.</summary>
    void Drain()
    {
        Task[] all; lock (_tasks) all = _tasks.ToArray();
        try { Task.WaitAll(all, 20000); } catch (Exception ex) { Log.Error("evidence drain", ex); }
    }

    /// <summary>The replay ended: writes the workbook in the background and says where it is on the command line.</summary>
    public static void Finished(EvidenceCapture c, ReplayResult r)
    {
        c.Dispose();
        string version = ""; Log.Safe("acad version", () => version = AcApp.Version.ToString());
        Task.Run(() =>
        {
            try
            {
                c.Drain();
                var path = c.WriteWorkbook(r, version);
                c._ui.BeginInvoke(() => Commands.Print($"Evidence: {r.Verdict}, {c.PictureCount} picture(s). Workbook: {path}"));
            }
            catch (Exception ex)
            {
                Log.Error("evidence workbook", ex);
                c._ui.BeginInvoke(() => Commands.Print("Evidence: the workbook could not be written: " + EvidenceMapping.Scrub(ex.Message)));
            }
        });
    }

    string WriteWorkbook(ReplayResult r, string acadVersion)
    {
        var warnings = new List<string>();
        var stepsMd = Exporter.ResolveSteps(_folder, warnings, out var error) ?? throw new IOException(error ?? "steps.md could not be made");
        var doc = StepsDoc.Load(stepsMd);

        // the sheet's numbered steps, by the time each began: a replay step belongs to the last one that began at or before it
        var one = RecordingReader.Load(Path.GetFullPath(_recording));
        RecordingAnalysis.Run([one], null);
        var numberedT = one.Steps.Where(ScriptWriter.IsNumbered).Select(s => s.T).ToList();

        var outcomes = EvidenceMapping.Outcomes(_steps, r, _pics, numberedT, _endStep, _end);
        bool waited = r.Steps.Any(x => x.WaitedForPerson);
        var info = EvidenceBook.AutomatedInfo(DateTime.Now, BuildInfo.Version, acadVersion, r.Verdict, r.StepsRun, r.StepsTotal, waited);
        if (numberedT.Count != doc.Steps.Count) info.Add(("Note", $"steps.md has {doc.Steps.Count} steps and the recording {numberedT.Count} numbered ones: pictures may sit on a neighbouring step."));
        if (!string.IsNullOrWhiteSpace(r.Message)) info.Add(("Message", EvidenceMapping.Scrub(r.Message)));
        var ev = EvidenceBook.FromOutcomes(doc, outcomes, info, "Automated run (Retrace)", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return EvidenceBook.Write(doc, _folder, ev, DateTime.Now);
    }
}
