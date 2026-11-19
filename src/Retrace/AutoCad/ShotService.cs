using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Retrace.AutoCad;

/// <summary>
/// Takes the screenshots of a recording. Event handlers only call <see cref="Request"/> (data work: no AutoCAD API, no capture).
/// <see cref="Pump"/> runs from a dispatcher timer and from Application.Idle, after the UI has had time to paint, and does the
/// BitBlt there; PNG encoding and the `shot` event happen on a pool thread. Every failure goes to retrace.log and is otherwise ignored.
/// </summary>
sealed class ShotService
{
    /// <summary>Pixels of a capture: 32-bit BGRA, top-down.</summary>
    public sealed record Picture(byte[] Pixels, int Width, int Height)
    {
        /// <summary>The screen point of the picture's top-left corner, when it is a capture of a screen rectangle (<see cref="HasOrigin"/>).</summary>
        public int Left { get; init; }
        public int Top { get; init; }
        public bool HasOrigin { get; init; }
        /// <summary>The size of that screen rectangle in the process's own (logical) coordinates; 0 = the pixel size. The picture's pixels are physical, so on a scaled screen it is smaller than <see cref="Picture.Width"/> x <see cref="Picture.Height"/>
        /// (<see cref="ScreenSpace"/>); every rectangle and point is mapped from this size to the pixels.</summary>
        public int SrcW { get; init; }
        public int SrcH { get; init; }
        public int SrcWidth => SrcW > 0 ? SrcW : Width;
        public int SrcHeight => SrcH > 0 ? SrcH : Height;
    }

    // ---- click arrows in step pictures (setting showClickArrows) ------------------------------------------------------------
    readonly Dictionary<int, (int X, int Y)> _clicks = new();
    readonly Dictionary<int, Core.PxRect> _rects = new();
    readonly HashSet<int> _suppressed = new();
    readonly HashSet<int> _boxOnly = new();   // pictures whose rectangle is a "look here" box: no arrow (a palette that opened)
    readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _openedState = new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _opened = new();   // event n -> name of the palette its end picture shows opening (written on the shot event)
    readonly PictureFallback _fallback = new();
    readonly Dictionary<int, Picture> _held = new();
    bool _arrows;

    /// <summary>Step pictures get a green rectangle and arrow where the click was (setting <c>testDocArrows</c>): drawn into the saved image, never captured from the live overlay.</summary>
    public bool Arrows { get => _arrows; set { _arrows = value; _clicks.Clear(); _rects.Clear(); _boxOnly.Clear(); } }

    /// <summary>The palette that opened during the command whose end picture is event <paramref name="n"/>, or null (asked when that picture is taken: the palette must be on screen then). Set by the recorder; the
    /// reason for "none" is its to log.</summary>
    public Func<int, Core.PaletteOpened?>? OpenedProbe { get; set; }

    /// <summary>Asked right before the end picture is grabbed for a rolled-up or anchored palette: false when the set shows expanded at that moment (the mouse reached it): the box and caption are dropped, never a strip boxed over an expanded palette.</summary>
    public Func<string, string, bool>? OpenedRecheck { get; set; }

    /// <summary>Asked after an end picture's probe found no opened palette: true while the recorder watches for a palette that shows up a moment later. The picture is then written but its `shot` event is held
    /// (<see cref="ReleaseShot"/>) until the watch ends, so a retake replaces the picture before anything is announced and the recording holds one `shot` event for it.</summary>
    public Func<int, bool>? LateWatching { get; set; }

    /// <summary>Asked when a picture is written for a palette click that carries an extra box (<see cref="NoteExtraBox"/>): is that palette still on screen at that rectangle? False: no extra box.</summary>
    public Func<string, Core.PxRect, bool>? ExtraRecheck { get; set; }

    readonly Core.EndShotGate _gate = new();                // which end pictures' `shot` events wait for the watch, and when each is written (Core, tested)
    readonly Dictionary<int, string> _heldKind = new();     // their picture kinds (file name part); UI thread only
    readonly Dictionary<int, Task> _endWrite = new();       // the latest write task of a held end picture: a retake writes after it, never beside it; UI thread only

    /// <summary>The late-palette watch of end picture <paramref name="n"/> is over: its `shot` event is written once the pictures being written have finished (with `opened` only when the retaken picture was written).</summary>
    public void ReleaseShot(int n)
    {
        _heldKind.Remove(n); _endWrite.Remove(n);
        Settle(_gate.Release(n));
    }

    /// <summary>Every held `shot` event is written (the recording is stopping).</summary>
    public void ReleaseAllShots()
    {
        foreach (var n in _gate.HeldNumbers()) ReleaseShot(n);
    }

    /// <summary>Writes the `shot` event the gate says is due (pool or UI thread). Without `opened` (the retake failed or never happened) the palette name is forgotten, so the event does not carry it.</summary>
    void Settle(Core.ShotAnnounce? due)
    {
        if (due is not { } s) return;
        if (!s.Opened) { _opened.TryRemove(s.N, out _); _openedState.TryRemove(s.N, out _); }
        var onShot = _onShot;
        onShot(s.N, s.Rel, s.What);
    }

