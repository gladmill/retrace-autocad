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
    readonly List<string> _done = new(), _failed = new();
    DateTime _shownAt;
    RibbonTab? _job;

    RibbonCapture(RibbonControl ribbon, IEnumerable<RibbonTab> tabs, string outDir, Editor ed)
    {
        _ribbon = ribbon; _queue = new Queue<RibbonTab>(tabs); _out = outDir; _ed = ed; _restore = ribbon.ActiveTab;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += (_, _) => Log.Safe("ribbon capture", Tick);
    }

    public static void Run()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument ?? throw new InvalidOperationException("open a drawing first");
        var ed = doc.Editor;
        if (AppDomain.CurrentDomain.GetData(RunningKey) != null) { ed.WriteMessage("\nRETRACECAPTURERIBBON: a capture is already running.\n"); return; }
        var ribbon = ComponentManager.Ribbon;
        if (ribbon == null) { ed.WriteMessage("\nRETRACECAPTURERIBBON: there is no ribbon (RIBBON shows it).\n"); return; }
        var tabs = ribbon.Tabs.Where(t => t.IsVisible && !t.IsContextualTab && !OwnCommands.IsOwnTab(t.Id)).ToList();
        if (tabs.Count == 0) { ed.WriteMessage("\nRETRACECAPTURERIBBON: the ribbon has no tabs to capture.\n"); return; }

        var o = new PromptKeywordOptions("\nCapture which ribbon tabs [All/Current] <All>: ", "All Current") { AllowNone = true };
        var k = Ask.Keywords(ed, o);
        if (k.Status == PromptStatus.Cancel) return;
        if (k.Status == PromptStatus.OK && k.StringResult == "Current")
            tabs = ribbon.ActiveTab is { } active && tabs.Contains(active) ? new() { active } : new();
        if (tabs.Count == 0) { ed.WriteMessage("\nRETRACECAPTURERIBBON: the active tab cannot be captured (contextual, or Retrace's own).\n"); return; }
        var def = RetraceSettings.Load().Captures;
        var p = Ask.String(ed, $"\nOutput folder <{def}>: ", Ask.Of.Folder, "folder:captures", initial: def);
        if (p.Status == PromptStatus.Cancel) return;
        var outDir = string.IsNullOrWhiteSpace(p.StringResult) ? def : p.StringResult.Trim().Trim('"');
        Directory.CreateDirectory(outDir);

        AppDomain.CurrentDomain.SetData(RunningKey, true);
        ed.WriteMessage($"\nRETRACECAPTURERIBBON: {tabs.Count} tab(s) to {outDir}. Leave the AutoCAD window as it is until it is done.\n");
        new RibbonCapture(ribbon, tabs, outDir, ed)._timer.Start();
    }

    void Tick()
    {
        if (_job == null)
        {
            if (_queue.Count == 0) { Finish(); return; }
            _job = _queue.Dequeue();
            _ribbon.ActiveTab = _job;
            _shownAt = DateTime.Now;
            return;
        }
        if ((DateTime.Now - _shownAt).TotalMilliseconds < SettleMs) return;
        var tab = _job;
        _job = null;
        try
        {
            var problem = Capture(tab);
            if (problem == null) _done.Add(tab.Title ?? tab.Id ?? "?");
            else { _failed.Add($"{tab.Title}: {problem}"); Log.Info($"ribbon capture {tab.Title}: {problem}"); }
        }
        catch (Exception ex) { Log.Error("ribbon capture " + tab.Title, ex); _failed.Add($"{tab.Title}: {ex.Message}"); }
    }

    void Finish()
    {
        _timer.Stop();
        AppDomain.CurrentDomain.SetData(RunningKey, null);
        try { if (_restore != null) _ribbon.ActiveTab = _restore; } catch (Exception ex) { Log.Error("ribbon capture restore", ex); }
        var msg = $"\nRETRACECAPTURERIBBON: {_done.Count} tab(s) captured to {_out}" + (_failed.Count > 0 ? $"; not captured: {string.Join("; ", _failed)}" : "") + "\n";
        try { (AcApp.DocumentManager.MdiActiveDocument?.Editor ?? _ed).WriteMessage(msg); } catch (Exception ex) { Log.Info(msg.Trim() + $" ({ex.Message})"); }
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
