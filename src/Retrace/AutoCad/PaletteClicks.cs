using System.Runtime.InteropServices;
using Retrace.Core;

namespace Retrace.AutoCad;

/// <summary>What the recorder saw in a palette besides a push button: a click on a tree item or its +/- button, an image (PictureBox), an item of a list view, or a control with no name (`palette_click` with `kind`
/// tree, expand, collapse, image, listview, point). <c>Tree</c>/<c>TreeLabel</c>/<c>Path</c>: the tree kinds; <c>Hit</c>: list view and point (and the click point of an image); <c>AbsorbKey</c>/<c>AbsorbKind</c>/<c>AbsorbValue</c>:
/// the value of the field the click changes, which is part of the click and is not written as a value (<see cref="PaletteWatch.Flush"/>).</summary>
sealed record PaletteClickInfo(string Kind, string Button, string? From, int Index, int LegacyIndex, int NoTextCount, string Tree, string TreeLabel, string Path, PaletteHit? Hit,
    string? AbsorbKey = null, string? AbsorbKind = null, string? AbsorbValue = null, object? Target = null, int TargetIndex = -1, bool Drop = false, ScreenRect? Rect = null)
{
    /// <summary>The fields of the `palette_click` event after palette / set / button.</summary>
    public (string, object?)[] ToFields()
    {
        var f = new List<(string, object?)> { ("kind", Kind) };
        if (PaletteClickKinds.OnTree(Kind)) { f.Add(("tree", Tree)); f.Add(("treeLabel", TreeLabel)); f.Add(("path", Path)); }
        if (Kind == PaletteClickKinds.Image)
        {
            f.Add(("buttonFrom", From));
            if (Index > 1) f.Add(("index", Index));
            f.Add(("legacyIndex", LegacyIndex > 0 ? LegacyIndex : null)); f.Add(("noTextCount", NoTextCount > 0 ? NoTextCount : null));
        }
        if (Kind is PaletteClickKinds.ToolStrip or PaletteClickKinds.Toolbar)
        {
            f.Add(("buttonFrom", From));
            if (Index > 1) f.Add(("index", Index));
            if (Hit is { } t)
            {
                f.Add(("strip", t.Strip.Length > 0 ? t.Strip : null)); f.Add(("stripIndex", t.StripIndex)); f.Add(("itemName", t.ItemName.Length > 0 ? t.ItemName : null));
                f.Add(("position", t.Position)); f.Add(("count", t.Count)); f.Add(("cmd", t.Cmd > 0 ? t.Cmd : null)); f.Add(("menu", t.Menu.Length > 0 ? t.Menu : null));
            }
        }
        if (Hit is { } h)
        {
            if (Kind == PaletteClickKinds.ListView)
            {
                f.Add(("list", h.Control)); f.Add(("listLabel", h.ControlLabel.Length > 0 ? h.ControlLabel : null)); f.Add(("item", h.Item)); f.Add(("itemIndex", h.ItemIndex)); f.Add(("occurrence", h.Occurrence)); f.Add(("view", h.View));
            }
            if (Kind == PaletteClickKinds.Point) { f.Add(("class", h.Class)); f.Add(("classIndex", h.ClassIndex)); f.Add(("controlName", h.Name.Length > 0 ? h.Name : null)); }
            if (h.HasPoint) { f.Add(("x", h.Fx)); f.Add(("y", h.Fy)); }
        }
        return f.ToArray();
    }
}