    /// <summary>A palette appeared after the command ended: the end picture of event <paramref name="n"/> is taken again (the whole window, the same file) with the box around the palette; its `shot` event, released after this, carries `opened`
    /// when this picture was written. False (nothing changed) when the picture cannot be taken or the palette is not on screen as it is going to be boxed.</summary>
    public bool Retake(int n, Core.PaletteOpened opened, IntPtr palettePage)
    {
        if (!_gate.Holds(n) || !_heldKind.TryGetValue(n, out var kind)) return false;
        if (!_enabled || _main == IntPtr.Zero || Win.IsIconic(_main) || !Win.IsWindowVisible(_main)) return false;
        if (opened.State.Length > 0 && OpenedRecheck != null && !OpenedRecheck(opened.Name, opened.State)) return false;
        Repaint(palettePage);
        _opened[n] = opened.Name; if (opened.State.Length > 0) _openedState[n] = opened.State; else _openedState.TryRemove(n, out _);
        NoteBoxOnly(n, opened.Rect);
        _retakeStarted = false;
        Grab(n, kind, _main, "window", announce: false, retake: true);
        if (!_retakeStarted) { _opened.TryRemove(n, out _); _openedState.TryRemove(n, out _); _rects.Remove(n); _boxOnly.Remove(n); }
        return _retakeStarted;
    }
    bool _retakeStarted;   // UI thread: the last Grab with retake began a write

    /// <summary>A green box with no arrow around the palette <paramref name="name"/> (<paramref name="r"/>) on the picture of event <paramref name="n"/> too, in addition to that picture's own marks: the click that is the first input after
    /// a palette appeared late. Drawn only when the palette is still there and the rectangle is inside the picture.</summary>
    public void NoteExtraBox(int n, string name, Core.PxRect r) { if (_arrows && n > 0 && r.Width > 0 && r.Height > 0) _extra[n] = (name, r); }
    readonly Dictionary<int, (string Name, Core.PxRect Rect)> _extra = new();

    AnnotationBox? ExtraFor(int n, Core.PxRect src, int w, int h)
    {
        if (!_extra.Remove(n, out var e)) return null;
        if (!Core.MarkGuard.RectInside(src, e.Rect)) { Log.Info($"shot {n}: palette box left out: the {e.Name} palette {e.Rect} is not inside the picture {src}"); return null; }
        if ((long)e.Rect.Width * e.Rect.Height * 10 >= (long)src.Width * src.Height * 6) { Log.Info($"shot {n}: palette box left out: the picture is a close-up of the {e.Name} palette (the box would be about the whole picture)"); return null; }
        if (ExtraRecheck != null && !ExtraRecheck(e.Name, e.Rect)) { Log.Info($"shot {n}: palette box left out: the {e.Name} palette is not on screen at {e.Rect} now"); return null; }
        return Annotate.ToPicture(e.Rect.Left, e.Rect.Top, e.Rect.Right, e.Rect.Bottom, src.Left, src.Top, src.Width, src.Height, w, h, 0);
    }

    /// <summary>The name of the palette picture <paramref name="n"/> shows opening (the `opened` field of its `shot` event), or null. Callable from the pool thread.</summary>
    public string? OpenedName(int n) => _opened.TryGetValue(n, out var name) ? name : null;

    /// <summary>How that palette shows: "" = open, "rolled" = Auto-hide rolled up to its title strip, "anchored" = Auto-hide anchored to an edge of AutoCAD's window (the `opened.state` of the `shot` event).</summary>
    public string OpenedState(int n) => _openedState.TryGetValue(n, out var st) ? st : "";

    /// <summary>A green box with no arrow around <paramref name="r"/> for event <paramref name="n"/>'s picture ("look here", not "click here"), only while marks are on.</summary>
    void NoteBoxOnly(int n, Core.PxRect r)
    {
        if (!_arrows || n <= 0 || r.Width <= 0 || r.Height <= 0) return;
        _rects[n] = r; _clicks.Remove(n); _boxOnly.Add(n);
    }

    /// <summary>The screen rectangle of what the person clicked for event <paramref name="n"/> (an item's row, a button, the ribbon button): its picture gets a green frame around it and an arrow ending on it.
    /// Not noted when the rectangle is empty; a picture the rectangle is not in gets neither (never a guessed spot).</summary>
    public void NoteTarget(int n, int left, int top, int right, int bottom) { if (_arrows && n > 0 && right > left && bottom > top) _rects[n] = new Core.PxRect(left, top, right, bottom); }

    /// <summary>Classic: palette step <paramref name="n"/> gets its window picture from the scheduler (request due at <paramref name="requestDueMs"/>); <paramref name="held"/>, the palette's close-up from the press, is written
    /// instead only when that picture does not come (the request was dropped, or the window is minimized): a step always ends with one picture.</summary>
    public void ExpectWindowPicture(int n, long requestDueMs, Picture? held)
    {
        if (!_enabled || n <= 0 || held == null) return;
        _held[n] = held; _fallback.Expect(n, requestDueMs);
    }

    void PumpFallbacks(long nowMs, bool force)
    {
        if (_fallback.Pending == 0) return;
        foreach (var n in _fallback.Due(nowMs, force))
        {
            if (!_held.Remove(n, out var pic)) continue;
            Log.Info($"shot {n}: the window picture did not come (request dropped or AutoCAD's window not shown); the palette close-up taken at the click is written instead");
            Write(n, "palette-closeup", pic, "palette");
        }
    }

