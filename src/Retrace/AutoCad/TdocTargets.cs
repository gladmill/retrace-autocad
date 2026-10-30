using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Retrace.AutoCad;

/// <summary>A rectangle on the screen in device pixels; Right and Bottom are exclusive, like a RECT.</summary>
readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public ScreenRect Moved(int dx, int dy) => new(Left + dx, Top + dy, Right + dx, Bottom + dy);
}

/// <summary>A control that gets a numbered box in the Tdoc picture of a dialog or palette. <see cref="Handle"/> (a Win32 control) is read again when the
/// picture is taken, because the window may have been moved; without one (UI Automation) <see cref="Rect"/> is where it was.</summary>
sealed record DialogBox(IntPtr Handle, ScreenRect Rect, string Text);

/// <summary>What a dialog shows when it is filled in and about to be closed: the controls that were filled and the button that closes it.</summary>
sealed record DialogShot(IntPtr Dialog, List<DialogBox> Filled, DialogBox? Close);

/// <summary>Where things are on the screen, for the boxes of a Tdoc picture. Every failure is logged and means "no box".</summary>
static class TdocTargets
{
    delegate bool EnumProc(IntPtr h, IntPtr lp);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    /// <summary>The screen rectangle of a ribbon item, read from the ribbon's own WPF tree (UI thread only; milliseconds, where UI Automation
    /// took over 6 s on AutoCAD 2026's ribbon). The item's visual is the outermost shown element whose DataContext is the RibbonItem. When the
    /// button does not show because a narrow window collapsed its panel into one button, that panel's rectangle is returned with
    /// <c>Panel</c> true: the reader opens the panel first. Null when neither shows (another tab, a slide-out) or on any failure.</summary>
    public static (ScreenRect Rect, bool Panel)? RibbonItemRect(string itemId)
    {
        try
        {
            var ribbon = Autodesk.Windows.ComponentManager.Ribbon;
            if (ribbon == null || string.IsNullOrEmpty(itemId)) return null;
            var item = ribbon.FindItem(itemId, false, true);
            if (item == null) return null;
            if (ShownRect(ribbon, item) is { } r) return (r, false);
            foreach (var tab in ribbon.Tabs)
            {
                if (!tab.IsActive) continue;
                foreach (var panel in tab.Panels)
                    if (Holds(panel.Source?.Items, item, 0) && ShownRect(ribbon, panel) is { } pr) return (pr, true);
            }
        }
        catch (Exception ex) { Log.Error("tdoc ribbon item rect", ex); }
        return null;
    }

    /// <summary>Whether a ribbon item collection holds <paramref name="target"/>, also inside rows, split buttons and other item lists.</summary>
    static bool Holds(System.Collections.IEnumerable? items, object target, int depth)
    {
        if (items == null || depth > 6) return false;
        foreach (var it in items)
        {
            if (ReferenceEquals(it, target)) return true;
            if (it?.GetType().GetProperty("Items")?.GetValue(it) is System.Collections.IEnumerable sub && Holds(sub, target, depth + 1)) return true;
        }
        return false;
    }

    /// <summary>The screen rectangle (device pixels) of the outermost visible element under <paramref name="root"/> that shows <paramref name="data"/>.</summary>
    static ScreenRect? ShownRect(System.Windows.FrameworkElement root, object data)
    {
        var queue = new Queue<System.Windows.DependencyObject>(); queue.Enqueue(root);
        for (int seen = 0; queue.Count > 0 && seen < 50000; seen++)
        {
            var v = queue.Dequeue();
            if (v is System.Windows.FrameworkElement fe && ReferenceEquals(fe.DataContext, data) && fe.IsVisible && fe.ActualWidth >= 1 && fe.ActualHeight >= 1
                && System.Windows.PresentationSource.FromVisual(fe) != null)
            {
                var tl = fe.PointToScreen(new System.Windows.Point(0, 0));
                var br = fe.PointToScreen(new System.Windows.Point(fe.ActualWidth, fe.ActualHeight));
                return new ScreenRect((int)Math.Floor(tl.X), (int)Math.Floor(tl.Y), (int)Math.Ceiling(br.X), (int)Math.Ceiling(br.Y));
            }
            if (v is System.Windows.Media.Visual || v is System.Windows.Media.Media3D.Visual3D)
                for (int i = 0, n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(v); i < n; i++) queue.Enqueue(System.Windows.Media.VisualTreeHelper.GetChild(v, i));
        }
        return null;
    }

