using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Retrace.AutoCad;

/// <summary>
/// Follows what the user does in palette sets while recording: the field values they change and the buttons they click. A palette set is not
/// a modal dialog, so <see cref="DialogWatch"/> never sees it. The recorder passes every click and key press (PreTranslateMessage); this class
/// finds the palette the input is aimed at (<see cref="Locate"/>), reads its fields when the user starts working in it, and hands back the
/// changes when the user clicks a button in it or moves on (another palette, the drawing, the ribbon). WinForms palettes are read through
/// their child windows (<see cref="DialogReader"/>); WPF palettes in process, from their visual tree (<see cref="WpfPalette"/>). Only
/// palettes with .NET content are read: AutoCAD's own native palettes (Properties, Tool Palettes) and Retrace's guide are left out.
/// UI thread only.
/// </summary>
sealed class PaletteWatch
{
    public const long ThrottleMs = 300;
    const uint WM_LBUTTONDBLCLK = 0x0203, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_KEYDOWN = 0x0100;

    /// <summary>Where input landed: the palette's window (its tab in the set), the palette's name and the palette set's title.</summary>
    public sealed record Spot(IntPtr Page, string Palette, string Set, string? PaneType = null, bool Strict = true);

    /// <summary>Values the user changed in <see cref="Where"/> since the last result. Source: "win32" | "wpf"; Read: fields seen. Picture: the
    /// palette as it last looked while open (it may have rolled up or slid away since), or null.</summary>
    public sealed record Values(Spot Where, string Source, List<FieldChange> Changes, int Read, ShotService.Picture? Picture);

    sealed class Watched
    {
        public required Spot Where;
        public string Source = "win32";
        public FieldTracker Tracker = new();
        public readonly TreeTickCache Ticks = new();
        public List<DialogField> Last = new();
        public long LastReadMs;
        public ShotService.Picture? Picture;   // the palette while open, kept fresh as the user works in it
        public List<DialogField>? PictureOf;   // the values it shows
        public readonly List<PaletteClickInfo> Absorb = new();   // clicks written since the last flush whose own change of a field is part of the click, not a value
    }

    Watched? _cur;
    /// <summary>The window of the palette page being worked in, zero when none.</summary>
    public IntPtr CurrentPage => _cur?.Where.Page ?? IntPtr.Zero;

    /// <summary>Where the last button's name came from (<see cref="ButtonNames"/>: "tooltip", "accessible", "automation", "name"); null for its text. Set by <see cref="OnInput"/>.</summary>
    public string? ButtonFrom { get; private set; }
    /// <summary>Which of the buttons without text of their own the last button was (`legacyIndex`); 0 when its name is its own text. Set by <see cref="OnInput"/>.</summary>
    public int ButtonLegacyIndex { get; private set; }
    /// <summary>How many buttons without text of their own the palette had at the last button click (`noTextCount`); 0 when its name is its own text.</summary>
    public int ButtonNoTextCount { get; private set; }

    /// <summary>A double-click on an item of a tree of the palette (set by <see cref="OnInput"/> when the message is one): the tree field's key and label, and the item's path. The button of the result is the item's text.</summary>
    public sealed record DoubleClick(string Tree, string TreeLabel, string Path);

    /// <summary>The double-click the last <see cref="OnInput"/> saw, or null.</summary>
    public DoubleClick? Double { get; private set; }

    /// <summary>The click on a tree item (or its +/- button), an image, a list view item or an unnamed control the last <see cref="OnInput"/> saw, or null. The button of the result is its name.</summary>
    public PaletteClickInfo? Intermediate { get; private set; }

    /// <summary>A toolbar click that went down and waits for its button-up.</summary>
    sealed record Pend(Spot Spot, PaletteClickInfo Info, IntPtr Hwnd);
    Pend? _pend;

    /// <summary>A toolbar item went down and its button-up is still to come (the recorder passes that message on).</summary>
    public bool HasPending => _pend != null;

