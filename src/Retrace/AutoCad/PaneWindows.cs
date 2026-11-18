using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Retrace.Core;

namespace Retrace.AutoCad;

/// <summary>Panes an add-in shows without a palette set: a modeless WinForms `Form` (Application.ShowModelessDialog) or WPF `Window` (ShowModelessWindow). The click is followed like a click in a palette (same reading of fields
/// and buttons, same steps); the window plays the palette's page (its title is the palette name, the set is empty). Only a form or window that is an add-in's own is taken: its own type is an add-in's, or it is a plain `Form` / `Window` with content of
/// an add-in's types. Never AutoCAD's own windows, a ribbon pop-up, a tool tip, Retrace's windows (by Retrace's own assembly), or any window while a modal dialog is open (that is <see cref="DialogWatch"/>'s). An assembly is an add-in's by where it
/// was loaded from (<see cref="IsAddInType"/>). UI thread only.</summary>
static class PaneWindows
{
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    delegate bool EnumProc(IntPtr h, IntPtr lp);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    static readonly Dictionary<Assembly, bool> Cache = new();
    static string? _programFolder;

    static string ProgramFolder()
    {
        if (_programFolder != null) return _programFolder;
        try { _programFolder = Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName) ?? ""; } catch (Exception) { _programFolder = ""; }
        return _programFolder;
    }

    static readonly HashSet<Assembly> Logged = new();

    /// <summary>Is this type an add-in's own (<see cref="PaneClicks.IsAddIn"/>: by the assembly's location and, under AutoCAD's folders or the GAC, by the file's company; no list of names)? Cached per assembly; one retrace.log line per assembly
    /// that is not taken for an add-in's because of where it lives (folder kind or the path below the AutoCAD folder, never a user path).</summary>
    public static bool IsAddInType(Type? t)
    {
        if (t == null) return false;
        var a = t.Assembly;
        if (Cache.TryGetValue(a, out var known)) return known;
        bool r;
        try
        {
            string loc = "";
            try { loc = a.IsDynamic ? "" : a.Location; } catch (Exception) { }
            string? company = null;
            if (loc.Length > 0) { try { company = System.Diagnostics.FileVersionInfo.GetVersionInfo(loc).CompanyName; } catch (Exception) { } }
            var d = PaneClicks.AddInDecision(a.GetName().Name, loc, a.IsDynamic, ProgramFolder(), company);
            r = d.AddIn;
            if (!r && d.Why.Length > 0 && !d.Why.StartsWith("a framework", StringComparison.Ordinal) && !d.Why.StartsWith("a dynamic", StringComparison.Ordinal) && Logged.Add(a)) Log.Info($"pane: assembly {a.GetName().Name} at {d.Why} treated as AutoCAD's or the framework's, not an add-in's");
        }
        catch (Exception) { r = false; }
        Cache[a] = r;
        return r;
    }

    static bool IsPlain(Type t) => t.FullName is "System.Windows.Forms.Form" or "System.Windows.Window";

    /// <summary>Does the WinForms control <paramref name="c"/> hold a child control of an add-in's own type (a bounded walk)?</summary>
    public static bool WinFormsContentIsAddIn(object c)
    {
        int seen = 0;
        bool Walk(object o)
        {
            if (seen++ > 400) return false;
            if (o.GetType().Name == "ElementHost" && o.GetType().GetProperty("Child")?.GetValue(o) is DependencyObject hosted && (IsAddInType(hosted.GetType()) || WpfContentIsAddIn(hosted))) return true;
            if (o.GetType().GetProperty("Controls")?.GetValue(o) is not System.Collections.IEnumerable kids) return false;
            foreach (var k in kids) { if (k == null) continue; if (IsAddInType(k.GetType()) || Walk(k)) return true; }
            return false;
        }
        return Walk(c);
    }

    static bool WpfContentIsAddIn(DependencyObject root)
    {
        int seen = 0;
        bool Walk(DependencyObject d)
        {
            if (seen++ > 600) return false;
            int n = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(d) : 0;
            for (int i = 0; i < n; i++)
            {
                var k = VisualTreeHelper.GetChild(d, i);
                if (k is FrameworkElement && IsAddInType(k.GetType())) return true;
                if (k.GetType().Name == "WindowsFormsHost" && k.GetType().GetProperty("Child")?.GetValue(k) is { } hostedForm && (IsAddInType(hostedForm.GetType()) || WinFormsContentIsAddIn(hostedForm))) return true;
                if (Walk(k)) return true;
            }
            return false;
        }
        return Walk(root);
    }

    /// <summary>The kind of the add-in window <paramref name="top"/> (the outermost managed window of a click's chain): "form", "window", or null when it is no window of an add-in. Never throws.</summary>
    public static string? KindOf(IntPtr top)
    {
        try
        {
            if (top == IntPtr.Zero || !IsWindowVisible(top)) return null;
            if (WinFormsControls.Control(top) is { } c)
            {
                var t = c.GetType();
                if (!WinFormsControls.IsA(t, "System.Windows.Forms.Form")) return null;
                return PaneClicks.IsPaneWindow(IsAddInType(t), IsPlain(t), IsPlain(t) && WinFormsContentIsAddIn(c)) ? "form" : null;
            }
            if (HwndSource.FromHwnd(top) is { RootVisual: System.Windows.Window w })
            {
                var t = w.GetType();
                return PaneClicks.IsPaneWindow(IsAddInType(t), IsPlain(t), IsPlain(t) && WpfContentIsAddIn(w)) ? "window" : null;
            }
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>Is the managed content hosted in the palette page <paramref name="h"/> an add-in's: a WinForms control, or WPF content, whose root type is an add-in's. The decision the loose palette frame rule makes.</summary>
    public static bool HostedContentIsAddIn(IntPtr h)
    {
        try
        {
            // the root type alone is not enough: a framework UserControl or ElementHost root may hold the add-in's controls
            if (WinFormsControls.Control(h) is { } c) return IsAddInType(c.GetType()) || WinFormsContentIsAddIn(c);
            if (HwndSource.FromHwnd(h) is { RootVisual: { } r } && !IsPopupRoot(r)) return IsAddInType(r.GetType()) || (r is DependencyObject d && WpfContentIsAddIn(d));
        }
        catch (Exception) { }
        return false;
    }

    static bool IsPopupRoot(object r) => r.GetType().Name == "PopupRoot";

    /// <summary>A mouse-up in a WPF menu pop-up of AutoCAD (type name `MenuWindow`): (is a menu pop-up, is the application menu: an element named *ApplicationMenu* above the point). Never throws.</summary>
    public static (bool Menu, bool AppMenu) MenuPopupAt(IntPtr hwnd, IntPtr lParam)
    {
        try
        {
            if (HwndSource.FromHwnd(hwnd) is not { RootVisual: UIElement root } src || !root.GetType().Name.Contains("MenuWindow", StringComparison.Ordinal)) return (false, false);
            long lp = lParam.ToInt64();
            var dev = new System.Windows.Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
            var pt = src.CompositionTarget?.TransformFromDevice.Transform(dev) ?? dev;
            int guard = 0;
            bool menuControl = false;
            for (var d = root.InputHitTest(pt) as DependencyObject; d != null && guard++ < 120 && !menuControl; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                menuControl = d.GetType().Name == "MenuControl";
            // the application menu opens at the top-left corner of AutoCAD's window, below the big A: measured from the main window's own corner and scaled by its DPI, so a moved, smaller or second-monitor window, or 200 % scaling, is the same
            var main = Autodesk.AutoCAD.ApplicationServices.Application.MainWindow.Handle;
            var rc = PaletteReveal.RectOf(hwnd); var mr = PaletteReveal.RectOf(main);
            double scale = 1; try { var dpi = GetDpiForWindow(main); if (dpi > 0) scale = dpi / 96.0; } catch (Exception) { }
            if (menuControl && Retrace.Core.AppMenuGeometry.IsAppMenu(rc.Left - mr.Left, rc.Top - mr.Top, rc.Height, scale)) return (true, true);
            return (true, false);
        }
        catch (Exception) { return (false, false); }
    }

    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);

    /// <summary>The type name of the window's root (form or window), recorded next to the title (`paneType`); null when it has none.</summary>
    public static string? TypeNameOf(IntPtr top)
    {
        try
        {
            if (WinFormsControls.Control(top) is { } c) return c.GetType().FullName;
            if (HwndSource.FromHwnd(top) is { RootVisual: System.Windows.Window w }) return w.GetType().FullName;
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>What the root of the managed window <paramref name="top"/> is, for a log line: the form's or window's type, a pop-up root, or "unknown".</summary>
    public static string DescribeRoot(IntPtr top)
    {
        try
        {
            if (WinFormsControls.Control(top) is { } c) return c.GetType().FullName ?? "WinForms control";
            if (HwndSource.FromHwnd(top) is { RootVisual: { } r }) return r.GetType().FullName ?? "WPF window";
        }
        catch (Exception) { }
        return "unknown";
    }

    /// <summary>The top-level add-in pane window of this process titled <paramref name="title"/> (loosely: <see cref="PaneClicks.SameTitle"/>) that shows, preferring the one of type <paramref name="typeName"/>; zero when none. <paramref name="count"/>: how
    /// many windows matched (more than one: the first in Z-order is taken, and the caller says so).</summary>
    public static IntPtr Find(string? title, string? typeName, out int count)
    {
        count = 0;
        if (string.IsNullOrEmpty(title)) return IntPtr.Zero;
        IntPtr best = IntPtr.Zero;
        int bestRank = int.MaxValue, n = 0;
        uint me = (uint)Environment.ProcessId;
        EnumWindows((top, _) =>
        {
            GetWindowThreadProcessId(top, out var pid);
            if (pid != me) return true;
            var live = Native.TitleOf(top);
            if (!PaneClicks.SameTitle(title, live) || KindOf(top) == null) return true;
            n++;
            int rank = PaneClicks.TitleRank(title, live, !string.IsNullOrEmpty(typeName) && TypeNameOf(top) == typeName);
            if (rank >= 0 && rank < bestRank) { bestRank = rank; best = top; }   // the first in Z-order wins a tie
            return true;
        }, IntPtr.Zero);
        count = n;
        return best;
    }
}
