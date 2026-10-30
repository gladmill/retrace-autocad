using Retrace.Engine.Recordings;
using Retrace.Engine.Util;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Retrace.AutoCad;

/// <summary>
/// The pictures of one test in a Tdoc run. The <see cref="Replayer"/> calls it at four points (all on the UI thread, each one a gate that says
/// "not yet" until the picture is taken, so the replay's 200 ms tick is never blocked):
///  - <see cref="BeforeStep"/>: the step is shown and AutoCAD has settled, the step has not run. Waits the settle delay, brings AutoCAD to the
///    front, hides Retrace's own replay window, finds the box of the ribbon button (UI Automation on a pool thread), then takes the picture.
///  - <see cref="DialogReady"/>: asked by <see cref="DialogAutoFiller"/> with the dialog filled in, before it presses OK / Cancel.
///  - <see cref="AfterStep"/>: a checkpoint step has run; its result goes into the manifest.
/// Boxes are drawn and the PNG is written on a pool thread. Every failure is logged and costs only the picture. Every hook is called only when a
/// Tdoc run is active; the replayer holds null otherwise.
/// </summary>
sealed class TdocCapture : IDisposable
{
    /// <summary>After the replay window is hidden the screen needs a moment to repaint before a picture.</summary>
    static readonly TimeSpan HideWait = TimeSpan.FromMilliseconds(300);
    /// <summary>The picture waits for AutoCAD's Idle event, but not longer than this after the settle delay.</summary>
    static readonly TimeSpan IdleWait = TimeSpan.FromSeconds(3);

    readonly List<Step> _steps;
    readonly string _folder;
    readonly int _testNo;
    readonly bool _front, _annotate;
    readonly TimeSpan _delay;
    readonly List<Task> _tasks = new();
    readonly int[] _pictures;
    Action<bool> _setReplayVisible = _ => { };
    IntPtr _main;
    DateTime _idleAt = DateTime.MinValue;   // set by the Idle handler (a plain field write)
    bool _disposed;

    // the step before which a picture is being prepared
    int _cur = -1;
    bool _ready;
    DateTime _begin, _hiddenAt;

    // the dialog being waited for
    int _dlgStep = -1, _dlgDone = -1;
    DateTime _dlgBegin, _dlgHidden;
    int _dx, _dy;

    /// <summary>The chapter of the manifest: one entry per step, pictures added as they are taken.</summary>
    public TrainingTest Test { get; }
    /// <summary>"Tdoc test 2/5": shown in front of the replay window's heading.</summary>
    public string Label { get; }
    /// <summary>The person pressed Stop (or the plugin shut down): the whole queue ends after this test.</summary>
    public bool StopRequested { get; set; }
    public int PictureCount => _pictures.Sum();

    public TdocCapture(TrainingTest test, List<Step> steps, string folder, int testNo, int testCount, RetraceSettings settings)
    {
        Test = test; _steps = steps; _folder = folder; _testNo = testNo;
        Label = $"Tdoc test {testNo}/{testCount}";
        _front = settings.TdocBringToFront; _annotate = settings.TdocAnnotate;
        _delay = TimeSpan.FromMilliseconds(settings.TdocStepDelayMs);
        _pictures = new int[steps.Count];
        for (int i = 0; i < steps.Count; i++)
            test.Steps.Add(new TrainingStep { Number = i + 1, Kind = TdocPlan.KindOf(steps[i]), Title = TdocPlan.StepTitle(steps[i]), Instructions = TdocPlan.Instructions(steps[i]) });
    }

