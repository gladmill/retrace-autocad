using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.Windows;
using Retrace.Core;
using Retrace.Engine.Recordings;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Retrace.AutoCad;

/// <summary>
/// RETRACECAPTURERIBBON: shows each tab of the live ribbon in turn and writes what AutoCAD drew, for the docs build to use instead of the
/// ribbon wireframes: <c>ribbon__&lt;tab&gt;.png</c> (the ribbon with that tab active), one <c>ribbon__&lt;tab&gt;__&lt;n&gt;.png</c> per panel,
/// and <c>ribbon__&lt;tab&gt;.json</c> with the tab's id and title and every panel's and item's id, text and rectangle in that picture
/// (device pixels, read from the ribbon's WPF elements: the element whose DataContext is the panel or item). Only tabs the ribbon has can
/// be captured: tabs of the current workspace, not contextual ones and not Retrace's own. The active tab is restored at the end.
/// A dispatcher timer drives it (switch tab, let it draw, capture), so AutoCAD stays responsive. UI thread only.
/// </summary>
public sealed class RibbonCapture
{
    // process-wide, not a static: a second load of this assembly must see a running capture too
    const string RunningKey = "Retrace.ribboncapture.running";
    const int SettleMs = 900;
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    readonly RibbonControl _ribbon;
    readonly Queue<RibbonTab> _queue;
    readonly string _out;
    readonly RibbonTab? _restore;
    readonly Editor _ed;
    readonly DispatcherTimer _timer;
    readonly List<string> _done = new();
    readonly Dictionary<string, string> _failed = new(StringComparer.OrdinalIgnoreCase);   // tab id (or title) -> why; dropped once captured
    readonly Action<string>? _then;   // Build Docs: goes on once the capture is done, with its summary
    DateTime _shownAt;
    RibbonTab? _job;
    bool _madeVisible;   // the job's tab was hidden (not in this workspace): hidden again after its picture
    readonly Queue<string> _workspaces = new();   // Build Docs: workspaces still to switch to (their own tabs, such as the 3D ones)
    readonly HashSet<string> _captured = new(StringComparer.OrdinalIgnoreCase);   // tab ids done, so another workspace only adds new ones
    string? _workspace;   // the workspace to go back to; null = none was switched
    DateTime _switchedAt = DateTime.MinValue;

    RibbonCapture(RibbonControl ribbon, IEnumerable<RibbonTab> tabs, string outDir, Editor ed, Action<string>? then = null)
    {
        _ribbon = ribbon; _queue = new Queue<RibbonTab>(tabs); _out = outDir; _ed = ed; _restore = ribbon.ActiveTab; _then = then;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += (_, _) => Log.Safe("ribbon capture", Tick);
    }

    public static void Run()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument ?? throw new InvalidOperationException("open a drawing first");
        var ed = doc.Editor;
        if (Running) { ed.WriteMessage("\nRETRACECAPTURERIBBON: a capture is already running.\n"); return; }
        var ribbon = ComponentManager.Ribbon;
        if (ribbon == null) { ed.WriteMessage("\nRETRACECAPTURERIBBON: there is no ribbon (RIBBON shows it).\n"); return; }
        var o = new PromptKeywordOptions("\nCapture which ribbon tabs [All/Shown/Current] <All>: ", "All Shown Current") { AllowNone = true };
        var k = Ask.Keywords(ed, o);
        if (k.Status == PromptStatus.Cancel) return;
        var which = k.Status == PromptStatus.OK ? k.StringResult : "All";
        var tabs = Capturable(ribbon, hidden: which == "All");
        if (which == "Current") tabs = ribbon.ActiveTab is { } active && tabs.Contains(active) ? new() { active } : new();
        if (tabs.Count == 0)
        {
            ed.WriteMessage(which == "Current" ? "\nRETRACECAPTURERIBBON: the active tab cannot be captured (Retrace's own).\n" : "\nRETRACECAPTURERIBBON: the ribbon has no tabs to capture.\n");
            return;
        }
        var def = RetraceSettings.Load().Captures;
        var p = Ask.String(ed, $"\nOutput folder <{def}>: ", Ask.Of.Folder, "folder:captures", initial: def);
        if (p.Status == PromptStatus.Cancel) return;
        var outDir = string.IsNullOrWhiteSpace(p.StringResult) ? def : p.StringResult.Trim().Trim('"');
        Directory.CreateDirectory(outDir);