    /// <summary>Does the button come up over the same item it went down on?</summary>
    static bool SameTarget(Pend p, IntPtr hwnd, IntPtr lParam)
    {
        int x = (short)((long)lParam & 0xFFFF), y = (short)(((long)lParam >> 16) & 0xFFFF);
        if (hwnd != p.Hwnd) return false;
        if (p.Info.Kind == PaletteClickKinds.Toolbar) return ToolbarControl.HitTest(hwnd, x, y) == p.Info.TargetIndex;
        if (p.Info.Drop) return PaletteClickDetect.DropDownChoice(hwnd, lParam, out _, out var leaf) != null && ReferenceEquals(leaf, p.Info.Target);
        return ToolStripControls.StripOf(hwnd) is { } strip && ToolStripControls.ItemAt(strip, x, y) is { } it && ReferenceEquals(it.Obj, p.Info.Target) && !ToolStripControls.OnArrow(it, x, y);
    }

    public bool Enabled { get; set; }
    /// <summary>Keep close-up pictures of the palette (setting screenshots).</summary>
    public bool Pictures { get; set; }
    /// <summary>Smart zoom (setting screenshotMode): the palette pictures show the whole palette with its surroundings (<see cref="SmartShots.PaletteArea"/>).</summary>
    public bool Smart { get; set; }

    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsChild(IntPtr parent, IntPtr h);

    public void Clear() { _cur = null; _pend = null; }

    /// <summary>The palette <paramref name="h"/> is in, or null. The chain AutoCAD builds (seen in R25.1, floating and docked): the control's
    /// WinForms / WPF windows, then the palette (AfxWnd, titled with the palette's name), the palette set (AfxWnd, the set's title), and
    /// its control bar (AfxControlBar). AutoCAD hosts its ribbon the same way, so the ribbon's window is ruled out by handle.</summary>
    public static Spot? Locate(IntPtr h)
    {
        bool managed = false;
        IntPtr topManaged = IntPtr.Zero;
        var start = h;
        for (int i = 0; i < 40 && h != IntPtr.Zero; i++, h = GetParent(h))
        {
            var cls = DialogReader.ClassOf(h);
            if (cls.StartsWith("WindowsForms", StringComparison.Ordinal) || cls.StartsWith("HwndWrapper", StringComparison.Ordinal)) { managed = true; topManaged = h; continue; }
            if (!cls.StartsWith("AfxWnd", StringComparison.Ordinal)) continue;
            if (!managed) return null;   // native content
            var set = GetParent(h);
            bool framed = DialogReader.ClassOf(set).StartsWith("AfxWnd", StringComparison.Ordinal) && DialogReader.ClassOf(GetParent(set)).StartsWith("AfxControlBar", StringComparison.Ordinal);
            if (framed)
            {
                // the frame seen in R25.1 (floating and docked): the first choice everywhere, so AutoCAD 2026 and every recording made so far stay as they were
                var ribbon = RibbonHost();
                if (ribbon != IntPtr.Zero && (ribbon == h || IsChild(h, ribbon))) return null;
                var setTitle = Native.TitleOf(set);
                if (setTitle == Retrace.Ui.GuidePalette.SetTitle) return null;
                return new Spot(h, Native.TitleOf(h), setTitle);
            }
            return LooseFrame(h, topManaged, start);
        }
        // managed content with no palette frame above it: a modeless form or window of an add-in (never AutoCAD's own, a pop-up, Retrace's, or a modal dialog's)
        if (managed && !IsModal())
        {
            // the decision is kept per window for the recording: no reflection or visual walk on every key and click in a window that is not a pane
            if (_paneCache.TryGetValue(topManaged, out var known) && IsWindow(topManaged)) return known;
            var spot = PaneWindows.KindOf(topManaged) != null ? new Spot(topManaged, Native.TitleOf(topManaged), "", PaneWindows.TypeNameOf(topManaged), Strict: false) : null;
            _paneCache[topManaged] = spot;
            return spot;
        }
        return null;
    }

    /// <summary>A palette page in a frame that is not the R25.1 one (another AutoCAD version builds it differently): followed only when the content it hosts is an add-in's (by its root type), the page has a title, a titled ancestor names the set,
    /// and it is not the ribbon, the Guide or a modal dialog's. One log line says the rule was used, so a user's log shows which frame their AutoCAD builds.</summary>
    static Spot? LooseFrame(IntPtr page, IntPtr content, IntPtr clicked)
    {
        if (IsModal()) return null;
        if (_looseCache.TryGetValue(page, out var known) && IsWindow(page)) return known;
        var spot = LooseFrameCompute(page, content, clicked);
        _looseCache[page] = spot;
        return spot;
    }