/// <summary>Recording side of the intermediate clicks of a palette (<see cref="PaletteWatch"/> asks on every left button-down). UI thread only.</summary>
static class PaletteClickDetect
{
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out Win32.RECT r);

    public static PaletteClickInfo? Detect(IntPtr page, IntPtr hwnd, uint message, IntPtr lParam)
    {
        try
        {
            const uint LBUTTONDOWN = 0x0201, LBUTTONDBLCLK = 0x0203;
            if (message is not (LBUTTONDOWN or LBUTTONDBLCLK)) return null;
            int x = (short)((long)lParam & 0xFFFF), y = (short)(((long)lParam >> 16) & 0xFFFF);
            var seg = DialogReader.Segment(DialogReader.ClassOf(hwnd));
            if (seg == "systreeview32") return Tree(page, hwnd, message == LBUTTONDBLCLK, x, y);
            // a ToolStrip and a toolbar take the second click of a double-click as a click of its own (the control raises Click again): it is a second step, as the person made it
            if (seg == "toolbarwindow32") return Toolbar(page, hwnd, x, y);
            if (seg is "static" or "window" && ToolStripControls.StripOf(hwnd) is { } strip0) return Strip(page, hwnd, strip0, x, y);
            if (message == LBUTTONDBLCLK) return null;
            if (seg == "syslistview32") return List(page, hwnd, x, y);
            if (seg is "static" or "window") return PictureOrPoint(page, hwnd, seg, x, y);
            if (!KnownQuiet(seg) && !seg.StartsWith("hwndwrapper", StringComparison.Ordinal)) Ignored(hwnd, "no kind of click is recorded for this control class");   // a WPF palette's buttons are recorded by their own path
            return null;
        }
        catch (Exception ex) { Log.Error("palette click", ex); return null; }
    }

    // ---- toolbars -----------------------------------------------------------------------------------------------------------------------

    /// <summary>Window classes whose clicks are the values of the field they edit (or no action at all): a click on one is no step of its own and needs no line in the log.</summary>
    static bool KnownQuiet(string seg) =>
        seg is "edit" or "combobox" or "comboboxex32" or "combolbox" or "listbox" or "button" or "systabcontrol32" or "scrollbar" or "msctls_updown32" or "msctls_trackbar32" or "msctls_progress32"
            or "sysdatetimepick32" or "sysmonthcal32" or "sysipaddress32" or "msctls_hotkey32" or "richedit20w" or "richedit50w" or "systreeview32" or "syslistview32" or "sysheader32" or "msctls_statusbar32" or "tooltips_class32";

    static readonly HashSet<string> Logged = new();

    /// <summary>A recording starts: every control class is logged once again.</summary>
    public static void NewSession() => Logged.Clear();

    /// <summary>A left click in a followed palette that is no step: one line in retrace.log naming the control, so a user's log says what was clicked (once per control class and reason in a recording).</summary>
    internal static void Ignored(IntPtr hwnd, string why)
    {
        try
        {
            var cls = DialogReader.ClassOf(hwnd);
            var net = WinFormsControls.Control(hwnd)?.GetType().FullName;
            // once per control class and reason in a recording, not per click
            if (!Logged.Add(cls + "|" + why)) return;
            Log.Info($"palette click not recorded: {why}; control class {cls}" + (net != null ? $" ({net})" : ""));
        }
        catch (Exception) { }
    }

    /// <summary>The place of a ToolStrip (or ToolbarWindow32) of the page among those of its kind, 1 = the first, 0 when it is not among them.</summary>
    static int StripNumber(IntPtr page, IntPtr h, bool toolbar)
    {
        var all = Children(page, hidden: true).Where(c => toolbar ? DialogReader.Segment(DialogReader.ClassOf(c)) == "toolbarwindow32" : ToolStripControls.StripOf(c) != null).ToList();
        int at = all.IndexOf(h);
        return at < 0 ? 0 : at + 1;
    }

    static PaletteClickInfo? Strip(IntPtr page, IntPtr hwnd, object strip, int x, int y)
    {
        if (ToolStripControls.ItemAt(strip, x, y) is not { } item) { Ignored(hwnd, "a click on the toolbar's empty space"); return null; }
        if (item.Separator) { Ignored(hwnd, "a click on a toolbar separator"); return null; }
        if (item.Overflow) { Ignored(hwnd, "the toolbar's overflow button (its menu is not followed)"); return null; }
        if (!item.Enabled) { Ignored(hwnd, "a click on a disabled toolbar item"); return null; }
        if (item.Label || item.Host) { Ignored(hwnd, item.Host ? "a click on a control hosted by the toolbar" : "a click on a toolbar label"); return null; }
        // a drop-down opens its menu (the item chosen there is the step, see DropDownChoice); the main part of a split button is a button of its own
        if (item.DropDown && (!item.Split || ToolStripControls.OnArrow(item, x, y))) return null;
        var buttons = ToolStripControls.Buttons(strip);
        int at = buttons.FindIndex(b => ReferenceEquals(b.Obj, item.Obj));
        if (at < 0) { Ignored(hwnd, "the toolbar item is not among the strip's buttons"); return null; }
        var cands = buttons.Select(b => b.Candidate).ToList();
        var (name, from) = PaletteToolbar.Display(cands[at]);
        int occurrence = PaletteToolbar.Occurrence(cands, at);
        string? src = ButtonNames.FromToRecord(from);
        ScreenRect? rect = WantRects && ToolStripControls.ScreenOf(hwnd, item, arrow: false) is { } sc ? ToolStripControls.InsideStrip(hwnd, sc.Rect) : null;   // the item, never the whole strip; none when it is not inside the strip
        return new PaletteClickInfo(PaletteClickKinds.ToolStrip, name, src, occurrence, 0, 0, "", "", "",
            new PaletteHit(Strip: ToolStripControls.NameOf(strip), StripIndex: StripNumber(page, hwnd, false), ItemName: UniqueName(buttons, item), Position: at + 1, Count: buttons.Count), Target: item.Obj, Rect: rect);
    }

    /// <summary>The item's `Name` when no other button of the strip has it, else "" (a replay then goes by the display name and its occurrence).</summary>
    static string UniqueName(List<TsItem> buttons, TsItem item) =>
        item.Name.Length > 0 && buttons.Count(b => string.Equals(b.Name, item.Name, StringComparison.OrdinalIgnoreCase)) == 1 ? item.Name : "";

    /// <summary>The buttons of a ToolbarWindow32 as the finder lists them: own text, else the tool tip the owner gives (asked once per button), and the command id.</summary>
    internal static List<ToolCandidate> Candidates(IntPtr tb, List<ToolbarButton> buttons, int askId = -1, bool all = false) =>
        buttons.Select(b => { var (t, tip) = ToolbarControl.NamesOf(tb, b.Id, ask: all || b.Id == askId, fresh: b.Id == askId); return new ToolCandidate("", t, tip, "", b.Enabled, b.Id); }).ToList();

    /// <summary>Rectangles for the test document's frames are read only when the setting is on (set by the recorder).</summary>
    public static bool WantRects;

    static PaletteClickInfo? Toolbar(IntPtr page, IntPtr tb, int x, int y)
    {
        if (!ToolbarControl.Ok(tb)) { Ignored(tb, "a toolbar of another thread"); return null; }
        int index = ToolbarControl.HitTest(tb, x, y);
        if (index < 0) { Ignored(tb, "a click on a toolbar separator or empty space"); return null; }
        var buttons = ToolbarControl.Buttons(tb);
        int at = buttons.FindIndex(b => b.Index == index);
        if (at < 0) { Ignored(tb, "the toolbar button under the click is hidden"); return null; }
        var b = buttons[at];
        if (!b.Enabled) { Ignored(tb, "a click on a disabled toolbar button"); return null; }
        // the arrow of a split drop-down button opens the owner's menu: it is no click of the button (TB_HITTEST does not tell the parts apart; TB_GETITEMDROPDOWNRECT does)
        if (ToolbarControl.DropDownArrowRect(tb, b) is { } arrow && ToolbarLabel.Inside(x, y, arrow.L, arrow.T, arrow.R, arrow.B)) { Ignored(tb, "toolbar drop-down arrow: not recorded"); return null; }
        var cands = Candidates(tb, buttons, b.Id, all: true);   // every button's tool tip, asked once per toolbar (an empty answer is remembered), so the occurrence of the name is counted over all of them
        var (name, from) = PaletteToolbar.Display(cands[at]);
        int occurrence = PaletteToolbar.Occurrence(cands, at);
        if (ToolbarPress.StopFor(b.Style, ToolbarControl.ExtendedStyle(tb)) != null) Log.Info($"palette toolbar button {b.Id} opens a menu: the click is recorded, the menu item chosen is not followed");
        return new PaletteClickInfo(PaletteClickKinds.Toolbar, name, ButtonNames.FromToRecord(from), occurrence, 0, 0, "", "", "",
            new PaletteHit(Strip: WinFormsControls.NamesOf(tb)?.Name ?? "", StripIndex: StripNumber(page, tb, true), Position: at + 1, Count: buttons.Count, Cmd: b.Id > 0 && buttons.Count(q => q.Id == b.Id) == 1 ? b.Id : 0), TargetIndex: index,
            Rect: WantRects && ToolbarControl.ItemScreenRectInStrip(tb, index) is { } ir ? new ScreenRect(ir.L, ir.T, ir.R, ir.B) : null);   // a duplicate or zero id names nothing
    }

    /// <summary>A click on a drop-down menu of a toolbar item of a palette (the menu is a window of its own, so the palette's click detection never sees it): the item chosen, as a click of the toolbar item that opened the menu, with
    /// <c>menu</c> the path of texts. <paramref name="strip"/>: the strip's window, to find the palette. Null when the click is no menu choice.</summary>
    public static PaletteClickInfo? DropDownChoice(IntPtr hwnd, IntPtr lParam, out IntPtr strip) => DropDownChoice(hwnd, lParam, out strip, out _);

    public static PaletteClickInfo? DropDownChoice(IntPtr hwnd, IntPtr lParam, out IntPtr strip, out object? leaf)
    {
        strip = IntPtr.Zero; leaf = null;
        try
        {
            if (!DialogReader.ClassOf(hwnd).StartsWith("WindowsForms", StringComparison.Ordinal) || ToolStripControls.DropDownOf(hwnd) == null) return null;   // every click outside a palette asks: the class alone rules out almost all of them
            int x = (short)((long)lParam & 0xFFFF), y = (short)(((long)lParam >> 16) & 0xFFFF);
            var choice = ToolStripControls.DropDownChoice(hwnd, x, y, out var why);
            if (choice == null)
            {
                if (why is not ("no item under the click" or "a sub menu (it only opens)" or "a menu with no owner item")) Ignored(hwnd, "a click in a toolbar drop-down: " + why);
                return null;
            }
            strip = choice.Strip; leaf = choice.Leaf;
            var root = ToolStripControls.StripOf(strip);
            var page = Locate(strip);
            if (root == null || page == null) return null;
            var item = ToolStripControls.Items(root).FirstOrDefault(i => ReferenceEquals(i.Obj, choice.Root));
            if (item == null) { Ignored(hwnd, "the toolbar item that opened the menu is not on the strip"); return null; }
            var buttons = ToolStripControls.Buttons(root);
            int at = buttons.FindIndex(b => ReferenceEquals(b.Obj, item.Obj));
            if (at < 0) return null;
            var cands = buttons.Select(b => b.Candidate).ToList();
            var (name, from) = PaletteToolbar.Display(cands[at]);
            return new PaletteClickInfo(PaletteClickKinds.ToolStrip, name, ButtonNames.FromToRecord(from), PaletteToolbar.Occurrence(cands, at), 0, 0, "", "", "",
                new PaletteHit(Strip: ToolStripControls.NameOf(root), StripIndex: StripNumber(page.Page, strip, false), ItemName: UniqueName(buttons, item), Position: at + 1, Count: buttons.Count, Menu: TreePath.Join(choice.Path)), Target: choice.Leaf, Drop: true, Rect: WantRects ? ToolStripControls.WholeItemRect(strip, item) : null);   // the button that owns the menu: the menu has closed by the time the picture is taken, so a frame on its item would be blank space
        }
        catch (Exception ex) { Log.Error("palette drop-down click", ex); return null; }
    }

    static PaletteWatch.Spot? Locate(IntPtr h) => PaletteWatch.Locate(h);

    static PaletteClickInfo? Tree(IntPtr page, IntPtr tree, bool dbl, int x, int y)
    {
        if (!Native.OwnedByThisThread(tree)) return null;
        var (item, flags) = TreeControl.HitTest(tree, x, y);
        if (item == IntPtr.Zero) return null;
        string kind;
        if (dbl)
        {
            // a double-click on a node with children opens or closes it (a leaf is a `dblclick`, see PaletteWatch)
            // (a double-click on the +/- button is a second toggle in comctl32: the first button-down opened the node, this one closes it again, and a replay has to do both)
            if ((flags & (TreeControl.TVHT_ITEMBODY | 0x10 /* TVHT_ONITEMBUTTON */)) == 0 || !TreeControl.HasChildren(tree, item)) return null;
            kind = Toggles(tree, item);
        }
        else if ((flags & 0x10) != 0) kind = Toggles(tree, item);   // TVHT_ONITEMBUTTON
        else if ((flags & TreeControl.TVHT_ITEMBODY) != 0) kind = PaletteClickKinds.Tree;
        else return null;   // a check box, the indent, empty space: the value of the tree says what changed
        var scan = DialogReader.Scan(page, readTicks: false);
        for (int i = 0; i < scan.Fields.Count; i++)
            if (scan.Fields[i].Kind == "tree" && scan.Handles[i].Contains(tree))
            {
                var texts = TreeControl.TextsTo(tree, item);
                var path = PaletteClickKinds.ClickPath(texts);
                var selected = TreePath.Join(texts);   // the form the tree's own value has (TreeControl.SelectedPath: a blank level stays empty), so the selection this click makes is not written again as a value
                return new PaletteClickInfo(kind, PaletteClickKinds.ItemName(TreeControl.Text(tree, item)), null, 1, 0, 0, scan.Fields[i].Key, scan.Fields[i].Label, path, null,
                    kind == PaletteClickKinds.Tree ? scan.Fields[i].Key : null, "tree", kind == PaletteClickKinds.Tree ? selected : null);
            }
        return null;
    }

    static string Toggles(IntPtr tree, IntPtr item) => (TreeControl.State(tree, item, TreeControl.TVIS_EXPANDED) & TreeControl.TVIS_EXPANDED) != 0 ? PaletteClickKinds.Collapse : PaletteClickKinds.Expand;

    static PaletteClickInfo? List(IntPtr page, IntPtr list, int x, int y)
    {
        var o = MenuListView.Read(list, x, y);
        if (!o.Known || o.NoItem) return null;
        var (key, label) = MenuListView.KeyOf(list, page);
        string? fieldKey = null;
        var scan = DialogReader.Scan(page, readTicks: false);
        for (int i = 0; i < scan.Fields.Count; i++)
            if (scan.Fields[i].Kind == ListSelection.Kind && scan.Handles[i].Contains(list)) { fieldKey = scan.Fields[i].Key; break; }
        var item = new ListItem(o.Item, o.Index, o.Occurrence);
        var hit = new PaletteHit(key, label, o.Item, o.Index, o.Occurrence, MenuListView.ViewOf(list));
        return new PaletteClickInfo(PaletteClickKinds.ListView, o.Item.Trim().Length > 0 ? o.Item.Trim() : "(blank)", null, 1, 0, 0, "", "", "", hit, fieldKey, ListSelection.Kind, ListSelection.Format(new[] { item }));
    }

    // ---- images and unnamed controls -----------------------------------------------------------------------------------------------

    /// <summary>The picture boxes of a palette page in window order, and their fallback names (read when asked, kept for this scan).</summary>
    internal sealed class PictureScan
    {
        public readonly List<IntPtr> H = new();
        readonly ButtonNaming.Cache _names = new();
        public string? Fallback(int i, string from) => _names.Get(H[i], from, () => ButtonNaming.Win32Source(H[i], from));
        public List<string?> Names(string? from) => Enumerable.Range(0, H.Count).Select(i => ButtonNames.IsFallback(from) ? Fallback(i, from!) : null).ToList();
    }

    internal static PictureScan Pictures(IntPtr page, bool hidden)
    {
        var scan = new PictureScan();
        foreach (var h in Children(page, hidden))
            if (DialogReader.Segment(DialogReader.ClassOf(h)) is "static" or "window" && WinFormsControls.ClickKindOf(h) is { Picture: true }) scan.H.Add(h);
        return scan;
    }

    /// <summary>The visible child windows of a page, in enumeration order (hidden: a palette that is rolled up, whose controls count when they and every window above them have WS_VISIBLE).</summary>
    internal static List<IntPtr> Children(IntPtr page, bool hidden)
    {
        var all = new List<IntPtr>();
        EnumChildWindows(page, (h, _) => { if (hidden ? DialogReader.OwnVisibleUpTo(h, page) : Native.IsVisible(h)) all.Add(h); return true; }, IntPtr.Zero);
        return all;
    }

    delegate bool EnumProc(IntPtr h, IntPtr lp);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lp);

    static PaletteClickInfo? PictureOrPoint(IntPtr page, IntPtr h, string seg, int x, int y)
    {
        if (WinFormsControls.ClickKindOf(h) is not { } kind) { Ignored(h, "not a WinForms control"); return null; }
        if (!kind.Clickable) { Ignored(h, "the control has no click handler of its own and is no known clickable kind"); return null; }
        GetClientRect(h, out var rc);
        double fx = PaletteClickKinds.Fraction(x, rc.Right), fy = PaletteClickKinds.Fraction(y, rc.Bottom);
        if (kind.Picture)
        {
            var pics = Pictures(page, hidden: false);
            int at = pics.H.IndexOf(h);
            if (at < 0) return null;
            var (name, from) = ButtonNames.Pick(null, () => pics.Fallback(at, ButtonNames.FromTooltip), () => pics.Fallback(at, ButtonNames.FromAccessible), null, () => pics.Fallback(at, ButtonNames.FromName));
            string recorded = from.Length > 0 ? name : PaletteClickKinds.ImageUnnamed;
            string? src = from.Length > 0 ? from : null;
            int index = PaletteClickKinds.ImageIndexAmong(pics.Names(src), at, recorded, src);
            return new PaletteClickInfo(PaletteClickKinds.Image, recorded, src, index, src == null ? 0 : at + 1, src == null ? 0 : pics.H.Count, "", "", "", new PaletteHit(Fx: fx, Fy: fy));
        }
        // a control with no name of its own: its class and place among the controls of that class
        var same = Children(page, hidden: false).Where(c => DialogReader.Segment(DialogReader.ClassOf(c)) == seg).ToList();
        int ci = same.IndexOf(h) + 1;
        if (ci <= 0) return null;
        return new PaletteClickInfo(PaletteClickKinds.Point, kind.Name.Length > 0 ? kind.Name : seg, null, 1, 0, 0, "", "", "", new PaletteHit(Class: seg, ClassIndex: ci, Name: kind.Name, Fx: fx, Fy: fy));
    }
}

