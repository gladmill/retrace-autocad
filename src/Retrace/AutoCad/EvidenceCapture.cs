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
    const int HideWaitMs = 450;   // after Retrace's windows are hidden, the screen needs a moment before the picture (the replay window fades out; shorter waits left its ghost in the picture); none when nothing was visible to hide

    readonly string _recording, _folder, _runDir;
    readonly List<Step> _steps;
    readonly bool[] _noWindow;   // by replay step: its dialog never had a window on the screen (no "open" picture)
    readonly string?[] _pics, _opened, _filled, _failed;   // by replay step: after it ended, its dialog open, its dialog filled in, the screen when the test ended on it
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
        _noWindow = new bool[steps.Count]; _pics = new string?[steps.Count]; _opened = new string?[steps.Count]; _filled = new string?[steps.Count]; _failed = new string?[steps.Count];
    }

    string PathOf(int step, string suffix = "") => Path.Combine(_runDir, $"step-{step + 1:00}{suffix}.png");

    // the two dialog moments (the dialog open, the dialog filled in): same places as the Tdoc's hooks
    static readonly TimeSpan OpenedSettle = TimeSpan.FromMilliseconds(500), FilledSettle = TimeSpan.FromMilliseconds(400), OpenedMaxWait = TimeSpan.FromSeconds(6);
    int _openKey = -1, _fillKey = -1; bool _openDone, _fillDone; DateTime _openBegin, _openSeen, _fillBegin; IntPtr _openDialog;
    Func<bool> _replayShown = () => true;

    /// <summary>The dialog of step <paramref name="i"/> is open and nothing has been filled in: its picture (the screen with the dialog on it). False until taken (the replayer asks again on its next tick); once per step.</summary>
    public bool DialogOpened(int i, IntPtr replayWindow, DateTime now)
    {
        try
        {
            if (i < 0 || i >= _steps.Count) return true;
            if (_openKey != i) { _openKey = i; _openDone = false; _openBegin = now; _openSeen = DateTime.MinValue; _openDialog = IntPtr.Zero; }
            if (_openDone) return true;
            // the wait for the dialog to be drawn starts when a dialog window is on the screen, not when AutoCAD entered the modal state
            if (_openSeen == DateTime.MinValue)
            {
                var dlg = Native.DialogWindow(_main, replayWindow);
                if (dlg != IntPtr.Zero) { _openSeen = now; _openDialog = dlg; }
                else if (now - _openBegin > OpenedMaxWait) { _noWindow[i] = true; _openDone = true; Log.Info($"evidence: no dialog window for step {i + 1}: no picture of it open"); return true; }
                else return false;
            }
            if (now - _openSeen < OpenedSettle) return false;
            Snap(i, "-open", _opened, _openDialog, alone: false);
            _openDone = true;
            return true;
        }
        catch (Exception ex) { Log.Error("evidence dialog open picture", ex); _openDone = true; return true; }
    }

    /// <summary>The dialog of step <paramref name="i"/> is filled in and its closing button is about to be pressed (asked by the dialog filler): the picture, with the dialog in front. False until taken.</summary>
    public bool DialogReady(int i, IntPtr dialog, DateTime now)
    {
        try
        {
            if (i < 0 || i >= _steps.Count) return true;
            if (_fillKey != i) { _fillKey = i; _fillDone = false; _fillBegin = now; if (dialog != IntPtr.Zero) Native.Front(dialog); }
            if (_fillDone) return true;
            if (now - _fillBegin < FilledSettle) return false;
            Snap(i, "-filled", _filled, dialog, alone: true);
            _fillDone = true;
            return true;
        }
        catch (Exception ex) { Log.Error("evidence dialog picture", ex); _fillDone = true; return true; }
    }

    public void Begin(Action<bool> setReplayVisible, IntPtr main, Func<bool>? replayShown = null)
    {
        _setReplayVisible = setReplayVisible; _main = main; _replayShown = replayShown ?? (() => true);
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
            Snap(next - 1, "", _pics);
            _ready = true;
            return true;
        }
        catch (Exception ex) { Log.Error("evidence picture", ex); _ready = true; return true; }
    }

    /// <summary>Hides Retrace's windows, waits a moment, takes the picture and shows the windows again, whatever happens.</summary>
    /// <summary><paramref name="dialog"/> non-zero: a picture for a dialog moment while the command's modal dialog is open. Then the floating Guide palette is left alone unless it overlaps the dialog (showing it again may take the activation from the dialog), the dialog gets the
    /// foreground back afterwards, and the picture is the dialog's own (<paramref name="alone"/>: the dialog alone when it is small) or AutoCAD's window widened to hold the dialog.</summary>
    void Snap(int step, string suffix, string?[] into, IntPtr dialog = default, bool alone = false)
    {
        PictureWindows? hidden = null;
        try
        {
            bool replayShown = _replayShown();
            _setReplayVisible(false);
            bool guide = true;
            if (dialog != IntPtr.Zero && Native.Exists(dialog)) { var d = PaletteReveal.RectOf(dialog); guide = Retrace.Ui.GuidePalette.FloatingOverlaps(d.Left, d.Top, d.Right, d.Bottom); }
            hidden = PictureWindows.Hide(guide);
            if (replayShown || hidden.AnyHidden) Thread.Sleep(HideWaitMs);
            Shoot(step, suffix, into, dialog, alone);
        }
        finally
        {
            hidden?.Restore();
            try { _setReplayVisible(true); } catch (Exception ex) { Log.Error("evidence show replay window", ex); }
            if (dialog != IntPtr.Zero) Native.Front(dialog);   // whatever showing the windows again did to the activation: the dialog is the one in front
        }
    }

    void Shoot(int step, string suffix, string?[] into, IntPtr dialog = default, bool alone = false)
    {
        (byte[] Pixels, int W, int H)? taken;
        if (dialog != IntPtr.Zero)
        {
            var t2 = EvidenceShot.TakeWithDialog(_main, dialog, alone);
            if (t2 != null) Log.Info($"evidence: step {step + 1}{suffix}: picture of the {t2.Value.What}");
            taken = t2?.Pic;
        }
        else taken = EvidenceShot.Take(_main);
        if (taken is not { } p) { Log.Info($"evidence: no picture after step {step + 1} (AutoCAD's window could not be captured)"); return; }
        var path = PathOf(step, suffix);
        var t = Task.Run(() =>
        {
            try { EvidenceShot.WritePng(path, p.Pixels, p.W, p.H); into[step] = path; Interlocked.Increment(ref _written); }
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
            Snap(step, "-failed", _failed);
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
        // the numbered steps are the ones the script wrote (steps.md), and for each its sub-items (answers, dialogs) in the order of the sheet's rows
        var numberedT = new List<long>();
        var bullets = new List<IReadOnlyList<Engine.Emit.WordingBullet>>();
        foreach (var st in one.Steps)
        {
            var list = new List<Engine.Emit.WordingBullet>();
            if (Engine.Emit.StepWording.Render(st, numberedT.Count + 1, new Engine.Emit.WordingContext { Script = true, Bullets = list }) == null) continue;
            numberedT.Add(st.T); bullets.Add(list);
        }
        var shots = _steps.Select((_, k) => (StepShots?)new StepShots(_opened[k], _filled[k], _pics[k], _failed[k], _noWindow[k])).ToList();
        // per-row placement only when the sheet on disk has the recording's steps (a hand-edited or older steps.md: the pictures keep to each step's own row)
        var shape = EvidenceRows.Shape(doc, numberedT.Count);
        var outcomes = EvidenceMapping.Outcomes(_steps, r, _pics, numberedT, _endStep, _end, shots, shape == null ? null : bullets, shape?.RowCounts, shape?.SubRowsFilled);
        bool waited = r.Steps.Any(x => x.WaitedForPerson);
        var info = EvidenceBook.AutomatedInfo(DateTime.Now, acadVersion, r.Verdict, r.StepsRun, r.StepsTotal, waited);
        if (numberedT.Count != doc.Steps.Count) info.Add(("Note", $"steps.md has {doc.Steps.Count} steps and the recording {numberedT.Count} numbered ones: pictures may sit on a neighbouring step."));
        if (!string.IsNullOrWhiteSpace(r.Message)) info.Add(("Message", EvidenceMapping.Scrub(r.Message)));
        var ev = EvidenceBook.FromOutcomes(doc, outcomes, info, "Automated run", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return EvidenceBook.Write(doc, _folder, ev, DateTime.Now);
    }
}