    static readonly Dictionary<IntPtr, Spot?> _paneCache = new(), _looseCache = new();

    static Spot? LooseFrameCompute(IntPtr page, IntPtr content, IntPtr clicked)
    {
        var title = Native.TitleOf(page);
        if (title.Length == 0 || !PaneWindows.HostedContentIsAddIn(content)) return null;
        var ribbon = RibbonHost();
        if (ribbon != IntPtr.Zero && (ribbon == page || IsChild(page, ribbon))) return null;
        string set = "";
        var up = GetParent(page);
        for (int i = 0; i < 6 && up != IntPtr.Zero && set.Length == 0; i++, up = GetParent(up))
        {
            var cls = DialogReader.ClassOf(up);
            if (!cls.StartsWith("Afx", StringComparison.Ordinal) || cls.StartsWith("Afx:", StringComparison.Ordinal) || cls.StartsWith("AfxMDI", StringComparison.Ordinal)) break;   // the top-level frame's title is the drawing's name
            var t = Native.TitleOf(up);
            if (t == Retrace.Ui.GuidePalette.SetTitle) return null;
            if (t.Length > 0) set = t;
        }
        if (set.Length == 0) return null;
        try
        {
            var classes = new List<string>(12);
            for (var x = clicked; x != IntPtr.Zero && classes.Count < 12; x = GetParent(x)) classes.Add(DialogReader.ClassOf(x));
            var chain = PaneClicks.ChainText(classes);
            if (LooseGate.Allow("loose|" + chain, Environment.TickCount64, out _)) Log.Info($"pane frame: loose rule, chain {chain}; palette \"{title}\" in \"{set}\"");
        }
        catch (Exception) { }
        return new Spot(page, title, set, Strict: false);
    }

    /// <summary>A modal dialog is open: the recorder says so (<see cref="ModalNow"/>), and a main window that is disabled is one too (a modal WPF window from ShowDialog may raise no EnterModal).</summary>
    static bool IsModal()
    {
        if (ModalNow) return true;
        try { var main = Autodesk.AutoCAD.ApplicationServices.Application.MainWindow.Handle; return main != IntPtr.Zero && !IsWindowEnabled(main); }
        catch (Exception) { return false; }
    }

    [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr h);

    /// <summary>A modal dialog is open (the recorder sets it): the dialog's own watch follows it, so no pane window is taken meanwhile.</summary>
    public static bool ModalNow;

    static readonly PaneClicks.LogGate PaneGate = new(), LooseGate = new();
    static readonly HashSet<IntPtr> _unfollowed = new();   // top-level windows a click in was already logged as not followed

    /// <summary>A new recording starts: the "pane click not recorded" lines are allowed again.</summary>
    public static void NewSession() { PaneGate.Reset(); LooseGate.Reset(); _paneCache.Clear(); _looseCache.Clear(); _unfollowed.Clear(); ModalNow = false; }

    /// <summary>The recording stops: one line says how many lines were left out by the rate limit.</summary>
    public static void EndSession() { ModalNow = false; try { if (PaneGate.Skipped > 0) Log.Info($"pane click lines: {PaneGate.Skipped} more left out by the rate limit"); } catch (Exception) { } }

    /// <summary>A left click that is in no followed pane. When the window chain holds managed (WinForms / WPF) content, one rate-limited line in retrace.log names the chain and why it was not followed, so a user's log says
    /// what was clicked. Native windows (the drawing, AutoCAD's own palettes) leave no line. The chain is read only here, for a left button-down outside every pane.</summary>
    static void LogUnfollowed(IntPtr hwnd, long nowMs)
    {
        try
        {
            IntPtr top = hwnd;
            for (int i = 0; i < 12 && top != IntPtr.Zero; i++) { var up = GetParent(top); if (up == IntPtr.Zero) break; top = up; }
            if (_unfollowed.Contains(top)) return;   // a click in this window was logged already: no string work on every click
            var classes = new List<string>(12);
            for (var h = hwnd; h != IntPtr.Zero && classes.Count < 12; h = GetParent(h)) classes.Add(DialogReader.ClassOf(h));
            var kind = PaneClicks.Classify(classes);
            if (kind == PaneChain.Native || kind == PaneChain.InPaletteSet) return;   // a framed page that Locate refused is the ribbon or the guide: no pane
            string why;
            if (IsModal()) why = "a modal dialog is open (its own watch follows it)";
            else if (kind == PaneChain.ManagedTopLevel)
            {
                var root = PaneWindows.DescribeRoot(top);
                why = PaneWindows.KindOf(top) == null ? $"not a form or window of an add-in (root {root}): not an add-in's (AutoCAD's own, Retrace's, a pop-up or a tool tip)" : "form or window of an add-in not followed";
            }
            else why = "palette frame of an unknown shape";
            var chain = PaneClicks.ChainText(classes);
            if (!PaneGate.Allow(chain + "|" + why, nowMs, out var skipped)) return;
            _unfollowed.Add(top);
            Log.Info($"pane click not recorded: {why}; window chain {chain}" + (skipped > 0 ? $" (+{skipped} more not logged)" : ""));
        }
        catch (Exception) { }
    }

