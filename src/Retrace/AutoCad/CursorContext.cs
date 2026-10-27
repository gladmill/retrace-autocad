using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Retrace.Engine.Recordings;

namespace Retrace.AutoCad;

/// <summary>
/// What is under the mouse when the checkpoint shortcut is pressed, so the checkpoint can be named after it and say what it looked at: in a
/// palette set, the field or button under the cursor (else the nearest one) and the ones around it, with their values; on the ribbon, the item
/// under it. Null anywhere else (the drawing, the command line): the checkpoint is then named after the last action, as before. UI thread only.
/// </summary>
static class CursorContext
{
    /// <summary>How far from the cursor a control still counts as near it (pixels).</summary>
    const double Reach = 160;

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsChild(IntPtr parent, IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr h);

    /// <summary>One control near the cursor: what a person reads (label or button text) and, for a field, its kind and value.</summary>
    sealed record Near(string Label, string? Kind, string? Value, double Distance, bool Disabled = false)
    {
        public string Text => Kind == null ? $"{Label} (button{(Disabled ? ", disabled" : "")})" : Value is { Length: > 0 } v ? $"{Label} ({Kind}: \"{Short(v)}\")" : $"{Label} ({Kind}, empty)";
    }

    static string Short(string s) => s.Length <= 60 ? s : s[..57] + "...";

    /// <summary>Where the cursor is now, or null.</summary>
    public static Checkpoints.CheckpointContext? Now()
    {
        try { return GetCursorPos(out var p) ? At(p) : null; }
        catch (Exception ex) { Log.Error("cursor context", ex); return null; }
    }

    static Checkpoints.CheckpointContext? At(POINT p)
    {
        var h = WindowFromPoint(p);
        if (h == IntPtr.Zero) return null;
        GetWindowThreadProcessId(h, out var pid);
        if (pid != (uint)Environment.ProcessId) return null;
        if (PaletteWatch.Locate(h) is { } spot) return InPalette(spot, h, p);
        return OnRibbon(h, p);
    }

    static Checkpoints.CheckpointContext? InPalette(PaletteWatch.Spot spot, IntPtr h, POINT p)
    {
        var near = new List<Near>();
        var scan = DialogReader.Scan(spot.Page, disabledToo: true);   // a disabled button is part of what the person looked at
        if (scan.Fields.Count > 0 || scan.Buttons.Count > 0)
        {
            for (int i = 0; i < scan.Fields.Count; i++)
                if (scan.Handles[i].Count > 0 && Distance(scan.Handles[i][0], p) is { } d) near.Add(new Near(scan.Fields[i].Label, scan.Fields[i].Kind, scan.Fields[i].Value, d));
            foreach (var (bh, _, text) in scan.Buttons)
                if (Distance(bh, p) is { } d) near.Add(new Near(PaletteFill.ButtonLabel(text), null, null, d, !IsWindowEnabled(bh)));
        }
        else
        {
            var wpf = WpfPalette.ScanPage(spot.Page, disabledToo: true);
            for (int i = 0; i < wpf.Fields.Count; i++)
                if (Distance(WpfPalette.ScreenRect(wpf.Elements[i]), p) is { } d) near.Add(new Near(wpf.Fields[i].Label, wpf.Fields[i].Kind, wpf.Fields[i].Value, d));
            foreach (var (b, text) in wpf.Buttons)
                if (Distance(WpfPalette.ScreenRect(b), p) is { } d) near.Add(new Near(text, null, null, d, !b.IsEnabled));
        }
        var close = near.Where(n => n.Distance <= Reach).OrderBy(n => n.Distance).Take(4).ToList();
        var where = $"the {spot.Palette} palette" + (spot.Set != spot.Palette && spot.Set.Length > 0 ? $" ({spot.Set})" : "");
        if (close.Count == 0) return new Checkpoints.CheckpointContext("control", spot.Palette, null, $"The cursor was in {where}.");
        var first = close[0];
        var desc = $"{(first.Distance == 0 ? "At the cursor" : "Nearest the cursor")}: {first.Text} in {where}."
                   + (close.Count > 1 ? " Near it: " + string.Join(", ", close.Skip(1).Select(n => n.Text)) + "." : "");
        return new Checkpoints.CheckpointContext("control", spot.Palette, first.Label, desc);
    }

    /// <summary>The ribbon item under the cursor (Autodesk.Windows ribbon, WPF): its text, panel and tab.</summary>
    static Checkpoints.CheckpointContext? OnRibbon(IntPtr h, POINT p)
    {
        try
        {
            if (Autodesk.Windows.ComponentManager.Ribbon is not { } ribbon || PresentationSource.FromVisual(ribbon) is not HwndSource src) return null;
            if (src.Handle != h && !IsChild(src.Handle, h)) return null;
            if (src.RootVisual is not UIElement root) return null;
            for (var d = root.InputHitTest(root.PointFromScreen(new Point(p.X, p.Y))) as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
            {
                if (d is not FrameworkElement { DataContext: Autodesk.Windows.RibbonItem item }) continue;
                var text = string.IsNullOrWhiteSpace(item.Text) ? item.Id : item.Text;
                if (string.IsNullOrWhiteSpace(text)) continue;
                var panel = ribbon.Tabs.SelectMany(t => t.Panels.Select(pn => (t, pn))).FirstOrDefault(x => x.pn.Source?.Items.Contains(item) == true);
                var tab = ribbon.ActiveTab?.Title;
                var path = string.Join(" › ", new[] { tab, panel.pn?.Source?.Title, text }.Where(s => !string.IsNullOrWhiteSpace(s)));
                return new Checkpoints.CheckpointContext("control", panel.pn?.Source?.Title ?? tab ?? "ribbon", text, $"The cursor was on {path} on the ribbon.");
            }
        }
        catch (Exception ex) { Log.Error("cursor context (ribbon)", ex); }
        return null;
    }

    static double? Distance(IntPtr h, POINT p)
    {
        if (!GetWindowRect(h, out var r)) return null;
        return Distance(new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), p);
    }

    static double? Distance(Rect? r, POINT p)
    {
        if (r is not { } b || b.IsEmpty) return null;
        double dx = Math.Max(0, Math.Max(b.Left - p.X, p.X - b.Right)), dy = Math.Max(0, Math.Max(b.Top - p.Y, p.Y - b.Bottom));
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