    /// <summary>The replayer started: <paramref name="setReplayVisible"/> hides / shows its window; <paramref name="main"/> is AutoCAD's main window.</summary>
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
        Log.Safe("tdoc idle unhook", () => AcApp.Idle -= OnIdle);
    }

    void HideReplay() { if (_front) _setReplayVisible(false); }
    void ShowReplay() { try { _setReplayVisible(true); } catch (Exception ex) { Log.Error("tdoc show replay window", ex); } }

    /// <summary>The replay window is shown again: the dialog gate hid it for its picture, and the auto-filler gave up before asking the gate
    /// again (the dialog closed, a value stopped verifying). The person needs that window's message and its Stop button.</summary>
    public void EnsureShown() => ShowReplay();

    /// <summary>Waits (bounded) for the pictures still being written. Call from a pool thread.</summary>
    public void Drain(int timeoutMs = 20000)
    {
        Task[] all;
        lock (_tasks) all = _tasks.ToArray();
        try { Task.WaitAll(all, timeoutMs); } catch (Exception ex) { Log.Error("tdoc drain", ex); }
    }

    // ---- before a step ------------------------------------------------------------------------------------------------------

    /// <summary>False until the picture of the step is taken (the replayer asks again on its next tick); true at once for a dialog step (the
    /// dialog is pictured by <see cref="DialogReady"/>).</summary>
    public bool BeforeStep(int i, Step s, DateTime now)
    {
        try { return BeforeStepCore(i, s, now); }
        catch (Exception ex)
        {
            Log.Error("tdoc before step", ex);
            ShowReplay();
            _cur = i; _ready = true;
            return true;
        }
    }

    bool BeforeStepCore(int i, Step s, DateTime now)
    {
        if (s.Kind == "dialog") return true;
        if (i != _cur)
        {
            _cur = i; _ready = false; _begin = now; _hiddenAt = DateTime.MinValue;
            if (s.Kind == "ribbon") Replayer.ActivateTab(s.Tab);   // the button must show
            if (s.Kind == "mark") ZoomExtents();   // the checkpoint picture shows the whole drawing (the view only; the checkpoint compares objects)
            if (_front) Native.Front(_main);
        }
        if (_ready) return true;
        if (now - _begin < _delay) return false;
        if (_hiddenAt == DateTime.MinValue)
        {
            _hiddenAt = now;
            HideReplay();
        }
        if (_front && now - _hiddenAt < HideWait) return false;
        if (_idleAt < _begin && now - _begin < _delay + IdleWait) return false;   // AutoCAD has not been idle since the step was shown
        Shoot(i, s);
        ShowReplay();
        _ready = true;
        return true;
    }

    /// <summary>ZOOM Extents in the active drawing, typed as a command: AutoCAD is quiescent here (the step has settled), and the step's delay
    /// lets it finish before the picture.</summary>
    static void ZoomExtents()
    {
        try { Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument?.SendStringToExecute("_.ZOOM _E ", true, false, false); }
        catch (Exception ex) { Log.Error("tdoc zoom extents", ex); }
    }

    void Shoot(int i, Step s)
    {
        if (s.Kind == "palette") { ShootPalette(i, s); return; }
        var w = ShotService.CaptureWindow(_main, workArea: true);
        if (w == null) { Log.Info($"tdoc: no picture of step {i + 1} (AutoCAD's window is hidden or minimized)"); return; }
        var targets = new List<(string Text, ScreenRect? Rect)>();
        if (_annotate)
            foreach (var p in TdocPlan.StepCallouts(s))
            {
                if (p.Role == CalloutRole.RibbonItem)
                {
                    var hit = string.IsNullOrEmpty(s.ItemId) ? null : TdocTargets.RibbonItemRect(s.ItemId);
                    // the button sits in a collapsed panel: the box is on the panel, and the legend says to open it first
                    targets.Add((hit is { Panel: true } ? $"Open the {(string.IsNullOrWhiteSpace(s.Panel) ? "collapsed" : s.Panel.Trim())} panel, then {char.ToLowerInvariant(p.Text[0])}{p.Text[1..]}" : p.Text, hit?.Rect));
                }
                else targets.Add((p.Text, p.Role == CalloutRole.CommandLine ? TdocTargets.CommandLineRect(_main) : null));
            }
        Save(i, w.Value.Pic, w.Value.Left, w.Value.Top, targets, TdocPlan.Caption(s));
    }

    void ShootPalette(int i, Step s)
    {
        var page = PaletteFill.FindPage(s);
        // an open palette: its own frame; hidden (auto-hide) or not found: AutoCAD's window, without boxes
        if (page != IntPtr.Zero && PaletteReveal.PictureRect(page) is { } r && ShotService.CaptureRect(r.Left, r.Top, r.Right, r.Bottom) is { } pic)
        {
            var targets = new List<(string Text, ScreenRect? Rect)>();
            if (_annotate)
                foreach (var b in PaletteFill.TdocBoxes(s)) targets.Add((b.Text, TdocTargets.Of(b)));
            Save(i, pic, r.Left, r.Top, targets, TdocPlan.Caption(s));
            return;
        }
        var w = ShotService.CaptureWindow(_main);
        if (w != null) Save(i, w.Value.Pic, w.Value.Left, w.Value.Top, [], TdocPlan.Caption(s));
        else Log.Info($"tdoc: no picture of step {i + 1} (palette not shown)");
    }

    // ---- a dialog, filled in ---------------------------------------------------------------------------------------------

    /// <summary>The gate <see cref="DialogAutoFiller"/> asks before it presses OK / Cancel: false until the picture of the filled-in dialog is taken.</summary>
    public bool DialogReady(int i, DialogShot shot, DateTime now)
    {
        try { return DialogReadyCore(i, shot, now); }
        catch (Exception ex)
        {
            Log.Error("tdoc dialog", ex);
            ShowReplay();
            _dlgDone = i;
            return true;
        }
    }

    bool DialogReadyCore(int i, DialogShot shot, DateTime now)
    {
        if (_dlgDone == i || i < 0 || i >= _steps.Count) return true;
        if (_dlgStep != i)
        {
            _dlgStep = i; _dlgBegin = now; _dlgHidden = DateTime.MinValue; _dx = 0; _dy = 0;
            if (_front)
            {
                Native.Front(shot.Dialog);
                (_dx, _dy) = Native.MoveOntoScreen(shot.Dialog);   // a dialog off the screen would not be in any picture
            }
        }
        if (now - _dlgBegin < _delay) return false;
        if (_dlgHidden == DateTime.MinValue) { _dlgHidden = now; HideReplay(); }
        if (_front && now - _dlgHidden < HideWait) return false;
        var w = ShotService.CaptureWindow(shot.Dialog);
        if (w != null)
        {
            var targets = new List<(string Text, ScreenRect? Rect)>();
            if (_annotate)
            {
                foreach (var b in shot.Filled) targets.Add((b.Text, TdocTargets.Of(b, _dx, _dy)));
                if (shot.Close != null) targets.Add((shot.Close.Text, TdocTargets.Of(shot.Close, _dx, _dy)));
            }
            Save(i, w.Value.Pic, w.Value.Left, w.Value.Top, targets, TdocPlan.Caption(_steps[i]));
            // the instruction names the button as it reads ("Click OK." for a LISP alert, whose only button has Cancel's id)
            if (shot.Close != null && i < Test.Steps.Count && Test.Steps[i].Instructions is { Count: > 0 } ins)
                ins[^1] = Rx.Replace(ins[^1], @"click (OK|Cancel)\.$", m => (char.IsUpper(m.Value[0]) ? "Click " : "click ") + shot.Close.Text + ".", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        else Log.Info($"tdoc: no picture of the dialog of step {i + 1}");
        ShowReplay();
        _dlgDone = i;
        return true;
    }

    // ---- after a step ------------------------------------------------------------------------------------------------------------

    /// <summary>A step has run. A checkpoint copies its result (the replayer has just added it to <paramref name="result"/>) into the manifest.</summary>
    public void AfterStep(int i, Step s, ReplayResult result)
    {
        try
        {
            if (s.Kind != "mark" || i < 0 || i >= Test.Steps.Count || result.Checkpoints.Count == 0) return;
            var cp = result.Checkpoints[^1];
            Test.Steps[i].Checkpoint = new TrainingCheckpoint { Index = cp.Index, Note = cp.Note, Pass = cp.Pass, Differences = [.. cp.Differences] };
        }
        catch (Exception ex) { Log.Error("tdoc checkpoint", ex); }
    }

    // ---- the picture ----------------------------------------------------------------------------------------------------------------

    /// <summary>Adds the picture to the step and writes it on a pool thread. <paramref name="targets"/>: the legend lines in the order they are
    /// numbered, each with its rectangle on the screen (null = not found; the line is left out). The picture shows the screen area at
    /// (<paramref name="sx"/>, <paramref name="sy"/>) the size of <paramref name="pic"/>, scaled down to fit.</summary>
    void Save(int i, ShotService.Picture pic, int sx, int sy, IReadOnlyList<(string Text, ScreenRect? Rect)> targets, string? caption)
    {
        var (nw, nh) = ShotImage.FitSize(pic.Width, pic.Height);
        var candidates = targets.Select(t => t.Rect is { } r ? Annotate.ToPicture(r.Left, r.Top, r.Right, r.Bottom, sx, sy, pic.Width, pic.Height, nw, nh, 1) : null).ToList();
        var numbered = TdocPlan.Number(targets.Select((t, k) => new CalloutSpec(t.Text, candidates[k] != null)).ToList());
        var boxes = numbered.Select(n => candidates[n.Index]!.Value with { Number = n.Number }).ToList();

        var no = ++_pictures[i];
        var path = Path.Combine(_folder, TdocNaming.PicturesDir, TdocNaming.PictureName(_testNo, i + 1, no));
        Test.Steps[i].Pictures.Add(new TrainingPicture
        {
            File = TdocNaming.PictureRel(_testNo, i + 1, no), Caption = caption,
            Callouts = numbered.Select(n => new TrainingCallout { Number = n.Number, Text = n.Text }).ToList(),
        });
        var px = pic.Pixels; int w = pic.Width, h = pic.Height;
        var task = Task.Run(() =>
        {
            try
            {
                var data = nw == w && nh == h ? px : ShotImage.Scale(px, w, h, nw, nh);
                Annotate.Draw(data, nw, nh, boxes);
                WritePng(path, nw, nh, data);
            }
            catch (Exception ex) { Log.Error("tdoc picture", ex); }
        });
        lock (_tasks) { _tasks.RemoveAll(x => x.IsCompleted); _tasks.Add(task); }
    }

    /// <summary>Through a temp file, so the Word writer never reads half a picture.</summary>
    static void WritePng(string path, int w, int h, byte[] bgra)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + ".tmp");
        try
        {
            Png.Write(tmp, w, h, bgra);
            File.Move(tmp, path, overwrite: true);
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }
}