    /// <summary>The window of AutoCAD's ribbon (WPF), or zero before the ribbon exists.</summary>
    static IntPtr RibbonHost()
    {
        try
        {
            return Autodesk.Windows.ComponentManager.Ribbon is { } r && System.Windows.PresentationSource.FromVisual(r) is HwndSource src ? src.Handle : IntPtr.Zero;
        }
        catch (Exception) { return IntPtr.Zero; }
    }

    /// <summary>A click or key press aimed at <paramref name="hwnd"/>. Returns the values to record now (the user left a palette, or is about to
    /// apply them with a button) and the button pressed, if any: clicked, or Space on a WinForms button that has the focus. Index: which
    /// of the palette's buttons with that text it was (1 = the first or only one). <paramref name="lParam"/> holds the click point.</summary>
    public (Values? Changed, Spot? ClickedIn, string? Button, int Index, ShotService.Picture? Picture) OnInput(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, long nowMs)
    {
        Double = null; Intermediate = null;
        ButtonFrom = null; ButtonLegacyIndex = 0; ButtonNoTextCount = 0;
        ButtonNaming.Recorded? rec = null;
        if (!Enabled) { _pend = null; return (null, null, null, 0, null); }
        // a toolbar item acts on the button-UP over the item it went down on: the click is recorded then (before the control handles that message, so what the handler starts follows it in the recording), and a press dragged off the item records nothing
        PaletteClickInfo? upClick = null;
        Spot? upSpot = null;
        if (message == WM_LBUTTONUP)
        {
            var p = _pend; _pend = null;
            if (p == null)
            {
                // the press went down on a drop-down button and the button came up on an item of its menu: no click (a menu item acts on a press that went down on it)
                if (ToolStripControls.IsDropDownWindow(hwnd) && PaletteClickDetect.DropDownChoice(hwnd, lParam, out _) != null) Log.Info("palette toolbar click not recorded: the button was pressed on a drop-down button and released on a menu item");
                return (null, null, null, 0, null);
            }
            if (!SameTarget(p, hwnd, lParam)) { Log.Info($"palette toolbar click not recorded: the button was released off the item it went down on ({p.Info.Button})"); return (null, null, null, 0, null); }
            // a menu item whose menu opens when it is pressed but had no items at the press (filled on open): the click is the choice made in it, not this press
            if (!p.Info.Drop && p.Info.Target != null && ToolStripControls.IsOpen(p.Info.Target)) { Log.Info($"palette toolbar click not recorded: the item opened a menu ({p.Info.Button}); the item chosen in it is the click"); return (null, null, null, 0, null); }
            upClick = p.Info; upSpot = p.Spot;
        }
        else if (message == WM_LBUTTONDOWN) _pend = null;
        var spot = upSpot ?? Locate(hwnd);
        // the menu of a toolbar's drop-down button is a window of its own: an item chosen there is a click of the palette's toolbar item (the strip leads back to the palette)
        PaletteClickInfo? dropChoice = null;
        if (spot == null && message == WM_LBUTTONDOWN && PaletteClickDetect.DropDownChoice(hwnd, lParam, out var stripWindow) is { } dc && Locate(stripWindow) is { } stripSpot) { dropChoice = dc; spot = stripSpot; }
        // a combo box's drop-down list is a top-level window of its own (Win32 ComboLBox, a WPF popup): picking an item there is still work in
        // the palette
        // (a click in the drop-down menu of a toolbar item that is no choice, a sub menu that only opens, a separator, a disabled item, padding, is work in the palette too)
        if (spot == null && _cur != null && (DialogReader.ClassOf(hwnd) == "ComboLBox" || WpfPalette.IsPopup(hwnd) || ToolStripControls.IsDropDownWindow(hwnd))) return (null, null, null, 0, null);
        if (spot == null) { if (message == WM_LBUTTONDOWN) LogUnfollowed(hwnd, nowMs); return (Flush(nowMs, drop: true), null, null, 0, null); }
        Values? left = null;
        if (_cur == null || _cur.Where.Page != spot.Page) { left = Flush(nowMs, drop: true); Begin(spot, nowMs); }
        string? button = null;
        if (upClick != null) { Intermediate = upClick; button = upClick.Button; }
        else if (message == WM_LBUTTONDOWN)
        {
            // a WinForms push button is told by its class and style alone (no scan for any other window); a WPF button by a hit test, and only then is the page scanned
            rec = DialogReader.PushButtonText(hwnd) != null ? DialogReader.RecordButton(spot.Page, hwnd) : null;
            if (rec == null && WpfPalette.RecordButtonAt(spot.Page, hwnd, lParam) is { } w) { rec = w; }
            button = rec?.Name;
        }
        else if (message == WM_LBUTTONDBLCLK && TreeDoubleClick(hwnd, spot, lParam) is { } dbl) { Double = dbl.Info; button = dbl.Text; }
        if (upClick == null && button == null && message is WM_LBUTTONDOWN or WM_LBUTTONDBLCLK && (dropChoice ?? PaletteClickDetect.Detect(spot.Page, hwnd, message, lParam, spot.Strict)) is { } ic)
        {
            if (ic.Kind is PaletteClickKinds.ToolStrip or PaletteClickKinds.Toolbar) { _pend = new Pend(spot, ic, hwnd); return (left, null, null, 0, null); }   // recorded at the button-up
            Intermediate = ic; button = ic.Button; rec = null;
        }
        // Space presses a focused WinForms button (Enter only does in a form with an AcceptButton, not in a palette)
        else if (message == WM_KEYDOWN && (long)wParam == 0x20 && (((long)lParam >> 30) & 1) == 0 && DialogReader.PushButtonText(hwnd) != null && DialogReader.RecordButton(spot.Page, hwnd) is { } kb) { rec = kb; button = kb.Name; }
        if (button == null) return (left, null, null, 0, null);
        if (Double != null) return (left ?? Flush(nowMs, drop: false), spot, button, 1, null);   // the tree's own selection came with the first click: its values go with this step
        if (Intermediate is { } clicked)
        {
            // the values so far go first; the field the click itself changes (the selected item) belongs to the click
            TakePicture(_cur!);
            ButtonFrom = clicked.From; ButtonLegacyIndex = clicked.LegacyIndex; ButtonNoTextCount = clicked.NoTextCount;
            var flushed = left ?? Flush(nowMs, drop: false);
            if (clicked.AbsorbKey != null) _cur!.Absorb.Add(clicked);
            return (flushed, spot, button, clicked.Index, _cur!.Picture);
        }
        // read now, before the button handles the click: these are the values it acts on
        var index = rec?.Index ?? 1;
        ButtonFrom = rec?.From; ButtonLegacyIndex = rec?.LegacyIndex ?? 0; ButtonNoTextCount = rec?.NoTextCount ?? 0;
        TakePicture(_cur!);   // the palette is open under the mouse right now: as it looks when the button is pressed
        var picture = _cur!.Picture;
        return (left ?? Flush(nowMs, drop: false), spot, button, index, picture);
    }

