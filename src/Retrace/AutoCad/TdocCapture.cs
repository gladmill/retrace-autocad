using Retrace.Engine.Recordings;
using Retrace.Engine.Util;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Retrace.AutoCad;

/// <summary>
/// The pictures of one test in a Tdoc run. The <see cref="Replayer"/> calls it at these points (all on the UI thread, each one a gate that says
/// "not yet" until the picture is taken, so the replay's 200 ms tick is never blocked):
///  - <see cref="BeforeStep"/>: the step is shown and AutoCAD has settled, the step has not run. Waits the settle delay, brings AutoCAD to the
///    front, hides Retrace's own replay window, finds the box of the ribbon button (UI Automation on a pool thread), then takes the picture.
///  - <see cref="DialogReady"/>: asked by <see cref="DialogAutoFiller"/> with the dialog filled in, before it presses OK / Cancel.
///  - <see cref="AfterStep"/>: a checkpoint step has run; its result goes into the manifest.
///  - <see cref="AfterCommand"/> / <see cref="AfterDialog"/>: the answers a command's person gave after a dialog (a step of their own, <see cref="TdocSplit"/>) have been typed: the picture
///    is taken when the command has ended (the last such step) or when the next dialog of the command has opened. <see cref="AnswersTyped"/> tells it that the replay typed them; a step whose
///    answers never were (the dialog failed or was cancelled) is dropped from the manifest when the test ends.
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

    /// <summary>The state of one picture gate (the step's, the answers after a dialog's): the key it is waiting for, whether its picture is taken, when it began and when the replay window was hidden for it.</summary>
    sealed class PicGate { public int Key = -1; public bool Ready; public DateTime Begin, Hidden; }
    readonly PicGate _stepGate = new(), _afterGate = new();

    // the dialog being waited for
    readonly bool _smart;   // setting screenshotMode = smart: ribbon and palette pictures are 1:1 crops (SmartCrop)
    readonly bool _arrows;   // setting showClickArrows: the picture points at the control the step clicks or fills
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
    /// <summary>The document mode (SDI) of the run, read once by it.</summary>
    public bool? Single { get; init; }

    public TdocCapture(TrainingTest test, List<Step> steps, string folder, int testNo, int testCount, RetraceSettings settings)
    {
        Test = test; _steps = steps; _folder = folder; _testNo = testNo;
        Label = $"Tdoc test {testNo}/{testCount}";
        _front = settings.TdocBringToFront; _annotate = settings.TdocAnnotate; _arrows = settings.ShowClickArrows; _smart = settings.SmartShots;
        _delay = TimeSpan.FromMilliseconds(settings.TdocStepDelayMs);
        // the document's steps: a command's answers given after a dialog are a step of their own (TdocSplit)
        var entries = TdocSplit.Entries(steps);
        _pictures = new int[entries.Count];
        _m = new int[steps.Count]; _before = new Step[steps.Count];
        for (int k = 0; k < entries.Count; k++)
        {
            var e = entries[k];
            test.Steps.Add(new TrainingStep { Number = k + 1, Kind = TdocSplit.Kind(e), Title = TdocSplit.Title(e), Instructions = TdocSplit.Instructions(e) });
            if (e.After) { _after[e.Index] = new Continuation(k, e.Shown, e.Group, e.GroupEnd, e.Last); continue; }
            _m[e.Index] = k; _before[e.Index] = e.Shown;
        }
    }

    readonly int[] _m;                 // replay step -> its step in the document
    readonly Step[] _before;           // replay step -> what its text and boxes are made from (a step that has answers after a dialog: only the ones before)
    /// <summary>The step of the answers given after a dialog: its place in the document, its text's source, the first and last dialog step of the command (the replay types them all when one of its dialogs is done) and whether it is the last of the command.</summary>
    sealed record Continuation(int Doc, Step Shown, int Group, int GroupEnd, bool Last);
    readonly Dictionary<int, Continuation> _after = new();   // dialog step -> the answers given after it
    readonly HashSet<int> _typed = new();                    // dialog steps after which the replay typed the answers of their command
    bool Typed(Continuation c) => TdocSplit.IsTyped(_typed, c.Group, c.GroupEnd);

    /// <summary>The replay typed the answers a command's person gave after its dialogs (it is at the dialog step <paramref name="dialog"/>, the first of the command, or a later one when the next dialog opened at once).</summary>
    public void AnswersTyped(int dialog) => _typed.Add(dialog);

    /// <summary>The replay is at step <paramref name="next"/> (or past the end) and everything before it has run and settled: when the step before is the last dialog of a command, the answers
    /// typed after it have run too, and the picture of that step is taken now. False until it is (the replayer asks again on its next tick).</summary>
    public bool AfterCommand(int next, DateTime now) => AfterDialog(next - 1, last: true, now);

    /// <summary>The dialog step <paramref name="dialog"/> has been done and the replay is at the step after it: <paramref name="last"/> false = that step is another dialog of the command (the answers between the two
    /// are pictured when it has opened).</summary>
    public bool AfterDialog(int dialog, bool last, DateTime now)
    {
        try
        {
            if (!_after.TryGetValue(dialog, out var c) || c.Last != last || !Typed(c)) return true;   // not typed: no picture (the step goes when the test ends)
            return Gate(_afterGate, dialog, now, null, null, () => Shoot(c.Doc, c.Shown, continuation: true), front: last);   // between two dialogs the next one is open and in front: AutoCAD's window is not brought up over it (the dialog fill looks for it in the foreground)
        }
        catch (Exception ex) { Log.Error("tdoc after dialog", ex); ShowReplay(); _afterGate.Key = dialog; _afterGate.Ready = true; return true; }
    }

    /// <summary>One picture gate: false until the picture is taken, true after. Waits the settle delay, brings AutoCAD to the front, hides the replay window, waits for AutoCAD to be idle, then calls
    /// <paramref name="shoot"/>. <paramref name="start"/> runs when <paramref name="key"/> is new; <paramref name="wait"/> true = not yet, for a reason of the caller's.</summary>
    bool Gate(PicGate g, int key, DateTime now, Action? start, Func<bool>? wait, Action shoot, bool front = true)
    {
        if (g.Key != key)
        {
            g.Key = key; g.Ready = false; g.Begin = now; g.Hidden = DateTime.MinValue;
            start?.Invoke();
            if (_front && front) Native.Front(_main);
        }
        if (g.Ready) return true;
        if (wait?.Invoke() == true) return false;
        if (now - g.Begin < _delay) return false;
        if (g.Hidden == DateTime.MinValue) { g.Hidden = now; HideReplay(); }
        if (_front && now - g.Hidden < HideWait) return false;
        if (_idleAt < g.Begin && now - g.Begin < _delay + IdleWait) return false;   // AutoCAD has not been idle since the picture was asked for
        shoot();
        ShowReplay();
        g.Ready = true;
        return true;
    }

    /// <summary>The replayer started: <paramref name="setReplayVisible"/> hides / shows its window; <paramref name="main"/> is AutoCAD's main window.</summary>
    public void Begin(Action<bool> setReplayVisible, IntPtr main)
    {
        _setReplayVisible = setReplayVisible; _main = main; if (main != IntPtr.Zero) ScreenSpace.Reference = main;
        AcApp.Idle += OnIdle;
    }

    void OnIdle(object? s, EventArgs e) => _idleAt = DateTime.Now;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Log.Safe("tdoc idle unhook", () => AcApp.Idle -= OnIdle);
        Log.Safe("tdoc drop untyped steps", DropUntypedContinuations);
    }

    /// <summary>The answers after a dialog that the replay never typed (the dialog failed or was cancelled, the command ended) are not a step of the document: the others are numbered again.</summary>
    void DropUntypedContinuations()
    {
        foreach (var c in _after.Values.Where(c => !Typed(c)).OrderByDescending(c => c.Doc))
            if (c.Doc < Test.Steps.Count && Test.Steps[c.Doc].Pictures.Count == 0) Test.Steps.RemoveAt(c.Doc);
        for (int k = 0; k < Test.Steps.Count; k++) Test.Steps[k].Number = k + 1;
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
            _stepGate.Key = i; _stepGate.Ready = true;
            return true;
        }
    }

    bool BeforeStepCore(int i, Step s, DateTime now)
    {
        // a dialog is pictured by DialogReady; the answers given after the dialog before it (when this is the command's next dialog) are pictured first
        if (s.Kind == "dialog") return i == 0 || AfterDialog(i - 1, last: false, now);
        s = _before[i];
        return Gate(_stepGate, i, now,
            start: () =>
            {
                _zoom = null;
                if (s.Kind == "ribbon") Replayer.ActivateTab(s.Tab);   // the button must show
                if (s.Kind == "mark" && i == _steps.Count - 1 && Replayer.Current?.PostTestZoomNow() == true) { }   // the test ends on this checkpoint: setting postTestZoom (not "none") sets the view for its picture
                else if (s.Kind == "mark" && Replayer.Current?.RecordZoomOn == true) { }   // setting recordZoom (not "none"): the view follows each command that made objects; the checkpoint picture shows it
                else if (s.Kind == "mark") ZoomExtents();   // the checkpoint picture shows the whole drawing (the view only; the checkpoint compares objects)
            },
            wait: () => _zoom is { Pending: true },   // SDI: the ZOOM is typed a moment after it was sent; the idle check would not see it yet
            shoot: () => Shoot(_m[i], s));
    }

    /// <summary>ZOOM Extents in the active drawing, typed as a command: AutoCAD is quiescent here (the step has settled), and the step's delay
    /// lets it finish before the picture.</summary>
    SdiDocs.SdiSend? _zoom;   // the last ZOOM sent (pending under SDI until it is typed)

    void ZoomExtents()
    {
        try { if (Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument is { } d) _zoom = SdiDocs.Send(d, "_.ZOOM _E ", true, false, false, single: Single); }
        catch (Exception ex) { Log.Error("tdoc zoom extents", ex); }
    }

    /// <summary><paramref name="i"/>: the step's place in the document.</summary>
    void Shoot(int i, Step s, bool continuation = false)
    {
        if (s.Kind == "palette" && !continuation) { ShootPalette(i, s); return; }
        // smart zoom: a ribbon click shows the button, its panel and the tab row, 1:1; the pictures of the drawing (commands, checkpoints, prompts) stay the whole window
        var crop = _smart && !continuation && s.Kind == "ribbon" ? SmartShots.RibbonPicture(s.ItemId, null, null, _main, workArea: true) : null;
        var w = crop != null ? (crop, crop.Left, crop.Top) : ShotService.CaptureWindow(_main, workArea: true);
        if (w == null) { Log.Info($"tdoc: no picture of step {i + 1} (AutoCAD's window is hidden or minimized)"); return; }
        var targets = new List<(string Text, ScreenRect? Rect)>();
        ScreenRect? arrowAt = null;
        if (_arrows && !continuation && s.Kind == "ribbon" && !string.IsNullOrEmpty(s.ItemId)) arrowAt = TdocTargets.RibbonItemRect(s.ItemId)?.Rect;
        if (_annotate)
            foreach (var p in TdocPlan.StepCallouts(s, continuation))
            {
                if (p.Role == CalloutRole.RibbonItem)
                {
                    var hit = string.IsNullOrEmpty(s.ItemId) ? null : TdocTargets.RibbonItemRect(s.ItemId);
                    // the button sits in a collapsed panel: the box is on the panel, and the legend says to open it first
                    targets.Add((hit is { Panel: true } ? $"Open the {(string.IsNullOrWhiteSpace(s.Panel) ? "collapsed" : s.Panel.Trim())} panel, then {char.ToLowerInvariant(p.Text[0])}{p.Text[1..]}" : p.Text, hit?.Rect));
                }
                else targets.Add((p.Text, p.Role == CalloutRole.CommandLine ? TdocTargets.CommandLineRect(_main) : null));
            }
        Save(i, w.Value.Pic, w.Value.Left, w.Value.Top, targets, TdocPlan.Caption(s), arrowAt is { } ra ? [new ArrowAt(ra, false, _annotate ? 0 : -1)] : null, keepSize: crop != null);
    }

    void ShootPalette(int i, Step s)
    {
        var page = PaletteFill.FindPage(s);
        // an open palette: its own frame; hidden (auto-hide) or not found: AutoCAD's window, without boxes
        if (page != IntPtr.Zero && SmartShots.PaletteArea(page, _smart) is { } r && ShotService.CaptureRect(r.L, r.T, r.R, r.B, page) is { } pic)
        {
            var targets = new List<(string Text, ScreenRect? Rect)>();
            var arrows = new List<ArrowAt>();
            if (_annotate || _arrows)
            {
                var boxes = PaletteFill.TdocBoxes(s);
                foreach (var b in boxes)
                {
                    if (_annotate) targets.Add((b.Text, TdocTargets.Of(b)));
                    if (_arrows && b.Arrow && TdocTargets.Of(b) is { } ir) arrows.Add(new ArrowAt(ir, true, _annotate ? targets.Count - 1 : -1));   // the item a tree / list selection is on
                }
                // the button the step presses, found the way the replay finds it (a WPF button, a button taken by position too); a step without a button, the first control it fills; a button that was not found gets no arrow
                bool byButton = !string.IsNullOrEmpty(s.Button) || Retrace.Core.PaletteClickKinds.IsIntermediate(s.ClickKind);
                if (_arrows && (byButton ? PaletteFill.TdocButtonRect(s) : boxes.Count > 0 && !boxes[0].Arrow ? TdocTargets.Of(boxes[0]) : null) is { } pr)
                {
                    // the box of the control the arrow points at (the button's, which TdocBoxes adds last; else the first field's): its own box and number tag are not in the arrow's way
                    int own = !_annotate ? -1 : byButton ? (boxes.Count > 0 && boxes[^1].Text == "Click " + s.Button ? boxes.Count - 1 : -1) : 0;
                    arrows.Add(new ArrowAt(pr, false, own));
                }
            }
            Save(i, pic, r.L, r.T, targets, TdocPlan.Caption(s), arrows, keepSize: _smart);
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
            // rectangles that were read (UI Automation, WPF, a toolbar button) move with the dialog: by how far it is from where it was when each one was read (a box has its own place, a button pressed
            // earlier was read before this gate's move; a shot made after the move was read in the new place already), not by the move this gate made
            var dlgNow = TdocTargets.Of(shot.Dialog);
            ScreenRect? Rect(DialogBox b)
            {
                var was = b.At ?? shot.DialogAt;
                return was is { } w0 && dlgNow is { } n0 ? TdocTargets.Of(b, n0.Left - w0.Left, n0.Top - w0.Top) : TdocTargets.Of(b, _dx, _dy);
            }
            if (_annotate)
            {
                foreach (var b in shot.Filled) targets.Add((b.Text, Rect(b)));
                if (shot.Close != null) targets.Add((shot.Close.Text, Rect(shot.Close)));
            }
            // an arrow on each tree item / list item that was selected, and on the button that closes the dialog (no arrow when it was not found: never a guess), else on the last field it fills; a rectangle that is not a control of the dialog (the dialog itself, its title bar) gets none
            var arrows = new List<ArrowAt>();
            if (_arrows)
            {
                var client = TdocTargets.ClientOf(shot.Dialog);
                bool Control(ScreenRect pr) => client is { } cr && TdocGeometry.IsControlOf(pr.Left, pr.Top, pr.Right, pr.Bottom, cr.Left, cr.Top, cr.Right, cr.Bottom);
                int index = 0;   // the box's place among the targets (the legend's order), when boxes are drawn
                foreach (var b in shot.Filled)
                {
                    if (b.Arrow && Rect(b) is { } ir && Control(ir)) arrows.Add(new ArrowAt(ir, true, _annotate ? index : -1));
                    index++;
                }
                var pick = shot.Close != null ? Rect(shot.Close) : shot.Filled.Count > 0 && !shot.Filled[^1].Arrow ? Rect(shot.Filled[^1]) : null;
                if (pick is { } pk && Control(pk)) arrows.Add(new ArrowAt(pk, false, !_annotate ? -1 : shot.Close != null ? shot.Filled.Count : shot.Filled.Count - 1));
            }
            Save(_m[i], w.Value.Pic, w.Value.Left, w.Value.Top, targets, TdocPlan.Caption(_steps[i]), arrows);
            // the instruction names the button as it reads ("Click OK." for a LISP alert, whose only button has Cancel's id)
            if (shot.Close != null && Test.Steps[_m[i]].Instructions is { Count: > 0 } ins)
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
            if (s.Kind != "mark" || i < 0 || i >= _m.Length || result.Checkpoints.Count == 0) return;
            var cp = result.Checkpoints[^1];
            Test.Steps[_m[i]].Checkpoint = new TrainingCheckpoint { Index = cp.Index, Note = cp.Note, Pass = cp.Pass, Differences = [.. cp.Differences] };
        }
        catch (Exception ex) { Log.Error("tdoc checkpoint", ex); }
    }

    // ---- the picture ----------------------------------------------------------------------------------------------------------------

    /// <summary>Adds the picture to the step and writes it on a pool thread. <paramref name="targets"/>: the legend lines in the order they are
    /// numbered, each with its rectangle on the screen (null = not found; the line is left out). The picture shows the screen area at
    /// (<paramref name="sx"/>, <paramref name="sy"/>) the size of <paramref name="pic"/>, scaled down to fit.</summary>
    void Save(int i, ShotService.Picture pic, int sx, int sy, IReadOnlyList<(string Text, ScreenRect? Rect)> targets, string? caption, IReadOnlyList<ArrowAt>? arrowsAt = null, bool keepSize = false)
    {
        var (nw, nh) = keepSize ? (pic.Width, pic.Height) : ShotImage.FitSize(pic.Width, pic.Height);   // smart zoom: a 1:1 crop is never scaled
        // rectangles are in the process's logical space like sx, sy and the picture's source size; the pixels are the screen's own (ScreenSpace). A rectangle that does not show in the picture gets no frame; one that sticks out is clipped (MarkGuard).
        int sw = pic.SrcWidth, sh = pic.SrcHeight;
        var shown = new PxRect(sx, sy, sx + sw, sy + sh);
        var candidates = targets.Select(t => t.Rect is { } r && MarkGuard.Visible(shown, new PxRect(r.Left, r.Top, r.Right, r.Bottom)) is not null ? Annotate.ToPicture(r.Left, r.Top, r.Right, r.Bottom, sx, sy, sw, sh, nw, nh, 1) : null).ToList();
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
        // the green click arrows (setting showClickArrows), drawn into the image on the middle of the control (a button) or just inside the edge of the item (a tree item, a list item), translated to the picture's pixels and
        // placed so that they do not cover the other boxes, their numbers or each other
        List<ArrowLine> arrows = [];
        if (_arrows && arrowsAt is { Count: > 0 })
        {
            var src = shown;
            var rects = new List<PxRect>();   // every box and number tag of the picture: what an arrow keeps off
            var own = new Dictionary<int, List<PxRect>>();
            foreach (var n in numbered)
            {
                var c = candidates[n.Index]!.Value with { Number = n.Number };
                var r = new PxRect(c.Left, c.Top, c.Left + c.Width, c.Top + c.Height);
                rects.Add(r);
                var tag = Annotate.TagArea(c, nw, nh);
                if (tag.Width > 0) rects.Add(tag);
                own[n.Index] = [r, tag];
            }
            var list = new List<ClickArrow.ArrowTarget>();
            foreach (var a in arrowsAt)
            {
                if (MarkGuard.Visible(shown, new PxRect(a.Rect.Left, a.Rect.Top, a.Rect.Right, a.Rect.Bottom)) is null) continue;   // an arrow only on a rectangle that shows in the picture (clipped by ToPicture)
                var ip = Annotate.ToPicture(a.Rect.Left, a.Rect.Top, a.Rect.Right, a.Rect.Bottom, sx, sy, sw, sh, nw, nh, 1);
                if (ip is not { } b) continue;
                var pr = new PxRect(b.Left, b.Top, b.Left + b.Width, b.Top + b.Height);
                // an arrow's own box and tag may be run over by it, except the tag of an item arrow (it ends on the box's edge, next to the tag)
                var mine = a.Target >= 0 && own.TryGetValue(a.Target, out var o) ? (a.AtEdge ? o.Take(1).ToList() : o) : null;
                list.Add(new ClickArrow.ArrowTarget(pr, a.AtEdge, mine));
            }
            arrows = ClickArrow.Place(list, rects, nw, nh, ClickArrow.ImageLength(src, nw, nh));
        }
        var task = Task.Run(() =>
        {
            try
            {
                var data = nw == w && nh == h ? px : ShotImage.Scale(px, w, h, nw, nh);
                Annotate.Draw(data, nw, nh, boxes);
                foreach (var a in arrows) Annotate.DrawArrow(data, nw, nh, a);
                WritePng(path, nw, nh, data);
            }
            catch (Exception ex) { Log.Error("tdoc picture", ex); }
        });
        lock (_tasks) { _tasks.RemoveAll(x => x.IsCompleted); _tasks.Add(task); }
    }

    /// <summary>An arrow wanted in a picture: the rectangle it ends on (screen pixels), whether it ends at the rectangle's edge (an item) or on its middle (a button), and which legend target's box it belongs to (-1: none).</summary>
    readonly record struct ArrowAt(ScreenRect Rect, bool AtEdge = false, int Target = -1);

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