        AppDomain.CurrentDomain.SetData(RunningKey, true);
        ed.WriteMessage($"\nRETRACECAPTURERIBBON: {tabs.Count} tab(s) to {outDir}. Leave the AutoCAD window as it is until it is done.\n");
        new RibbonCapture(ribbon, tabs, outDir, ed)._timer.Start();
    }

    /// <summary>The tabs to capture: those the ribbon shows, and with <paramref name="hidden"/> also its hidden ones (tabs of other workspaces)
    /// and its contextual ones (Text Editor, Array...), which are shown one at a time for their picture. Never Retrace's own tab.</summary>
    static List<RibbonTab> Capturable(RibbonControl ribbon, bool hidden) =>
        ribbon.Tabs.Where(t => !OwnCommands.IsOwnTab(t.Id) && (hidden || (t.IsVisible && !t.IsContextualTab))).ToList();

    /// <summary>Whether a ribbon capture runs now (this command's or Build Docs').</summary>
    public static bool Running => AppDomain.CurrentDomain.GetData(RunningKey) != null;

    /// <summary>Build Docs: captures every tab (shown, hidden, contextual) to <paramref name="outDir"/>, then, with <paramref name="workspaces"/>,
    /// the tabs only those other workspaces load; then calls <paramref name="then"/> with a one-line summary. Null when started, else why not.</summary>
    public static string? StartForDocs(string outDir, Editor ed, Action<string> then, out int count, IReadOnlyList<string>? workspaces = null)
    {
        count = 0;
        var ribbon = ComponentManager.Ribbon;
        if (ribbon == null) return "there is no ribbon (RIBBON shows it)";
        if (Running) return "a ribbon capture is running already";
        var tabs = Capturable(ribbon, hidden: true);
        if (tabs.Count == 0) return "the ribbon has no tabs to capture";
        Directory.CreateDirectory(outDir);
        AppDomain.CurrentDomain.SetData(RunningKey, true);
        count = tabs.Count;
        var c = new RibbonCapture(ribbon, tabs, outDir, ed, then);
        // then the other workspaces: AutoCAD loads a workspace's own tabs (3D Modeling's Solid, Surface, Mesh...) only when it is current
        string? current = null;
        try { current = AcApp.GetSystemVariable("WSCURRENT") as string; } catch (Exception ex) { Log.Error("ribbon capture WSCURRENT", ex); }
        if (current != null)
            foreach (var w in (workspaces ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Where(w => !string.Equals(w, current, StringComparison.OrdinalIgnoreCase)))
                c._workspaces.Enqueue(w);
        if (c._workspaces.Count > 0) c._workspace = current;
        c._timer.Start();
        return null;
    }

    /// <summary>The queue is done: the next workspace, if any. Its tabs not captured yet are queued once its ribbon has been built.
    /// False when there is none left.</summary>
    bool NextWorkspace()
    {
        if (_switchedAt != DateTime.MinValue)
        {
            if ((DateTime.Now - _switchedAt).TotalMilliseconds < 3000) return true;   // let it build its ribbon
            _switchedAt = DateTime.MinValue;
            foreach (var t in Capturable(_ribbon, hidden: true).Where(t => t.Id == null || !_captured.Contains(t.Id))) _queue.Enqueue(t);
            return true;
        }
        while (_workspaces.Count > 0)
        {
            var w = _workspaces.Dequeue();
            try { AcApp.SetSystemVariable("WSCURRENT", w); _switchedAt = DateTime.Now; return true; }
            catch (Exception ex) { Log.Info($"ribbon capture: cannot switch to workspace {w}: {ex.Message}"); }
        }
        return false;
    }

    void Tick()
    {
        if (_job == null)
        {
            if (_queue.Count == 0 && NextWorkspace()) return;
            if (_queue.Count == 0) { Finish(); return; }
            _job = _queue.Dequeue();
            try { Show(_job); }
            catch (Exception ex)
            {
                Log.Error("ribbon capture show " + _job.Title, ex);
                Fail(_job, ex.Message);
                var shown = _job; Log.Safe("ribbon capture hide", () => Hide(shown));   // a hidden tab made visible must not stay so
                _job = null;
                return;
            }
            _shownAt = DateTime.Now;
            return;
        }
        if ((DateTime.Now - _shownAt).TotalMilliseconds < SettleMs) return;
        var tab = _job;
        _job = null;
        try
        {
            var problem = Capture(tab);
            if (problem == null)
            {
                _done.Add(tab.Title ?? tab.Id ?? "?");
                if (tab.Id != null) _captured.Add(tab.Id);
                _failed.Remove(Key(tab));
            }
            else { Fail(tab, problem); Log.Info($"ribbon capture {tab.Title}: {problem}"); }
        }
        catch (Exception ex) { Log.Error("ribbon capture " + tab.Title, ex); Fail(tab, ex.Message); }
        finally { Log.Safe("ribbon capture hide", () => Hide(tab)); }
    }

    static string Key(RibbonTab t) => t.Id ?? t.Title ?? "?";
    void Fail(RibbonTab t, string why) => _failed[Key(t)] = $"{t.Title}: {why}";

    /// <summary>Makes the tab the active one: a contextual tab through ShowContextualTab, a hidden one by making it visible for the moment.
    /// AutoCAD fills a hidden tab's panels when it is shown (seen in R25.1: 0 panels before, 7 after).</summary>
    void Show(RibbonTab tab)
    {
        _madeVisible = false;
        if (tab.IsContextualTab) _ribbon.ShowContextualTab(tab, false, true);
        else
        {
            if (!tab.IsVisible) { tab.IsVisible = true; _madeVisible = true; }
            _ribbon.ActiveTab = tab;
        }
    }

    void Hide(RibbonTab tab)
    {
        if (tab.IsContextualTab) _ribbon.HideContextualTab(tab);
        else if (_madeVisible) tab.IsVisible = false;
        _madeVisible = false;
    }

    DateTime _restoredAt = DateTime.MinValue;

    void Finish()
    {
        // back to the first workspace; its ribbon is rebuilt after that, so the tab is restored on a later tick
        if (_workspace != null && _restoredAt == DateTime.MinValue)
        {
            try { AcApp.SetSystemVariable("WSCURRENT", _workspace); } catch (Exception ex) { Log.Error("ribbon capture restore workspace", ex); }
            _restoredAt = DateTime.Now;
            return;
        }
        if (_restoredAt != DateTime.MinValue && (DateTime.Now - _restoredAt).TotalMilliseconds < 3000) return;
        _timer.Stop();
        AppDomain.CurrentDomain.SetData(RunningKey, null);
        try
        {
            // the same tab, or the one with its id after the rebuild
            var back = _restore != null && _ribbon.Tabs.Contains(_restore) ? _restore : _ribbon.Tabs.FirstOrDefault(t => _restore?.Id != null && t.Id == _restore.Id);
            if (back != null) _ribbon.ActiveTab = back;
        }
        catch (Exception ex) { Log.Error("ribbon capture restore", ex); }
        var msg = $"{_done.Count} ribbon tab(s) captured to {_out}" + (_failed.Count > 0 ? $"; not captured: {string.Join("; ", _failed.Values)}" : "");
        if (_then != null) { Log.Safe("ribbon capture then", () => _then(msg)); return; }
        try { (AcApp.DocumentManager.MdiActiveDocument?.Editor ?? _ed).WriteMessage($"\nRETRACECAPTURERIBBON: {msg}\n"); } catch (Exception ex) { Log.Info(msg + $" ({ex.Message})"); }
    }

    /// <summary>One element per panel / item: the outermost visible element whose DataContext it is. Rectangles in device pixels,
    /// relative to the ribbon's top left.</summary>
    sealed record Box(object Of, Rect R);

    string? Capture(RibbonTab tab)
    {
        if (PresentationSource.FromVisual(_ribbon) is not { CompositionTarget: { } target }) return "the ribbon is not shown";
        var scale = target.TransformToDevice;
        var origin = _ribbon.PointToScreen(new Point(0, 0));
        int w = (int)Math.Round(_ribbon.ActualWidth * scale.M11), h = (int)Math.Round(_ribbon.ActualHeight * scale.M22);
        if (w < 50 || h < 30) return "the ribbon is too small (minimized?)";
        var pic = ShotService.CaptureRect((int)origin.X, (int)origin.Y, (int)origin.X + w, (int)origin.Y + h);
        if (pic == null) return "the screen could not be read";

        var boxes = new Dictionary<object, Rect>(ReferenceEqualityComparer.Instance);
        Walk(_ribbon, boxes, scale);
        var stem = "ribbon__" + Slug(tab.Id ?? tab.Title ?? "tab");
        var panels = new List<object>();
        int n = 0;
        foreach (var panel in tab.Panels)
        {
            if (!boxes.TryGetValue(panel, out var pr)) continue;
            n++;
            var (crop, at) = Crop(pic, pr);
            string? file = null;
            if (crop != null) { file = $"{stem}__{n}.png"; WritePng(file, crop); }
            var items = new List<object>();
            foreach (var item in Items(panel))
                if (boxes.TryGetValue(item, out var ir))
                    items.Add(new { id = item.Id, text = item.Text, name = item.Name, type = item.GetType().Name, rect = Ints(ir) });
            panels.Add(new { id = panel.Source?.Id, title = panel.Source?.Title, rect = Ints(pr), picture = file, pictureRect = crop != null ? new[] { at.X, at.Y, crop.Width, crop.Height } : null, items });
        }
        if (n == 0) return "no panel of the tab was found on screen";
        WritePng(stem + ".png", pic);
        var json = new
        {
            kind = "ribbon-tab", tabId = tab.Id, tabTitle = tab.Title, width = pic.Width, height = pic.Height,
            dpi = Math.Round(96 * scale.M11), captured = DateTime.UtcNow.ToString("o"), panels,
        };
        // the .json last: a docs build reads a tab only through its .json, so it never sees a tab whose pictures are still being written
        var jsonPath = Path.Combine(_out, stem + ".json");
        File.WriteAllText(jsonPath + ".tmp", JsonSerializer.Serialize(json, Json));
        File.Move(jsonPath + ".tmp", jsonPath, overwrite: true);
        return null;
    }

    /// <summary>Writes a picture through a temp file, so a docs build running meanwhile never reads half a file.</summary>
    void WritePng(string file, ShotService.Picture p)
    {
        var path = Path.Combine(_out, file);
        Png.Write(path + ".tmp", p.Width, p.Height, p.Pixels);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    /// <summary>Every item of a panel, rows and slide-out included, and the items inside split buttons and row panels.</summary>
    static IEnumerable<RibbonItem> Items(RibbonPanel panel)
    {
        IEnumerable<RibbonItem> Deep(IEnumerable<RibbonItem> items)
        {
            foreach (var i in items)
            {
                yield return i;
                if (i is RibbonRowPanel row) foreach (var c in Deep(row.Items)) yield return c;
                else if (i is RibbonListButton list) foreach (var c in Deep(list.Items)) yield return c;
            }
        }
        return panel.Source == null ? [] : Deep(panel.Source.Items);
    }

    static void Walk(DependencyObject root, Dictionary<object, Rect> boxes, Matrix scale)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (d is FrameworkElement fe && fe.IsVisible && fe.ActualWidth > 0 && fe.DataContext is RibbonPanel or RibbonItem && !boxes.ContainsKey(fe.DataContext))
            {
                try
                {
                    var r = fe.TransformToAncestor((Visual)root).TransformBounds(new Rect(0, 0, fe.ActualWidth, fe.ActualHeight));
                    boxes[fe.DataContext] = new Rect(r.X * scale.M11, r.Y * scale.M22, r.Width * scale.M11, r.Height * scale.M22);
                }
                catch (InvalidOperationException) { /* not a descendant any more */ }
            }
            int count = VisualTreeHelper.GetChildrenCount(d);
            for (int i = count - 1; i >= 0; i--) stack.Push(VisualTreeHelper.GetChild(d, i));
        }
    }

    static int[] Ints(Rect r) => new[] { (int)Math.Round(r.X), (int)Math.Round(r.Y), (int)Math.Round(r.Width), (int)Math.Round(r.Height) };

    /// <summary>The part of the picture inside <paramref name="r"/> (clipped to the picture), and where it starts.</summary>
    static (ShotService.Picture? Crop, (int X, int Y) At) Crop(ShotService.Picture p, Rect r)
    {
        int x0 = (int)Math.Round(r.X), y0 = (int)Math.Round(r.Y);
        int x = Math.Max(0, x0), y = Math.Max(0, y0);
        int w = Math.Min(p.Width, x0 + (int)Math.Round(r.Width)) - x, h = Math.Min(p.Height, y0 + (int)Math.Round(r.Height)) - y;
        if (w <= 0 || h <= 0) return (null, (x, y));
        var px = new byte[w * h * 4];
        for (int row = 0; row < h; row++) Buffer.BlockCopy(p.Pixels, ((y + row) * p.Width + x) * 4, px, row * w * 4, w * 4);
        return (new ShotService.Picture(px, w, h), (x, y));
    }

    static string Slug(string s)
    {
        var chars = s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? char.ToLowerInvariant(c) : '_').ToArray();
        var t = new string(chars).Trim('_');
        return t.Length == 0 ? "tab" : t.Length > 60 ? t[..60] : t;
    }
}