    /// <summary>The window picture the scheduler would take for event <paramref name="n"/> (the ribbon click's) is not needed: a picture was taken at the click itself.</summary>
    public void Suppress(int n) { if (n > 0) _suppressed.Add(n); }

    /// <summary>Smart zoom (setting screenshotMode): a palette click or palette values step gets its one picture from the palette's crop, so the window picture the scheduler would take for it is not needed
    /// (<see cref="Suppress"/>, only when the crop is certain to be written; the caller checks).</summary>
    public bool Smart { get; set; }

    /// <summary>The work area of the monitor a window is mostly on, as a logical rectangle; null when it cannot be read.</summary>
    public static Core.PxRect? WorkAreaOf(IntPtr hwnd) => hwnd != IntPtr.Zero && Win.WorkArea(hwnd) is { } w ? new Core.PxRect(w.Left, w.Top, w.Right, w.Bottom) : null;

    /// <summary>The visible frame of a window as a logical rectangle (the work area only with <paramref name="workArea"/>); null when it is gone, hidden or minimized.</summary>
    public static Core.PxRect? WindowRect(IntPtr hwnd, bool workArea = false)
    {
        try
        {
            if (hwnd == IntPtr.Zero || Win.IsIconic(hwnd) || !Win.IsWindowVisible(hwnd)) return null;
            var b = Win.Bounds(hwnd);
            if (workArea && Win.WorkArea(hwnd) is { } wa)
                b = new Win.RECT { Left = Math.Max(b.Left, wa.Left), Top = Math.Max(b.Top, wa.Top), Right = Math.Min(b.Right, wa.Right), Bottom = Math.Min(b.Bottom, wa.Bottom) };
            return b.Width > 0 && b.Height > 0 ? new Core.PxRect(b.Left, b.Top, b.Right, b.Bottom) : null;
        }
        catch (Exception ex) { Log.Error("window rect", ex); return null; }
    }

    /// <summary>The frame and the arrow for event <paramref name="n"/>'s picture of the screen rectangle <paramref name="src"/> saved at <paramref name="w"/> x <paramref name="h"/>: around its noted rectangle, else
    /// the arrow at its noted point.</summary>
    (AnnotationBox? Box, Func<byte[], ArrowLine?>? Arrow) MarksFor(int n, Core.PxRect src, int w, int h)
    {
        if (!_arrows) return (null, null);
        _clicks.TryGetValue(n, out var click);
        bool haveClick = _clicks.ContainsKey(n);
        var plan = Core.PictureMarks.Choose(_boxOnly.Contains(n), _rects.ContainsKey(n), haveClick);   // the one rule of what a picture gets (Core, tested)
        if (plan == Core.PictureMark.None) return (null, null);
        if (_rects.TryGetValue(n, out var r))
        {
            // never a mark that is not inside the picture and on the click (a rectangle read in another coordinate space, a stale one)
            if (!Core.MarkGuard.Accept(src, r, haveClick ? click : null, reach: Core.MarkGuard.RowReach)) { Log.Info($"shot {n}: green box and arrow left out: the clicked rectangle {r} is not inside the picture {src} (or not on the click point)"); return (null, null); }
            var box = Annotate.ToPicture(r.Left, r.Top, r.Right, r.Bottom, src.Left, src.Top, src.Width, src.Height, w, h, 0);
            if (box == null) { Log.Info($"shot {n}: green box and arrow left out: the rectangle {r} does not map into the picture {src}"); return (null, null); }
            var pb = new Core.PxRect(box.Value.Left, box.Value.Top, box.Value.Left + box.Value.Width, box.Value.Top + box.Value.Height);
            if (plan == Core.PictureMark.Box) return (box, null);   // a palette that opened: the box alone says "look here"
            return (box, px => ClickArrow.ForBoxQuiet(pb, src, w, h, px));   // ends at the rectangle's corner, from the side of the picture that is least busy there
        }
        if (haveClick && !Core.MarkGuard.Accept(src, null, click)) { Log.Info($"shot {n}: green arrow left out: the click point {click} is outside the picture {src}"); return (null, null); }
        var point = ArrowFor(n, src, w, h);
        return (null, point == null ? null : _ => point);
    }

    // ---- pictures taken after the click has settled -------------------------------------------------------------------------
    sealed record Deferred(int N, string Kind, string What, long DueMs, Func<(int L, int T, int R, int B)?> Area, Picture? Held, Func<(int L, int T, int R, int B)?>? Reread = null, IntPtr Window = default);
    readonly List<Deferred> _deferred = new();

    /// <summary>A picture of a screen area for event <paramref name="n"/>, taken when <paramref name="dueMs"/> has come (the click's reaction has been painted by then), or at once on <see cref="FlushDeferred"/>.
    /// <paramref name="area"/> is asked then: null (the window is gone or something is over it) means <paramref name="held"/>, the picture taken when the click was pressed, is written instead; no held picture, none written.</summary>
    public void DeferRect(int n, string kind, string what, long dueMs, Func<(int L, int T, int R, int B)?> area, Picture? held, Func<(int L, int T, int R, int B)?>? reread = null, IntPtr window = default)
    {
        if (_enabled && n > 0) _deferred.Add(new Deferred(n, kind, what, dueMs, area, held, reread, window));
    }