    /// <summary>A double-click on an item of a tree of the palette (not its +/- button, not its check box): the item's text and the tree and path it is. A node with children is left out (the double-click opens or closes it:
    /// whatever else the program does with it is not seen from here, so only an item without children is a click of the test). Null when it is none of that.</summary>
    (DoubleClick Info, string Text)? TreeDoubleClick(IntPtr tree, Spot spot, IntPtr lParam)
    {
        var item = TreeControl.ItemAt(tree, lParam);
        if (item == IntPtr.Zero || TreeControl.HasChildren(tree, item)) return null;
        var scan = DialogReader.Scan(spot.Page, readTicks: false);
        for (int i = 0; i < scan.Fields.Count; i++)
            if (scan.Fields[i].Kind == "tree" && scan.Handles[i].Contains(tree))
                return (new DoubleClick(scan.Fields[i].Key, scan.Fields[i].Label, TreePath.Join(TreeControl.TextsTo(tree, item))), TreeControl.Text(tree, item).Trim());
        return null;
    }

    /// <summary>Input reached <paramref name="hwnd"/>: a tree of the palette shows a changed tick once the message is dispatched.</summary>
    public void NoteInput(IntPtr hwnd) => _cur?.Ticks.Invalidate(hwnd);

    /// <summary>A picture of the palette <paramref name="spot"/> as it is now (taken at a right-click, before a menu covers it), or null (pictures off, the palette not on screen).</summary>
    public ShotService.Picture? PictureNow(Spot spot)
    {
        if (!Pictures) return null;
        if (_cur != null && _cur.Where.Page == spot.Page) { TakePicture(_cur); return _cur.Picture; }
        return SmartShots.PaletteArea(spot.Page, Smart) is { } r ? ShotService.CaptureRect(r.L, r.T, r.R, r.B, spot.Page) : null;
    }