    /// <summary>The UI Automation rectangle of an element as a screen rectangle; null when it has none.</summary>
    public static ScreenRect? Of(AutomationElement el)
    {
        try
        {
            var r = el.Current.BoundingRectangle;
            return r.IsEmpty || r.Width < 1 || r.Height < 1 ? null : new ScreenRect((int)Math.Floor(r.Left), (int)Math.Floor(r.Top), (int)Math.Ceiling(r.Right), (int)Math.Ceiling(r.Bottom));
        }
        catch (Exception) { return null; }
    }

    /// <summary>A window's rectangle on the screen; null when it is not visible. UI thread.</summary>
    public static ScreenRect? Of(IntPtr h)
    {
        if (h == IntPtr.Zero || !IsWindowVisible(h) || !GetWindowRect(h, out var r) || r.Right <= r.Left || r.Bottom <= r.Top) return null;
        return new ScreenRect(r.Left, r.Top, r.Right, r.Bottom);
    }

    /// <summary>The rectangle of a box: the control's window now when it has one, else where it was seen, moved by (<paramref name="dx"/>, <paramref name="dy"/>).</summary>
    public static ScreenRect? Of(DialogBox b, int dx = 0, int dy = 0) => b.Handle != IntPtr.Zero ? Of(b.Handle) : b.Rect.Moved(dx, dy);

    /// <summary>A WPF window of this process whose content is AutoCAD's command line (AutoCAD 2026: an untitled HwndWrapper owned by the main window,
    /// holding a CommandLineControl). UI thread only: it reads the WPF visual tree.</summary>
    static bool HostsCommandLine(IntPtr h)
    {
        try
        {
            if (!Native.ClassOf(h).StartsWith("HwndWrapper", StringComparison.Ordinal)) return false;
            if (System.Windows.Interop.HwndSource.FromHwnd(h)?.RootVisual is not System.Windows.DependencyObject root) return false;
            var queue = new Queue<(System.Windows.DependencyObject, int)>(); queue.Enqueue((root, 0));
            while (queue.Count > 0)
            {
                var (v, depth) = queue.Dequeue();
                if (v.GetType().Name.Contains("CommandLine", StringComparison.Ordinal)) return true;
                if (depth >= 6 || v is not System.Windows.Media.Visual) continue;
                for (int i = 0, n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(v); i < n; i++) queue.Enqueue((System.Windows.Media.VisualTreeHelper.GetChild(v, i), depth + 1));
            }
        }
        catch (Exception ex) { Log.Error("tdoc command line host", ex); }
        return false;
    }

    /// <summary>AutoCAD's command line: the biggest visible window of this process, docked or floating, that holds AutoCAD's command line
    /// control (<see cref="HostsCommandLine"/>) or is titled "Command Line" (or has "CmdLine" in its class). Null when there is none. UI thread.</summary>
    public static ScreenRect? CommandLineRect(IntPtr main)
    {
        ScreenRect? best = null;
        long bestArea = 0;
        bool Consider(IntPtr h)
        {
            if (!IsWindowVisible(h)) return true;
            var title = Native.TitleOf(h);
            if (!title.Equals("Command Line", StringComparison.OrdinalIgnoreCase) && Native.ClassOf(h).IndexOf("CmdLine", StringComparison.OrdinalIgnoreCase) < 0
                && !HostsCommandLine(h)) return true;
            if (Of(h) is { } r && (long)(r.Right - r.Left) * (r.Bottom - r.Top) is var area && area > bestArea) { best = r; bestArea = area; }
            return true;
        }
        try
        {
            uint me = (uint)Environment.ProcessId;
            EnumChildWindows(main, (h, _) => Consider(h), IntPtr.Zero);
            EnumWindows((top, _) =>
            {
                GetWindowThreadProcessId(top, out var pid);
                if (pid != me) return true;
                Consider(top);
                EnumChildWindows(top, (h, _) => Consider(h), IntPtr.Zero);
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) { Log.Error("tdoc command line rect", ex); }
        return best;
    }
}