    /// <summary>The dialog window's picture of a click, as <see cref="DeferRect"/>; <paramref name="held"/> from <see cref="CaptureDialogNow"/>. Settled means the dialog still exists, shows and is enabled (no window over it).</summary>
    public void DeferDialog(int n, IntPtr dialog, long dueMs, Picture? held, Func<(int L, int T, int R, int B)?>? reread = null) =>
        DeferRect(n, "dialog-click", "dialog-click", dueMs, () =>
        {
            if (!Native.Exists(dialog) || !Win.IsWindowVisible(dialog) || Win.IsIconic(dialog) || !Native.IsEnabled(dialog)) return null;
            var b = Win.Bounds(dialog);
            return b.Width > 0 && b.Height > 0 ? (b.Left, b.Top, b.Right, b.Bottom) : null;
        }, held, reread, dialog);

    /// <summary>A picture of the dialog window as it is now (the press-time picture of a button that may close it); null when it cannot be taken. UI thread.</summary>
    public Picture? CaptureDialogNow(IntPtr dialog) => _enabled ? CaptureWindow(dialog)?.Pic : null;

    /// <summary>Takes the pictures that are due (<paramref name="force"/>: all of them now).</summary>
    void PumpDeferred(long nowMs, bool force)
    {
        if (_deferred.Count == 0) return;
        foreach (var d in _deferred.Where(x => force || x.DueMs <= nowMs).ToList())
        {
            _deferred.Remove(d);
            try
            {
                Picture? pic = null;
                if (d.Area() is { } a)
                {
                    pic = CaptureRect(a.L, a.T, a.R, a.B, d.Window);   // the window's own monitor decides the scale (ScreenSpace)
                    // the click may have scrolled or opened the item: where it is now (the press-time rectangle stays when it cannot be read)
                    if (pic != null && d.Reread != null)
                    {
                        if (d.Reread() is { } rr) NoteTarget(d.N, rr.L, rr.T, rr.R, rr.B);
                        else _rects.Remove(d.N);   // the item cannot be confirmed where it was (scrolled away, gone, another item now): no frame and no arrow rather than a wrong one
                    }
                }
                pic ??= d.Held;
                if (pic != null) Write(d.N, d.Kind, pic, d.What);
                else if (d.Kind == "palette-closeup" && ShotRules.NeedsWindowFallback(_suppressed.Contains(d.N), false) && CaptureWindow(_main, workArea: true) is { } win)
                    { Log.Info($"shot {d.N}: no palette close-up could be taken (collapsed, hidden or covered), the window picture is taken instead"); Write(d.N, "palette", win.Pic, "window"); }   // smart zoom: the window picture that was left out for this step
            }
            catch (Exception ex) { Log.Error($"shot {d.N} {d.Kind}", ex); }
        }
    }

    /// <summary>Takes the deferred pictures now: another click is about to be handled (the dialog shows what the click before it did), or the dialog is about to close.</summary>
    public void FlushDeferred() => PumpDeferred(long.MaxValue, true);

    /// <summary>The screen point where the person clicked for event <paramref name="n"/>: its picture points at it.</summary>
    public void NoteClick(int n, int x, int y) { if (_arrows && n > 0) _clicks[n] = (x, y); }

    /// <summary>The arrow for event <paramref name="n"/>'s picture of the screen rectangle (<paramref name="src"/>) saved at <paramref name="w"/> x <paramref name="h"/>, or null.</summary>
    ArrowLine? ArrowFor(int n, Core.PxRect src, int w, int h) =>
        _arrows && _clicks.TryGetValue(n, out var c) && src.Contains(c.X, c.Y) ? ClickArrow.ForImage(c.X, c.Y, src, w, h) : null;   // a click outside what the picture shows has no place in it

    /// <summary>The closing input of event <paramref name="n"/> was not a mouse click: an earlier click's point is no longer its.</summary>
    public void ForgetClick(int n) => _clicks.Remove(n);

    readonly ShotScheduler _sched = new();
    readonly List<Task> _tasks = new();
    string _folder = "";
    string _sessionPath = "";
    bool _enabled;
    IntPtr _main;
    Func<bool> _modalOpen = () => false;
    Action<int, string, string> _onShot = (_, _, _) => { };

    public bool Enabled => _enabled;

    /// <param name="sessionPath">the recording's path; pictures go to `screenshots\` next to a recording.jsonl</param>
    /// <param name="onShot">called on a pool thread after the file is written: (n, relative file path, "window" | "dialog")</param>
    public void Begin(string sessionPath, bool enabled, IntPtr mainWindow, Func<bool> modalOpen, Action<int, string, string> onShot)
    {
        _sched.Clear(); _blankLogged.Clear();
        _tasks.Clear();
        _afterTaken.Clear();
        _clicks.Clear(); _rects.Clear(); _suppressed.Clear(); _deferred.Clear(); _fallback.Clear(); _held.Clear(); _boxOnly.Clear(); _opened.Clear(); _openedState.Clear(); _extra.Clear();
        _gate.Clear(); _heldKind.Clear(); _endWrite.Clear();
        _sessionPath = sessionPath;
        _folder = Path.Combine(Path.GetDirectoryName(sessionPath) ?? "", ShotNaming.FolderFor(sessionPath));
        _enabled = enabled;
        _main = mainWindow;
        if (mainWindow != IntPtr.Zero) ScreenSpace.Reference = mainWindow;
        _modalOpen = modalOpen;
        _onShot = onShot;
    }