/// <summary>A real left click of the mouse, aimed at one control: the point is checked to belong to that control (nothing else is clicked), the cursor goes there and the button goes down and up as for a person. Clicks are sent one at a time, from
/// the dispatcher, in the order they were queued, and a click near the one before it waits until the system's double-click time has passed, so two clicks never merge into a double-click. UI thread only.</summary>
static class RealMouse
{
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Win32.POINT p);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out Win32.RECT r);

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public MOUSEINPUT mi; }
    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    /// <summary>Set by the replay: moves the replay's own window off a screen rectangle, and moves the mouse (the replay remembers where it was and puts it back). Null outside a replay (Tdoc, tests).</summary>
    public static Action<int, int, int, int>? AvoidHook;
    public static Action<int, int>? CursorHook;

    /// <summary>One click of the queue. <c>Late</c>: the target is not known until the click before it was made (a menu item of a drop-down that the click before opens); it is asked when the click is due and again every 150 ms until it answers or 40 tries have passed.</summary>
    sealed class Item
    {
        public Item(string what, IntPtr control, int x, int y, LateTarget? late = null, Func<string?>? check = null, Action? onFail = null, bool person = false) { What = what; Control = control; X = x; Y = y; Late = late; Check = check; OnFail = onFail; Person = person; }
        /// <summary>A failure of this click ends the test as needs-a-person (a toolbar button the replay may not click), as a button that is not found does.</summary>
        public readonly bool Person;
        public string What; public IntPtr Control; public int X, Y; public readonly LateTarget? Late; public int Tries;
        /// <summary>Asked just before the click: null = go on, else why this click must not be made (the point is not on the recorded item).</summary>
        public readonly Func<string?>? Check;
        /// <summary>Runs when this click or one queued after it failed (a menu the clicks opened is closed, so a retry starts clean).</summary>
        public readonly Action? OnFail;
    }
    public delegate (IntPtr Window, int X, int Y, Func<string?>? Check)? LateTarget(out string why);
    static readonly Queue<Item> Queued = new();
    static bool _scheduled;
    static long _lastAt;
    static int _lastX, _lastY;
    static IntPtr _lastControl;

    /// <summary>Is a click still waiting to be sent? The next palette step does not start before it was.</summary>
    public static bool Pending => Queued.Count > 0 || _scheduled;

    /// <summary>A replay ends or starts: no click of an earlier one is left.</summary>
    public static void Reset()
    {
        Queued.Clear(); _scheduled = false; _lastAt = 0; _lastControl = IntPtr.Zero;
        _gen++;   // a timer that is still waiting out the spacing belongs to the replay that ended: it never fires into the next one
        if (_timer != null) { _timer.Stop(); _timer = null; }
    }

    static int _gen;
    static System.Windows.Threading.DispatcherTimer? _timer;

    static bool On(IntPtr control, int x, int y) => WindowFromPoint(new Win32.POINT { X = x, Y = y }) == control;

    /// <summary>Null when the screen point is on <paramref name="control"/> itself and the control is shown. When another window covers it, the replay's own window is moved off the control and the control's window is raised (as a dialog's mouse click
    /// does), then the point is looked at again; only when it is still covered is that the reason.</summary>
    public static string? Prepare(IntPtr control, int x, int y)
    {
        if (!IsWindowVisible(control)) return "the palette is not showing (rolled up or hidden)";
        if (On(control, x, y)) return null;
        if (GetWindowRect(control, out var r)) AvoidHook?.Invoke(r.Left, r.Top, r.Right, r.Bottom);
        if (!On(control, x, y))
        {
            var root = GetAncestor(control, 2 /* GA_ROOT */);
            if (root != IntPtr.Zero) { BringWindowToTop(root); SetForegroundWindow(root); }
        }
        if (On(control, x, y)) return null;
        var at = WindowFromPoint(new Win32.POINT { X = x, Y = y });
        return at == IntPtr.Zero ? "the point is not on a window" : $"the control is covered by another window ({DialogReader.ClassOf(at)})";
    }

    /// <summary>Queues a click on <paramref name="control"/> at screen point (x, y); it is sent after the ones queued before it, from the dispatcher (a tree waits for the mouse inside a button-down, and a handler may open a sub form).</summary>
    public static void Enqueue(string what, IntPtr control, int x, int y, Func<string?>? check = null, Action? onFail = null, bool person = false)
    {
        Queued.Enqueue(new Item(what, control, x, y, null, check, onFail, person));
        Pump();
    }

    static bool Near(Item i) =>
        i.Late == null && (i.Control == _lastControl || (Math.Abs(i.X - _lastX) <= Math.Max(2, GetSystemMetrics(36 /* SM_CXDOUBLECLK */) / 2) && Math.Abs(i.Y - _lastY) <= Math.Max(2, GetSystemMetrics(37) / 2)));

    /// <summary>Queues a click whose target is found when it is due (<see cref="LateTarget"/>).</summary>
    public static void EnqueueLate(string what, LateTarget late, Action? onFail = null, bool person = false)
    {
        Queued.Enqueue(new Item(what, IntPtr.Zero, 0, 0, late, null, onFail, person));
        Pump();
    }

    static void Pump(long? waitOverride = null)
    {
        if (_scheduled || Queued.Count == 0) return;
        _scheduled = true;
        var next = Queued.Peek();
        long wait = waitOverride ?? PaletteClickKinds.SpacingWaitMs(_lastAt, Environment.TickCount64, GetDoubleClickTime(), Near(next));
        if (wait <= 0) { int g = _gen; DialogFill.Later("palette click", () => { if (g == _gen) Fire(); }); return; }
        int gen = _gen;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(wait) };
        timer.Tick += (_, _) => { timer.Stop(); if (_timer == timer) _timer = null; if (gen == _gen) Fire(); };
        _timer = timer;
        timer.Start();
    }

    static void Fire()
    {
        _scheduled = false;
        if (Queued.Count == 0) return;
        var i = Queued.Peek();
        Func<string?>? lateCheck = null;
        if (i.Late != null)
        {
            var found = i.Late(out var lateWhy);
            if (found == null)
            {
                if (++i.Tries < 40) { Pump(150); return; }
                Fail($"could not click {i.What}: {lateWhy}"); return;
            }
            i.Control = found.Value.Window; i.X = found.Value.X; i.Y = found.Value.Y; lateCheck = found.Value.Check;
        }
        Queued.Dequeue();
        try
        {
            if ((i.Check ?? lateCheck)?.Invoke() is { } bad) { Fail($"did not click {i.What}: {bad}", i); return; }
            if (Send(i.Control, i.X, i.Y, activate: i.Late == null && i.Check != null) is { } why) { Fail($"could not click {i.What}: {why}", i); return; }
            _lastAt = Environment.TickCount64; _lastX = i.X; _lastY = i.Y; _lastControl = i.Control;
        }
        catch (Exception ex)
        {
            // the step is already marked done: a click that threw is a failure of the replay, never a pass
            Log.Error("palette click", ex);
            Fail($"could not click {i.What}: {ex.Message}", i);
        }
        finally { Pump(); }
    }

    /// <summary>A click failed: the step fails with the reason, the clicks queued after it are dropped, and whatever menu they opened is closed.</summary>
    static void Fail(string why, Item? current = null)
    {
        PaletteFill.FailLater(why, needsPerson: current?.Person ?? (Queued.Count > 0 && Queued.Peek().Person));
        current?.OnFail?.Invoke();
        foreach (var q in Queued) q.OnFail?.Invoke();
        Queued.Clear();
    }

    /// <summary>The click: the point is looked at again (a window may have come over it), the cursor moves there (through the replay, which puts it back; and the click arrow, when the setting is on), the button goes down and up. Null when sent, else why not.</summary>
    static string? Send(IntPtr control, int x, int y, bool activate = false)
    {
        if (Prepare(control, x, y) is { } why) return why;
        // a WinForms ToolStrip in a window that is not the active one takes the first click only to activate it (it does nothing else): the window comes to the front first (a Tdoc picture brings AutoCAD's main window up
        // between two steps, so the palette's own frame was not the active window any more)
        if (activate && GetAncestor(control, 2 /* GA_ROOT */) is var root && root != IntPtr.Zero) Native.Front(root);
        ClickArrows.At(x, y);
        if (CursorHook != null) CursorHook(x, y); else SetCursorPos(x, y);
        // SendInput says which physical button: with the buttons swapped (SM_SWAPBUTTON) the physical right one is the primary button
        bool swapped = GetSystemMetrics(23 /* SM_SWAPBUTTON */) != 0;
        var down = new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = swapped ? 0x0008u : 0x0002u } };
        var up = new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = swapped ? 0x0010u : 0x0004u } };
        return SendInput(2, new[] { down, up }, Marshal.SizeOf<INPUT>()) == 2 ? null : "the mouse click could not be sent";
    }
}