    /// <summary>Timer tick: re-read the palette being worked in, at most every <see cref="ThrottleMs"/>.</summary>
    public void Tick(long nowMs)
    {
        var w = _cur;
        if (w != null && nowMs - w.LastReadMs >= ThrottleMs) { Read(w, nowMs); if (Smart && (w.PictureOf == null || !w.PictureOf.SequenceEqual(w.Last))) TakePicture(w); }   // classic: only the picture from a press (the fallback), not one kept fresh while the person works
    }

    /// <summary>The changes since the last result, or null if none. <paramref name="drop"/>: stop following the palette; else keep following
    /// it, with the values as they are now as the new starting point.</summary>
    public Values? Flush(long nowMs, bool drop)
    {
        var w = _cur;
        if (w == null) return null;
        Read(w, nowMs);
        var changes = w.Tracker.Changes();
        if (w.Absorb.Count > 0)
        {
            changes = changes.Where(c => !w.Absorb.Any(a => c.Key == a.AbsorbKey && c.Kind == a.AbsorbKind && (a.AbsorbKind == "tree" ? TreePath.Same(c.After, a.AbsorbValue) : c.After == a.AbsorbValue))).ToList();
            w.Absorb.Clear();
        }
        var result = changes.Count > 0 ? new Values(w.Where, w.Source, changes, w.Tracker.ReadCount, w.Picture) : null;
        if (drop) _cur = null;
        else { w.Tracker = new FieldTracker(); w.Tracker.Observe(w.Last); }
        return result;
    }

    /// <summary>Keeps a picture of the palette while it is open (never opens it): a palette with auto roll-up or auto-hide may be closed again by
    /// the time its step's screenshot is taken, so the step gets this one too.</summary>
    void TakePicture(Watched w)
    {
        if (!Pictures || SmartShots.PaletteArea(w.Where.Page, Smart) is not { } r) return;
        if (ShotService.CaptureRect(r.L, r.T, r.R, r.B, w.Where.Page) is { } p) { w.Picture = p; w.PictureOf = w.Last; }
    }

    void Begin(Spot spot, long nowMs)
    {
        _cur = new Watched { Where = spot };
        Read(_cur, nowMs);
    }

    void Read(Watched w, long nowMs)
    {
        w.LastReadMs = nowMs;
        if (!IsWindow(w.Where.Page) || !IsWindowVisible(w.Where.Page)) return;   // closed, or rolled away (auto-hide): keep the last read
        List<DialogField> fields;
        if (w.Source == "win32")
        {
            fields = DialogReader.ReadWin32(w.Where.Page, w.Ticks);
            if (fields.Count == 0 && WpfPalette.Host(w.Where.Page) != IntPtr.Zero) { w.Source = "wpf"; fields = WpfPalette.Read(w.Where.Page); }
        }
        else fields = WpfPalette.Read(w.Where.Page);
        w.Last = fields;
        w.Tracker.Observe(fields);
    }
}