    public void SetMainWindow(IntPtr h) { if (_main == IntPtr.Zero) _main = h; if (h != IntPtr.Zero) ScreenSpace.Reference = h; }
    public bool NeedsMainWindow => _enabled && _main == IntPtr.Zero;

    public void Request(ShotRequest? r)
    {
        if (_enabled && r != null) _sched.Enqueue(r);
    }

    /// <summary>A click in a palette is about to be handled: the window pictures of the palette steps before it are taken now, so each shows its own click's result and none is taken (late, after a stall of the UI
    /// thread) with the next click's. Only requests of the "palette" kind, and only when one is queued (a list look otherwise: nothing is painted, nothing is captured). The due time is not waited for and the gap rule
    /// does not apply: every queued picture is of its own click. The click arrives before the WM_PAINT of what the stalled handler changed, so the main window and the palette's window are painted first
    /// (RedrawWindow UPDATENOW: only what is invalid); without it the picture showed the label of the click before.</summary>
    public void FlushPaletteWindows(long nowMs, IntPtr palettePage = default)
    {
        if (!_enabled || !_sched.HasPending("palette")) return;
        List<ShotRequest> ready;
        try { ready = _sched.TakeReady(nowMs, early: true, mayTake: r => r.Kind == "palette" && !(Hold?.Invoke() ?? false)); }
        catch (Exception ex) { Log.Error("shot flush", ex); return; }
        if (ready.Count == 0) return;
        Repaint(palettePage);
        foreach (var r in ready)
        {
            try { Capture(r); }
            catch (Exception ex) { Log.Error($"shot {r.N} {r.Kind}", ex); }
        }
    }

    /// <summary>A picture of a palette is queued or held for its click (a window picture or a close-up).</summary>
    public bool HasQueuedPaletteShots => _enabled && (_deferred.Count > 0 || _sched.HasPending("palette"));

    /// <summary>Paints what is invalid in the main window and in the palette's window now (RedrawWindow UPDATENOW). A click reaches PreTranslateMessage before the WM_PAINT of what the stalled handler before it changed, so the
    /// pictures of the click before (close-up and window) are taken after this, or they show the old label.</summary>
    public void Repaint(IntPtr palettePage = default)
    {
        try
        {
            const uint UPDATENOW = 0x0100, ALLCHILDREN = 0x0080, GA_ROOT = 2;
            if (_main != IntPtr.Zero) RedrawWindow(_main, IntPtr.Zero, IntPtr.Zero, UPDATENOW | ALLCHILDREN);
            if (palettePage != IntPtr.Zero && GetAncestor(palettePage, GA_ROOT) is { } root && root != IntPtr.Zero && root != _main) RedrawWindow(root, IntPtr.Zero, IntPtr.Zero, UPDATENOW | ALLCHILDREN);
        }
        catch (Exception ex) { Log.Error("shot repaint", ex); }
    }