/// <summary>Where a replayed intermediate click goes: the control, the screen point, a rectangle round it (the Tdoc arrow), a note for the result (added when the click was queued, never on a retry), and, for a node of a tree that shows no +/- button, what to do instead.</summary>
sealed record ClickTarget(IntPtr Control, int X, int Y, ScreenRect Rect, string What, string? Note = null, Action? Instead = null, bool Nothing = false, object? MenuRoot = null, List<string>? MenuPath = null, Func<string?>? Check = null);

/// <summary>Replay side: does a recorded intermediate click of a WinForms palette with a real mouse click on the control found by name, path or place. Everything that can be checked is checked on the tick (the control is there, shown, not covered):
/// the click itself is queued (<see cref="RealMouse"/>). A control that is not found, or not clickable, is a problem the replay retries and then gives to a person; never a pass. <see cref="Resolve"/> is the one finder, also for the Tdoc arrow. UI thread only.</summary>
static class PaletteClickFill
{
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out Win32.RECT r);

    /// <summary>Null when the click is queued (or there is nothing to click), else what stopped it.</summary>
    public static string? Run(Step s, IntPtr page, bool hidden, DialogReader.Win32Scan win32)
    {
        try
        {
            var (t, problem) = Resolve(s, page, hidden, win32, mutate: true);
            if (t == null) return problem;
            if (t.Nothing) { if (t.Note != null) PaletteFill.AddNote(t.Note); return null; }
            if (t.Instead != null) { if (t.Note != null) PaletteFill.AddNote(t.Note); DialogFill.Later("tree toggle", t.Instead); return null; }
            if (RealMouse.Prepare(t.Control, t.X, t.Y) is { } why) return why;
            // a drop-down that is open already (a retry of this step, or opened by hand) is not clicked again: that would close it
            bool open = t.MenuRoot != null && ToolStripControls.IsOpen(t.MenuRoot);
            bool person = t.Check != null;   // a toolbar click (it alone has a check): a refusal at the click ends the test as needs-a-person, like a button that is not found
            if (!open) RealMouse.Enqueue(t.What, t.Control, t.X, t.Y, t.Check, t.MenuRoot != null ? () => ToolStripControls.Close(t.MenuRoot) : null, person);
            if (t.MenuRoot != null && t.MenuPath != null) QueueMenu(t);
            if (t.Note != null) PaletteFill.AddNote(t.Note);   // once the click is queued: a step that is tried again does not repeat it
            return null;
        }
        catch (Exception ex) { Log.Error("palette click", ex); return ex.Message; }
    }

    /// <summary>Tdoc: the screen rectangle of the control the step clicks, found as the replay finds it, without touching the palette; null when it is not there.</summary>
    public static ScreenRect? TdocRect(Step s, IntPtr page, bool hidden)
    {
        try
        {
            var scan = DialogReader.Scan(page, hidden, readTicks: false);
            var (t, _) = Resolve(s, page, hidden, scan, mutate: false);
            if (t is not { Nothing: false, Instead: null }) return null;
            // a toolbar item's rectangle is used only when it lies inside the toolbar's own client area
            return t.What.StartsWith("toolbar button", StringComparison.Ordinal) ? ToolStripControls.InsideStrip(t.Control, t.Rect) : t.Rect;
        }
        catch (Exception ex) { Log.Error("tdoc palette click", ex); return null; }
    }

    /// <summary>The one finder of the control a step clicks. <paramref name="mutate"/>: a replay (opens the parents of a tree item, scrolls it into view); Tdoc only looks.</summary>
    public static (ClickTarget? Target, string? Problem) Resolve(Step s, IntPtr page, bool hidden, DialogReader.Win32Scan win32, bool mutate) => s.ClickKind switch
    {
        PaletteClickKinds.Tree or PaletteClickKinds.Expand or PaletteClickKinds.Collapse => ResolveTree(s, win32, mutate),
        PaletteClickKinds.ListView => ResolveList(s, page, hidden, mutate),
        PaletteClickKinds.Image => ResolveImage(s, page, hidden),
        PaletteClickKinds.Point => ResolvePoint(s, page, hidden),
        PaletteClickKinds.ToolStrip => ResolveToolStrip(s, page, hidden),
        PaletteClickKinds.Toolbar => ResolveToolbar(s, page, hidden),
        _ => (null, $"unknown palette click kind {s.ClickKind}"),
    };

    static (int X, int Y) Screen(IntPtr h, int cx, int cy)
    {
        var p = new Win32.POINT { X = cx, Y = cy };
        Win32.ClientToScreen(h, ref p);
        return (p.X, p.Y);
    }

    static ScreenRect Around(int x, int y, int r) => new(x - r, y - r, x + r, y + r);

    // ---- trees -----------------------------------------------------------------------------------------------------------------------

    /// <summary>The item at <paramref name="levels"/> (a blank item is "(blank)" on both sides); <paramref name="expand"/> opens the parents on the way (a replay), else only looks.</summary>
    static TreeFound<IntPtr> FindTreeItem(IntPtr tree, IReadOnlyList<string> levels, bool expand) =>
        TreePath.Find(levels, IntPtr.Zero, p => TreeControl.Children(tree, p), it => PaletteClickKinds.LevelText(TreeControl.Text(tree, it)), expand ? p => TreeControl.Expand(tree, p) : null);

    /// <summary>Tdoc: the screen rectangle of the label of the tree item at <paramref name="path"/>, found as the replay finds it. Only looks: nothing is opened, selected or scrolled (a palette's picture is taken before its step
    /// runs, and opening a node runs the owner's handlers). Null when the item is not there, a parent is collapsed, or the item is not inside the tree's client area.</summary>
    public static ScreenRect? TreeItemRect(IntPtr tree, string path)
    {
        try
        {
            var levels = PaletteClickKinds.ClickLevels(path);
            if (levels.Count == 0 || !Native.OwnedByThisThread(tree)) return null;
            var found = FindTreeItem(tree, levels, expand: false);
            if (found.Problem != null) return null;
            for (int k = 0; k < found.Items.Count - 1; k++)   // every parent open: TVM_GETITEMRECT would answer for a hidden item otherwise
                if ((TreeControl.State(tree, found.Items[k], TreeControl.TVIS_EXPANDED) & TreeControl.TVIS_EXPANDED) == 0) return null;
            var item = found.Items[^1];
            if (TreeControl.TextRect(tree, item) is not { } r || !GetClientRect(tree, out var rc)) return null;
            if (r.L < 0 || r.T < 0 || r.R > rc.Right + 1 || r.B > rc.Bottom + 1 || r.R - r.L < 2 || r.B - r.T < 2) return null;
            var (l, t) = Screen(tree, r.L, r.T); var (rr, b) = Screen(tree, r.R, r.B);
            return new ScreenRect(l, t, rr, b);
        }
        catch (Exception ex) { Log.Error("tdoc tree item rect", ex); return null; }
    }

    static (ClickTarget?, string?) ResolveTree(Step s, DialogReader.Win32Scan win32, bool mutate)
    {
        var at = FillMatcher.Match(new[] { new FieldChange(s.ClickTree, s.ClickTreeLabel, "tree", "", "") }, win32.Fields.Select(LiveField.Of).ToList())[0];
        if (at < 0 || win32.Handles[at].Count == 0) return (null, $"no tree \"{s.ClickTreeLabel}\" in the {s.DialogTitle} palette (yet)");
        var tree = win32.Handles[at][0];
        if (!Native.OwnedByThisThread(tree)) return (null, "the tree belongs to another thread");
        var levels = PaletteClickKinds.ClickLevels(s.ClickPath);
        if (levels.Count == 0) return (null, "the recorded click names no tree item (an empty path)");
        // a replay opens every parent on the way, so the item is there to click; Tdoc only looks (a blank item is "(blank)" on both sides)
        var found = FindTreeItem(tree, levels, mutate);
        if (found.Problem != null) return (null, found.Problem);
        var item = found.Items[^1];
        if (mutate) Native.SendNum(tree, TreeControl.TVM_ENSUREVISIBLE, IntPtr.Zero, item, 2, 2000, out _);
        if (s.ClickKind == PaletteClickKinds.Tree)
        {
            var r = TreeControl.TextRect(tree, item);
            if (r == null) return (null, $"the item {s.ClickPath} is not in view");
            var (x, y) = Screen(tree, Math.Min(r.Value.L + 8, (r.Value.L + r.Value.R) / 2), (r.Value.T + r.Value.B) / 2);
            var (l, t) = Screen(tree, r.Value.L, r.Value.T); var (rr, b) = Screen(tree, r.Value.R, r.Value.B);
            return (new ClickTarget(tree, x, y, new ScreenRect(l, t, rr, b), "tree item " + s.ClickPath), null);
        }
        bool expanded = (TreeControl.State(tree, item, TreeControl.TVIS_EXPANDED) & TreeControl.TVIS_EXPANDED) != 0;
        bool wantOpen = s.ClickKind == PaletteClickKinds.Expand;
        string what = (wantOpen ? "expand " : "collapse ") + s.ClickPath;
        if (expanded == wantOpen)
            return (new ClickTarget(tree, 0, 0, default, what, $"{s.ClickPath} is already {(wantOpen ? "expanded" : "collapsed")}; the {(wantOpen ? "expand" : "collapse")} click was not made", Nothing: true), null);
        if (ButtonPoint(tree, item) is { } bp)
        {
            var (x, y) = Screen(tree, bp.X, bp.Y);
            return (new ClickTarget(tree, x, y, Around(x, y, 8), what), null);
        }
        // a tree that shows +/- buttons must have one for this node: failing is the truth, a message would only pretend a person clicked it
        // (a top-level node of a tree without TVS_LINESATROOT has no button either: it is opened by message, as for a tree with no buttons)
        if (PaletteClickKinds.ButtonExpected((uint)DialogReader.GetWindowLong(tree, -16), levels.Count == 1))
            return (null, $"the +/- button of {s.ClickPath} was not found in the tree (it shows buttons; the item may be scrolled out of view)");
        // a tree or a node without a +/- button (opened by a double-click or the keyboard): the tree's own message, and a warning
        return (new ClickTarget(tree, 0, 0, default, what, $"the tree shows no +/- button for this node; {s.ClickPath} was {(wantOpen ? "expanded" : "collapsed")} by message",
            () => { if (wantOpen) TreeControl.Expand(tree, item); else Native.SendNum(tree, TreeControl.TVM_EXPAND, (IntPtr)1 /* TVE_COLLAPSE */, item, 2, 2000, out _); }), null);
    }

    /// <summary>The client point of an item's +/- button: found by hit test (TVHT_ONITEMBUTTON) along the item's line over the whole client width (the line starts at the tree's left edge, indent included; a scrolled tree is
    /// in client coordinates too); null when the line has none.</summary>
    internal static (int X, int Y)? ButtonPoint(IntPtr tree, IntPtr item)
    {
        var buf = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.WriteInt64(buf, 8, 0); Marshal.WriteIntPtr(buf, item);
            Native.SendNum(tree, TreeControl.TVM_GETITEMRECT, IntPtr.Zero, buf, 2, 2000, out var ok);
            if (ok == IntPtr.Zero) return null;
            int t = Marshal.ReadInt32(buf, 4), b = Marshal.ReadInt32(buf, 12), y = (t + b) / 2;
            if (!GetClientRect(tree, out var rc)) return null;
            for (int x = 0; x < rc.Right; x++)
            {
                var (hit, flags) = TreeControl.HitTest(tree, x, y);
                if (hit == item && (flags & 0x10) != 0) return (x, y);
            }
            return null;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // ---- list views -------------------------------------------------------------------------------------------------------------------

    static (ClickTarget?, string?) ResolveList(Step s, IntPtr page, bool hidden, bool mutate)
    {
        var hit = s.Hit ?? new PaletteHit();
        var list = MenuListView.Find(page, hit.Control, hidden);
        if (list == IntPtr.Zero) return (null, $"no list \"{(hit.ControlLabel.Length > 0 ? hit.ControlLabel : hit.Control)}\" in the {s.DialogTitle} palette (yet)");
        var texts = MenuListView.AllTexts(list);
        if (texts == null) return (null, "the list belongs to another thread");
        int row = ListViewRows.Resolve(texts, hit.Item, hit.Occurrence, hit.ItemIndex, out var problem);
        if (row < 0) return (null, problem ?? $"no item \"{hit.Item}\" in the list");
        if (MenuListView.RowClickPoint(list, row, scroll: mutate) is not { } p) return (null, $"the item \"{hit.Item}\" is not in view");
        return (new ClickTarget(list, p.X, p.Y, Around(p.X, p.Y, 16), "list item " + hit.Item), null);
    }

    // ---- toolbars -----------------------------------------------------------------------------------------------------------------------

    /// <summary>A WinForms ToolStrip item: the strip by `Name`, else its place among the page's strips; the item by `Name`, the recorded name or its place (<see cref="PaletteToolbar.Find"/>); its bounds in the strip's client
    /// coordinates, on the screen. A disabled item, an item on the overflow menu and an item that is not there are problems (the replay retries, then gives the step to a person): never a pass.</summary>
    static (ClickTarget?, string?) ResolveToolStrip(Step s, IntPtr page, bool hidden)
    {
        var hit = s.Hit ?? new PaletteHit();
        var strips = ToolStripControls.StripsOf(page, hidden: true);   // the same rule as the recorder's numbering (StripNumber)
        int at = hit.Strip.Length > 0 ? strips.FindIndex(t => string.Equals(ToolStripControls.NameOf(t.Strip), hit.Strip, StringComparison.OrdinalIgnoreCase)) : -1;
        // a strip recorded with a name is found by that name only; the place is for strips that had none
        if (at < 0 && hit.Strip.Length == 0 && hit.StripIndex >= 1 && hit.StripIndex <= strips.Count) at = hit.StripIndex - 1;
        if (at < 0) return (null, hit.Strip.Length > 0 ? $"toolbar \"{hit.Strip}\" not found in the {s.DialogTitle} palette (yet)" : $"no toolbar (ToolStrip) in the {s.DialogTitle} palette (yet)");
        var strip = strips[at].H;
        var buttons = ToolStripControls.Buttons(strips[at].Strip);
        var found = PaletteToolbar.Find(buttons.Select(b => b.Candidate).ToList(), s.Button ?? "", s.ButtonIndex, hit.ItemName, hit.Position, hit.Count, 0);
        if (found.At < 0) return (null, found.Problem);
        var item = buttons[found.At];
        var name = PaletteToolbar.Display(item.Candidate).Name;
        if (item.OnOverflow) return (null, $"the toolbar button \"{name}\" is on the toolbar's overflow menu (the palette is too narrow to show it): make the palette wider");
        if (!item.Enabled) return (null, $"the toolbar button \"{name}\" is disabled");
        bool menu = hit.Menu.Length > 0;
        if (menu && !item.DropDown) return (null, $"the toolbar button \"{name}\" has no drop-down menu now (recorded: {hit.Menu})");
        if (!menu && item.DropDown && !item.Split) return (null, $"the toolbar button \"{name}\" opens a menu now (recorded as a plain click): not clicked");
        if (ToolStripControls.ScreenOf(strip, item, arrow: menu) is not { } sc) return (null, $"the toolbar button \"{name}\" is not in view");
        var obj = item.Obj;
        return (new ClickTarget(strip, sc.X, sc.Y, sc.Rect, "toolbar button " + name, found.Note, MenuRoot: menu ? item.Obj : null, MenuPath: menu ? TreePath.Split(hit.Menu) : null,
            Check: () => ToolStripControls.PointIsOn(strip, obj, sc.X, sc.Y, menu)), null);
    }

    /// <summary>A toolbar (ToolbarWindow32) button: the toolbar by `Name`, else its place; the button by command id, else the recorded name or its place; clicked at the middle of its rectangle. A button whose whole face opens a menu is left to
    /// a person (its menu cannot be closed again by the replay).</summary>
    static (ClickTarget?, string?) ResolveToolbar(Step s, IntPtr page, bool hidden)
    {
        var hit = s.Hit ?? new PaletteHit();
        var bars = PaletteClickDetect.Children(page, hidden: true).Where(h => DialogReader.Segment(DialogReader.ClassOf(h)) == "toolbarwindow32" && ToolbarControl.Ok(h)).ToList();
        int at = hit.Strip.Length > 0 ? bars.FindIndex(h => string.Equals(WinFormsControls.NamesOf(h)?.Name, hit.Strip, StringComparison.OrdinalIgnoreCase)) : -1;
        if (at < 0 && hit.Strip.Length == 0 && hit.StripIndex >= 1 && hit.StripIndex <= bars.Count) at = hit.StripIndex - 1;
        if (at < 0 && hit.Strip.Length == 0 && hit.Cmd > 0) at = bars.FindIndex(h => ToolbarControl.Find(h, hit.Cmd) != null);
        if (at < 0) return (null, hit.Strip.Length > 0 ? $"toolbar \"{hit.Strip}\" not found in the {s.DialogTitle} palette (yet)" : $"no toolbar in the {s.DialogTitle} palette (yet)");
        var tb = bars[at];
        var buttons = ToolbarControl.Buttons(tb);
        // a tool tip is asked of the program for the button the id names only; every button's only when the id found nothing
        var found = PaletteToolbar.Find(PaletteClickDetect.Candidates(tb, buttons, hit.Cmd), s.Button ?? "", s.ButtonIndex, "", hit.Position, hit.Count, hit.Cmd);
        if (found.At < 0) found = PaletteToolbar.Find(PaletteClickDetect.Candidates(tb, buttons, -1, all: true), s.Button ?? "", s.ButtonIndex, "", hit.Position, hit.Count, hit.Cmd);
        if (found.At < 0) return (null, found.Problem);
        var b = buttons[found.At];
        var name = s.Button is { Length: > 0 } n && n != ButtonNames.Unnamed ? n : "no. " + (found.At + 1);
        if (!b.Enabled) return (null, $"the toolbar button \"{name}\" is disabled");
        if (ToolbarPress.StopFor(b.Style, ToolbarControl.ExtendedStyle(tb)) is { } stop) return (null, stop);
        if (ToolbarControl.ItemScreenRect(tb, b.Index) is not { } r || r.R <= r.L) return (null, $"the toolbar button \"{name}\" is not in view");
        // a click goes to the main part of the button: of a split drop-down button the part left of its arrow
        int cx = (r.L + r.R) / 2, cy = (r.T + r.B) / 2, bi = b.Index, bid = b.Id;
        var arrowPart = ToolbarControl.DropDownArrowRect(tb, b);
        var rect = new ScreenRect(r.L, r.T, r.R, r.B);
        if (arrowPart is { } ar && ToolbarControl.ButtonRect(tb, b.Index) is { } br)
        {
            var o = new Win32.POINT { X = br.L, Y = br.T }; Win32.ClientToScreen(tb, ref o);
            int mainRight = o.X + (ar.L - br.L);
            if (mainRight <= r.L) return (null, $"the toolbar button \"{name}\" has no main part to click");
            cx = (r.L + mainRight) / 2; rect = new ScreenRect(r.L, r.T, mainRight, r.B);
        }
        return (new ClickTarget(tb, cx, cy, rect, "toolbar button " + name, found.Note,
            Check: () =>
            {
                // at the click: the point is on that button (by index AND command id: the toolbar may have been rebuilt while the click waited) and not on its arrow
                var p = new Win32.POINT { X = cx, Y = cy }; Win32.ScreenToClient(tb, ref p);
                int hit = ToolbarControl.HitTest(tb, p.X, p.Y);
                if (hit != bi || !ToolbarControl.Buttons(tb).Any(q => q.Index == hit && q.Id == bid)) return "the click point is on another toolbar button";
                var now = ToolbarControl.Buttons(tb).FirstOrDefault(q => q.Index == hit);
                return ToolbarControl.DropDownArrowRect(tb, now) is { } a2 && ToolbarLabel.Inside(p.X, p.Y, a2.L, a2.T, a2.R, a2.B) ? "the click point is on the drop-down arrow of the button" : null;
            }), null);
    }

    /// <summary>The menu choices of a drop-down: one queued click per level, each found when it is due (the menu opens when the click before it was made).</summary>
    static void QueueMenu(ClickTarget t)
    {
        Func<object?> parent = () => t.MenuRoot;
        foreach (var level in t.MenuPath!)
        {
            var text = level;
            var from = parent;
            object? chosen = null;
            RealMouse.EnqueueLate($"menu item {text} of {t.What}", (out string why) =>
            {
                why = "the menu item that opens this menu was not found";
                if (from() is not { } p) return null;
                var m = ToolStripControls.MenuItemTarget(p, text, out why);
                if (m == null) return null;
                chosen = m.Value.Item;
                var menuItem = m.Value.Item; var ddWindow = m.Value.Window; int mx = m.Value.X, my = m.Value.Y;
                return (ddWindow, mx, my, () => ToolStripControls.MenuPointIsOn(ddWindow, menuItem, mx, my));   // just before the click: the point is on THAT menu item
            }, () => ToolStripControls.Close(t.MenuRoot!), person: true);
            parent = () => chosen;
        }
    }

    // ---- images and unnamed controls ----------------------------------------------------------------------------------------------------

    static (ClickTarget?, string?) ResolveImage(Step s, IntPtr page, bool hidden)
    {
        var pics = PaletteClickDetect.Pictures(page, hidden);
        var names = pics.Names(s.ButtonFrom);
        int at = PaletteClickKinds.FindImage(names, s.Button ?? "", s.ButtonFrom, s.ButtonIndex);
        string? note = null;
        if (at < 0 && ButtonNames.IsFallback(s.ButtonFrom) && ButtonNames.PositionAllowed(s.ButtonLegacyIndex, s.ButtonNoTextCount, pics.H.Count))
        {
            at = s.ButtonLegacyIndex - 1;
            note = $"image found by position (no. {s.ButtonLegacyIndex}); its name changed (recorded as \"{s.Button}\")";
        }
        if (at < 0) return (null, $"no image \"{s.Button}\" in the {s.DialogTitle} palette (yet)");
        var h = pics.H[at];
        var p = PointOn(h, s.Hit);
        var rect = Win32.GetWindowRect(h, out var wr) ? new ScreenRect(wr.Left, wr.Top, wr.Right, wr.Bottom) : Around(p.X, p.Y, 12);
        return (new ClickTarget(h, p.X, p.Y, rect, "image " + s.Button, note), null);
    }

    static (ClickTarget?, string?) ResolvePoint(Step s, IntPtr page, bool hidden)
    {
        var hit = s.Hit ?? new PaletteHit();
        var same = PaletteClickDetect.Children(page, hidden).Where(c => DialogReader.Segment(DialogReader.ClassOf(c)) == hit.Class).ToList();
        var live = same.Select(c => new PaletteClickKinds.Candidate(hit.Class, WinFormsControls.ClickKindOf(c)?.Name ?? "")).ToList();
        int at = PaletteClickKinds.FindPoint(live, hit, out var problem);
        if (at < 0) return (null, problem);
        var p = PointOn(same[at], hit);
        return (new ClickTarget(same[at], p.X, p.Y, Around(p.X, p.Y, 10), "control " + (hit.Name.Length > 0 ? hit.Name : hit.Class)), null);
    }

    /// <summary>The screen point on control <paramref name="h"/> at the recorded fractions of its client area (its middle when none).</summary>
    static (int X, int Y) PointOn(IntPtr h, PaletteHit? hit)
    {
        GetClientRect(h, out var rc);
        var (cx, cy) = PaletteClickKinds.PointIn(hit ?? new PaletteHit(), rc.Right, rc.Bottom);
        return Screen(h, cx, cy);
    }
}
