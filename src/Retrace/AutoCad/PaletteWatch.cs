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
    const uint WM_LBUTTONDOWN = 0x0201, WM_KEYDOWN = 0x0100;

    /// <summary>Where input landed: the palette's window (its tab in the set), the palette's name and the palette set's title.</summary>
    public sealed record Spot(IntPtr Page, string Palette, string Set);

    /// <summary>Values the user changed in <see cref="Where"/> since the last result. Source: "win32" | "wpf"; Read: fields seen. Picture: the
    /// palette as it last looked while open (it may have rolled up or slid away since), or null.</summary>
    public sealed record Values(Spot Where, string Source, List<FieldChange> Changes, int Read, ShotService.Picture? Picture);

    sealed class Watched
    {
        public required Spot Where;
        public string Source = "win32";
        public FieldTracker Tracker = new();
        public List<DialogField> Last = new();
        public long LastReadMs;
        public ShotService.Picture? Picture;   // the palette while open, kept fresh as the user works in it
        public List<DialogField>? PictureOf;   // the values it shows
    }

    Watched? _cur;

    public bool Enabled { get; set; }
    /// <summary>Keep close-up pictures of the palette (setting screenshots).</summary>
    public bool Pictures { get; set; }

    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsChild(IntPtr parent, IntPtr h);

    public void Clear() => _cur = null;

    /// <summary>The palette <paramref name="h"/> is in, or null. The chain AutoCAD builds (seen in R25.1, floating and docked): the control's
    /// WinForms / WPF windows, then the palette (AfxWnd, titled with the palette's name), the palette set (AfxWnd, the set's title), and
    /// its control bar (AfxControlBar). AutoCAD hosts its ribbon the same way, so the ribbon's window is ruled out by handle.</summary>
    public static Spot? Locate(IntPtr h)
    {
        bool managed = false;
        for (int i = 0; i < 40 && h != IntPtr.Zero; i++, h = GetParent(h))
        {
            var cls = DialogReader.ClassOf(h);
            if (cls.StartsWith("WindowsForms", StringComparison.Ordinal) || cls.StartsWith("HwndWrapper", StringComparison.Ordinal)) { managed = true; continue; }
            if (!cls.StartsWith("AfxWnd", StringComparison.Ordinal)) continue;
            if (!managed) return null;   // native content
            var set = GetParent(h);
            if (!DialogReader.ClassOf(set).StartsWith("AfxWnd", StringComparison.Ordinal)) return null;
            if (!DialogReader.ClassOf(GetParent(set)).StartsWith("AfxControlBar", StringComparison.Ordinal)) return null;
            var ribbon = RibbonHost();
            if (ribbon != IntPtr.Zero && (ribbon == h || IsChild(h, ribbon))) return null;
            var setTitle = Native.TitleOf(set);
            if (setTitle == Retrace.Ui.GuidePalette.SetTitle) return null;
            return new Spot(h, Native.TitleOf(h), setTitle);
        }
        return null;
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
        if (!Enabled) return (null, null, null, 0, null);
        var spot = Locate(hwnd);
        // a combo box's drop-down list is a top-level window of its own (Win32 ComboLBox, a WPF popup): picking an item there is still work in
        // the palette
        if (spot == null && _cur != null && (DialogReader.ClassOf(hwnd) == "ComboLBox" || WpfPalette.IsPopup(hwnd))) return (null, null, null, 0, null);
        if (spot == null) return (Flush(nowMs, drop: true), null, null, 0, null);
        Values? left = null;
        if (_cur == null || _cur.Where.Page != spot.Page) { left = Flush(nowMs, drop: true); Begin(spot, nowMs); }
        string? button = null;
        bool wpf = false;
        if (message == WM_LBUTTONDOWN)
        {
            button = DialogReader.PushButtonText(hwnd);
            if (button == null && WpfPalette.ButtonAt(hwnd, lParam) is { } w) { button = w; wpf = true; }
        }
        // Space presses a focused WinForms button (Enter only does in a form with an AcceptButton, not in a palette)
        else if (message == WM_KEYDOWN && (long)wParam == 0x20 && (((long)lParam >> 30) & 1) == 0) button = DialogReader.PushButtonText(hwnd);
        if (button == null) return (left, null, null, 0, null);
        // read now, before the button handles the click: these are the values it acts on
        var index = wpf ? WpfPalette.ButtonIndexAt(spot.Page, hwnd, lParam) : DialogReader.ButtonIndex(spot.Page, hwnd);
        TakePicture(_cur!);   // the palette is open under the mouse right now: as it looks when the button is pressed
        var picture = _cur!.Picture;
        return (left ?? Flush(nowMs, drop: false), spot, button, index, picture);
    }

    /// <summary>Timer tick: re-read the palette being worked in, at most every <see cref="ThrottleMs"/>.</summary>
    public void Tick(long nowMs)
    {
        var w = _cur;
        if (w != null && nowMs - w.LastReadMs >= ThrottleMs) { Read(w, nowMs); if (w.PictureOf == null || !w.PictureOf.SequenceEqual(w.Last)) TakePicture(w); }
    }

    /// <summary>The changes since the last result, or null if none. <paramref name="drop"/>: stop following the palette; else keep following
    /// it, with the values as they are now as the new starting point.</summary>
    public Values? Flush(long nowMs, bool drop)
    {
        var w = _cur;
        if (w == null) return null;
        Read(w, nowMs);
        var changes = w.Tracker.Changes();
        var result = changes.Count > 0 ? new Values(w.Where, w.Source, changes, w.Tracker.ReadCount, w.Picture) : null;
        if (drop) _cur = null;
        else { w.Tracker = new FieldTracker(); w.Tracker.Observe(w.Last); }
        return result;
    }

    /// <summary>Keeps a picture of the palette while it is open (never opens it): a palette with auto roll-up or auto-hide may be closed again by
    /// the time its step's screenshot is taken, so the step gets this one too.</summary>
    void TakePicture(Watched w)
    {
        if (!Pictures || PaletteReveal.PictureRect(w.Where.Page) is not { } r) return;
        if (ShotService.CaptureRect(r.Left, r.Top, r.Right, r.Bottom) is { } p) { w.Picture = p; w.PictureOf = w.Last; }
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
            fields = DialogReader.ReadWin32(w.Where.Page);
            if (fields.Count == 0 && WpfPalette.Host(w.Where.Page) != IntPtr.Zero) { w.Source = "wpf"; fields = WpfPalette.Read(w.Where.Page); }
        }
        else fields = WpfPalette.Read(w.Where.Page);
        w.Last = fields;
        w.Tracker.Observe(fields);
    }
}