    [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr region, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    /// <summary>Fulfils what is due. <paramref name="force"/>: everything pending now (recording is stopping).</summary>
    public void Pump(long nowMs, bool force = false)
    {
        if (_enabled) PumpDeferred(nowMs, force);
        if (_enabled && _sched.Pending == 0) PumpFallbacks(nowMs, force);
        if (!_enabled || _sched.Pending == 0) return;
        List<ShotRequest> ready;
        try { ready = _sched.TakeReady(nowMs, force, Hold == null ? null : _ => !Hold()); }
        catch (Exception ex) { Log.Error("shot schedule", ex); return; }
        foreach (var r in ready)
        {
            try { if (!Capture(r, force)) _sched.Untake(r, nowMs); }   // not painted yet: asked again (until the request is too old)
            catch (Exception ex) { Log.Error($"shot {r.N} {r.Kind}", ex); }
        }
        PumpFallbacks(nowMs, force);
    }

    /// <summary>True while a dialog that may be a flash (see <see cref="FlashDialog"/>) is on screen: no picture is taken then (not of the dialog, not of the window, which would show it).</summary>
    public Func<bool>? Hold { get; set; }
    readonly HashSet<int> _blankLogged = new();

    /// <summary>Takes the queued dialog pictures of events <paramref name="ns"/> out of the queue (the dialog was a flash).</summary>
    public int CancelDialog(params long[] ns) => _sched.Cancel(ShotTarget.Dialog, ns);

    /// <summary>false: the dialog has not been painted yet, ask again later.</summary>
    bool Capture(ShotRequest r, bool force = false)
    {
        IntPtr hwnd;
        string what;
        if (ShotRules.IsEndPicture(r.Kind) && r.Target == ShotTarget.Window && OpenedProbe != null)
        {
            // the end picture is always the whole AutoCAD window (never a crop, classic or smart); when a palette opened during the command it gets a box and the document a caption
            try
            {
                if (OpenedProbe(r.N) is { } opened) { _opened[r.N] = opened.Name; if (opened.State.Length > 0) _openedState[r.N] = opened.State; NoteBoxOnly(r.N, opened.Rect); }
                else if (LateWatching?.Invoke(r.N) == true) { _gate.Hold(r.N); _heldKind[r.N] = r.Kind; }
            }
            catch (Exception ex) { Log.Error($"shot {r.N} palette opened", ex); }
        }
        if (_openedState.TryGetValue(r.N, out var openedState) && _opened.TryGetValue(r.N, out var openedName) && OpenedRecheck != null && !OpenedRecheck(openedName, openedState))
        {
            Log.Info($"end picture {r.N}: the {openedName} palette shows expanded at the capture, not as a {openedState} strip: no box, no caption");
            _opened.TryRemove(r.N, out _); _openedState.TryRemove(r.N, out _); _rects.Remove(r.N); _boxOnly.Remove(r.N);
        }
        if (r.Kind is "ribbon" or "palette" && _suppressed.Contains(r.N)) return true;   // the picture was taken when the button was clicked (ribbon), or is the palette's own crop (smart zoom)
        if (r.Target == ShotTarget.Dialog)
        {
            if (!_modalOpen()) return true;                       // the dialog is already gone: the screen shows something else
            // the dialog the request is for (a nested dialog that opens meanwhile is not its picture); a request without a window takes the one in front, as before
            hwnd = r.Window != 0 ? (IntPtr)r.Window : Native.ForegroundOfThisProcess();
            if (hwnd == IntPtr.Zero || hwnd == _main) return true; // no dialog window of AutoCAD in front
            if (r.Window != 0 && (!Win.IsWindowVisible(hwnd) || !Native.Exists(hwnd))) return true;   // its window is gone
            what = "dialog";
        }
        else
        {
            hwnd = _main;
            if (hwnd == IntPtr.Zero || Win.IsIconic(hwnd) || !Win.IsWindowVisible(hwnd)) return true;
            what = "window";
        }
        return Grab(r.N, r.Kind, hwnd, what, skipBlank: what == "dialog" && !force);
    }

    readonly HashSet<int> _afterTaken = new();

    /// <summary>The "after" picture of dialog <paramref name="n"/> (its modal_title / modal_enter event), taken now: the person just pressed OK or
    /// Enter and the dialog still shows the values. Another OK (a validation message sent them back) overwrites the file; one `shot` event.</summary>
    public void DialogAfter(int n, IntPtr hwnd)
    {
        if (!_enabled || n <= 0 || hwnd == IntPtr.Zero || !Win.IsWindowVisible(hwnd)) return;
        try { Grab(n, "dialog-after", hwnd, "dialog-after", announce: _afterTaken.Add(n)); }
        catch (Exception ex) { Log.Error($"shot {n} dialog-after", ex); }
    }

    /// <summary>A picture of a screen rectangle (device pixels), taken now; null if it is empty or the capture fails. UI thread.</summary>
    public static Picture? CaptureRect(int left, int top, int right, int bottom, IntPtr hwnd = default)
    {
        var r = new Win.RECT { Left = left, Top = top, Right = right, Bottom = bottom };
        if (r.Width <= 0 || r.Height <= 0) return null;
        try { return Capture(r, hwnd); }
        catch (Exception ex) { Log.Error("capture rect", ex); return null; }
    }

    /// <summary>A picture of a window's visible frame and where its top-left corner is on the screen; null if the window is gone, hidden,
    /// minimized or empty, or the capture fails. UI thread.</summary>
    /// <param name="workArea">only the part on the monitor's work area: a maximized main window's frame reaches under the task bar</param>
    public static (Picture Pic, int Left, int Top)? CaptureWindow(IntPtr hwnd, bool workArea = false)
    {
        try
        {
            if (hwnd == IntPtr.Zero || Win.IsIconic(hwnd) || !Win.IsWindowVisible(hwnd)) return null;
            var (b, pb) = Win.BoundsBoth(hwnd);
            if (workArea && Win.WorkArea(hwnd) is { } wa)
            {
                b = new Win.RECT { Left = Math.Max(b.Left, wa.Left), Top = Math.Max(b.Top, wa.Top), Right = Math.Min(b.Right, wa.Right), Bottom = Math.Min(b.Bottom, wa.Bottom) };
                var (wl, wt) = ScreenSpace.ToPhysical(hwnd, wa.Left, wa.Top); var (wr, wb) = ScreenSpace.ToPhysical(hwnd, wa.Right, wa.Bottom);
                pb = new Win.RECT { Left = Math.Max(pb.Left, wl), Top = Math.Max(pb.Top, wt), Right = Math.Min(pb.Right, wr), Bottom = Math.Min(pb.Bottom, wb) };
            }
            return b.Width > 0 && b.Height > 0 && pb.Width > 0 && pb.Height > 0 && Capture(b, hwnd, pb) is { } p ? (p, b.Left, b.Top) : null;
        }
        catch (Exception ex) { Log.Error("capture window", ex); return null; }
    }

    /// <summary>A picture of the logical screen rectangle <paramref name="r"/> at the screen's own (physical) resolution (<see cref="ScreenSpace"/>).</summary>
    static Picture? Capture(Win.RECT r, IntPtr hwnd = default, Win.RECT? physical = null)
    {
        if (r.Width <= 0 || r.Height <= 0) return null;
        Win.RECT phys;
        if (physical is { } known) phys = known;   // a window's frame is copied exactly as DWM has it: no logical round trip that could shift an edge by a pixel
        else
        {
            var (pl, pt) = ScreenSpace.ToPhysical(hwnd, r.Left, r.Top);
            var (pr, pb) = ScreenSpace.ToPhysical(hwnd, r.Right, r.Bottom);
            phys = new Win.RECT { Left = pl, Top = pt, Right = Math.Max(pr, pl + 1), Bottom = Math.Max(pb, pt + 1) };
        }
        return new Picture(Win.CopyScreen(phys), phys.Width, phys.Height) { Left = r.Left, Top = r.Top, HasOrigin = true, SrcW = r.Width, SrcH = r.Height };
    }

    /// <summary>Writes a picture taken earlier as the `shot` of event <paramref name="n"/> (what: "palette").</summary>
    /// <param name="keepSize">smart zoom: the picture is a 1:1 crop and is never scaled down, whatever its size</param>
    public void Write(int n, string kind, Picture p, string what, bool keepSize = false)
    {
        if (!_enabled || n <= 0) return;
        _fallback.Written(n); _held.Remove(n);
        var path = Path.Combine(_folder, ShotNaming.FileName(n, kind));
        var rel = ShotNaming.RelativePathFor(_sessionPath, n, kind);
        var onShot = _onShot;
        var (nw, nh) = what == "window" && !keepSize ? ShotImage.FitSize(p.Width, p.Height) : (p.Width, p.Height);
        var (box, arrow) = p.HasOrigin ? MarksFor(n, new Core.PxRect(p.Left, p.Top, p.Left + p.SrcWidth, p.Top + p.SrcHeight), nw, nh) : (null, null);
        var extra = p.HasOrigin ? ExtraFor(n, new Core.PxRect(p.Left, p.Top, p.Left + p.SrcWidth, p.Top + p.SrcHeight), nw, nh) : null;
        var t = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var data = nw == p.Width && nh == p.Height ? p.Pixels : ShotImage.Scale(p.Pixels, p.Width, p.Height, nw, nh);
                if (box != null || arrow != null || extra != null) { if (ReferenceEquals(data, p.Pixels)) data = (byte[])data.Clone(); }
                var a = arrow?.Invoke(data);   // decided on the picture as it is, before the frame is drawn on it
                if (extra is { } ex) Annotate.Draw(data, nw, nh, new[] { ex });
                if (box is { } bx) Annotate.Draw(data, nw, nh, new[] { bx });
                if (a is { } al) Annotate.DrawArrow(data, nw, nh, al);
                Png.Write(path, nw, nh, data); onShot(n, rel, what);
            }
            catch (Exception ex) { Log.Error($"shot {n} write", ex); }
        });
        lock (_tasks) { _tasks.RemoveAll(x => x.IsCompleted); _tasks.Add(t); }
    }

    /// <summary>Takes the picture; false only when <paramref name="skipBlank"/> and the window shows nothing yet (the dialog has not painted its controls): nothing is written.</summary>
    bool Grab(int n, string kind, IntPtr hwnd, string what, bool announce = true, bool skipBlank = false, bool retake = false)
    {
        var (b, pbounds) = Win.BoundsBoth(hwnd);
        if (b.Width <= 0 || b.Height <= 0 || pbounds.Width <= 0 || pbounds.Height <= 0) return true;
        var cap = Capture(b, hwnd, pbounds);                          // the only part that must run on the UI thread
        if (cap == null) return true;
        if (skipBlank && ShotImage.IsBlank(cap.Pixels, cap.Width, cap.Height)) { if (_blankLogged.Add(n)) Log.Info($"dialog picture {n} not taken yet: the dialog shows nothing (not painted); asked again until it does"); return false; }
        var px = cap.Pixels;
        if (announce) { _fallback.Written(n); _held.Remove(n); }
        var path = Path.Combine(_folder, ShotNaming.FileName(n, kind));
        var rel = ShotNaming.RelativePathFor(_sessionPath, n, kind);
        int w = cap.Width, h = cap.Height;
        var onShot = _onShot;
        // an end picture the late-palette watch holds: its first write and a retake's are counted by the gate, which says when the one `shot` event is written
        bool early = !retake && announce && _gate.EarlyStarted(n);
        if (retake && !_gate.RetakeStarted(n, rel, what)) { _opened.TryRemove(n, out _); _openedState.TryRemove(n, out _); _rects.Remove(n); _boxOnly.Remove(n); return false; }
        Task? prev = null;
        if (retake) { _retakeStarted = true; _endWrite.TryGetValue(n, out prev); }
        var (nw, nh) = what == "window" ? ShotImage.FitSize(w, h) : (w, h);
        var (box, arrow) = MarksFor(n, new Core.PxRect(b.Left, b.Top, b.Right, b.Bottom), nw, nh);   // logical rectangle: the pixels are physical (ScreenSpace)
        var extra = ExtraFor(n, new Core.PxRect(b.Left, b.Top, b.Right, b.Bottom), nw, nh);
        Action work = () =>
        {
            bool ok = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var data = nw == w && nh == h ? px : ShotImage.Scale(px, w, h, nw, nh);
                var a = arrow?.Invoke(data);   // decided on the picture as it is, before the frame is drawn on it
                if (extra is { } ex) Annotate.Draw(data, nw, nh, new[] { ex });
                if (box is { } bx) Annotate.Draw(data, nw, nh, new[] { bx });
                if (a is { } al) Annotate.DrawArrow(data, nw, nh, al);
                Png.Write(path, nw, nh, data);
                ok = true;
            }
            catch (Exception ex) { Log.Error($"shot {n} write", ex); }
            try
            {
                if (early) Settle(_gate.EarlyFinished(n, ok, rel, what));
                else if (retake) { if (!ok) Log.Info($"end picture {n}: the retaken picture could not be written: the first picture stays, no box"); Settle(_gate.RetakeFinished(n, ok)); }
                else if (ok && announce) onShot(n, rel, what);
            }
            catch (Exception ex) { Log.Error($"shot {n} event", ex); }
        };
        // a retake writes after the first picture's write has finished (one file name, never two writers at once)
        var t = prev == null ? Task.Run(work) : prev.ContinueWith(_ => work(), TaskScheduler.Default);
        if (early || retake) _endWrite[n] = t;
        lock (_tasks) { _tasks.RemoveAll(x => x.IsCompleted); _tasks.Add(t); }
        return true;
    }

    /// <summary>Waits (bounded) for pictures still being encoded, so their `shot` events land before session_end.</summary>
    public void Drain(int timeoutMs = 5000)
    {
        Task[] all;
        lock (_tasks) all = _tasks.ToArray();
        try { Task.WaitAll(all, timeoutMs); } catch (Exception ex) { Log.Error("shot drain", ex); }
    }

    public void End()
    {
        _clicks.Clear(); _rects.Clear(); _suppressed.Clear(); _deferred.Clear(); _fallback.Clear(); _held.Clear(); _boxOnly.Clear();
        _sched.Clear();
        _enabled = false;
        _onShot = (_, _, _) => { };
    }

    // ---- Win32 -------------------------------------------------------------------------------------------------------

    static class Win
    {
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }
        [StructLayout(LayoutKind.Sequential)] struct BITMAPINFOHEADER { public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant; }

        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);

        [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

        /// <summary>The work area (the screen without the task bar) of the monitor the window is mostly on; null if it cannot be read.</summary>
        public static RECT? WorkArea(IntPtr hwnd)
        {
            var m = MonitorFromWindow(hwnd, 2);   // MONITOR_DEFAULTTONEAREST
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            return m != IntPtr.Zero && GetMonitorInfo(m, ref mi) && mi.rcWork.Width > 0 ? mi.rcWork : null;
        }

        const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        const uint SRCCOPY = 0x00CC0020;   // no CAPTUREBLT: it makes the cursor flicker on every capture

        /// <summary>Visible frame (DWM extended frame bounds: without the invisible resize border) in logical coordinates; GetWindowRect if DWM fails.</summary>
        public static RECT Bounds(IntPtr hwnd) => BoundsBoth(hwnd).Logical;

        /// <summary>The visible frame both as the screen has it (physical: what DWM answers, kept as is for the BitBlt) and in the process's logical space (what marks are computed in).</summary>
        public static (RECT Logical, RECT Physical) BoundsBoth(IntPtr hwnd)
        {
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var r, Marshal.SizeOf<RECT>()) == 0 && r.Width > 0)
            {
                var (l, t) = ScreenSpace.ToLogical(hwnd, r.Left, r.Top);
                var (rr, b) = ScreenSpace.ToLogical(hwnd, r.Right, r.Bottom);
                return (new RECT { Left = l, Top = t, Right = rr, Bottom = b }, r);
            }
            GetWindowRect(hwnd, out r);
            var (pl, pt) = ScreenSpace.ToPhysical(hwnd, r.Left, r.Top);
            var (pr, pb) = ScreenSpace.ToPhysical(hwnd, r.Right, r.Bottom);
            return (r, new RECT { Left = pl, Top = pt, Right = pr, Bottom = pb });
        }

        /// <summary>BitBlt from the screen DC; 32-bit BGRA, top-down.</summary>
        public static byte[] CopyScreen(RECT r)
        {
            int w = r.Width, h = r.Height;
            ClickArrows.HideNow();   // the live arrow is never in a picture: step pictures get their own arrow drawn into the image
            var screen = GetDC(IntPtr.Zero);
            var mem = CreateCompatibleDC(screen);
            var bmi = new BITMAPINFOHEADER { biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            var dib = CreateDIBSection(screen, ref bmi, 0, out var bits, IntPtr.Zero, 0);
            try
            {
                if (dib == IntPtr.Zero) throw new InvalidOperationException("CreateDIBSection failed");
                var old = SelectObject(mem, dib);
                if (!BitBlt(mem, 0, 0, w, h, screen, r.Left, r.Top, SRCCOPY)) throw new InvalidOperationException("BitBlt failed");
                var px = new byte[w * h * 4];
                Marshal.Copy(bits, px, 0, px.Length);
                SelectObject(mem, old);
                return px;
            }
            finally
            {
                if (dib != IntPtr.Zero) DeleteObject(dib);
                DeleteDC(mem);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }
    }
}
