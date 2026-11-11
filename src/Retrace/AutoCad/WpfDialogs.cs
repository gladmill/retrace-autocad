using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Retrace.AutoCad;

/// <summary>
/// A modal WPF window of AutoCAD's process (a dialog without child windows: one HwndWrapper), in process from its visual tree: its fields (<see cref="WpfPalette"/>), its enabled push buttons with the text they
/// show (<see cref="WpfPalette.ButtonText"/>, access key underscore removed), and pressing one the way WPF does (automation peer: Click handlers and Command run), later and from the dispatcher so the press never
/// runs inside a replay tick. UI Automation would deadlock here: these windows belong to the thread that asks. UI thread only. Pure rules are in Core/WpfPresses.
/// </summary>
static class WpfDialogs
{
    /// <summary>Is <paramref name="hwnd"/> a WPF window (its root visual a <see cref="Window"/>; a popup is not)? False for any other window and for a window that is gone.</summary>
    internal static bool IsWindow(IntPtr hwnd)
    {
        try { return hwnd != IntPtr.Zero && HwndSource.FromHwnd(hwnd)?.RootVisual is Window; }
        catch (Exception) { return false; }
    }

    /// <summary>One pass over the window (the same scan a palette gets: <see cref="WpfPalette.ScanHost"/>).</summary>
    internal static WpfPalette.Scan Read(IntPtr hwnd) => WpfPalette.ScanHost(hwnd);

    /// <summary>The text and the index (1 = the first of the enabled buttons with that text) of <paramref name="b"/> in <paramref name="scan"/>; null when it is not in it (a disabled, hidden or non-push button).</summary>
    internal static (string Text, int Index)? Describe(WpfPalette.Scan scan, ButtonBase b)
    {
        int at = scan.Buttons.FindIndex(x => ReferenceEquals(x.Button, b));
        return at < 0 ? null : (scan.Buttons[at].Text, WpfPalette.IndexOfButton(scan.Buttons, at));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetCursorPos(out CursorPoint p);
    /// <summary>The mouse pointer's screen position (where a mouse click on a WPF button just was); null when it cannot be read.</summary>
    internal static (int X, int Y)? CursorPos() => GetCursorPos(out var p) ? (p.X, p.Y) : null;
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct CursorPoint { public int X, Y; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

    /// <summary>The push button under the system cursor in the window <paramref name="hwnd"/> (a hit test, no scan; the cursor, because WPF's device position lags behind a pointer that a script moved), null when the pointer is
    /// elsewhere or not on a real push button. The hit may be a Run over the text, whose parent is a logical one.</summary>
    internal static ButtonBase? ButtonUnderCursor(IntPtr hwnd)
    {
        try
        {
            if (HwndSource.FromHwnd(hwnd) is not { RootVisual: UIElement root } || !GetCursorPos(out var p)) return null;
            for (var d = root.InputHitTest(root.PointFromScreen(new Point(p.X, p.Y))) as DependencyObject; d != null;
                 d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                if (d is ButtonBase b) return WpfPalette.IsPushButton(b) && b.IsEnabled ? b : null;
        }
        catch (Exception ex) { Log.Error("dialog click (wpf)", ex); }
        return null;
    }

    internal static bool IsDefault(ButtonBase b) => b is Button { IsDefault: true };
    internal static bool IsCancel(ButtonBase b) => b is Button { IsCancel: true };

    /// <summary>Closes the window the way its close box does (WM_CLOSE, posted): what a Cancel does in a dialog that has no button for it.</summary>
    internal static bool PostClose(IntPtr hwnd) => PostMessage(hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero);

    /// <summary>Is <paramref name="element"/> in the window <paramref name="hwnd"/>?</summary>
    internal static bool IsIn(DependencyObject element, IntPtr hwnd)
    {
        try { return PresentationSource.FromDependencyObject(element) is HwndSource src && src.Handle == hwnd; }
        catch (Exception) { return false; }
    }

    /// <summary>Is the mouse over (or capturing) the window <paramref name="hwnd"/>? Is the keyboard input going to it?</summary>
    internal static bool IsMouseIn(IntPtr hwnd) => HasActive(System.Windows.Input.Mouse.PrimaryDevice.ActiveSource, hwnd);
    internal static bool IsKeyboardIn(IntPtr hwnd) => HasActive(System.Windows.Input.Keyboard.PrimaryDevice.ActiveSource, hwnd);
    static bool HasActive(PresentationSource? active, IntPtr hwnd) => active is HwndSource src && src.Handle == hwnd;

    // ---- pressing (replay) ---------------------------------------------------------------------------------------------------

    /// <summary>Queues the press of <paramref name="b"/> on the dispatcher (background priority): the replay tick that asked has returned by then, and a button that opens a modal window runs that window's
    /// loop from here, not from inside the tick. <paramref name="stillWanted"/> is asked when the press is due: a filler that has given up or been stopped by then says no and nothing is pressed.
    /// <paramref name="failed"/> gets the reason when it could not be pressed (the button went away or was disabled): the caller keeps it, per filler.</summary>
    internal static void PressLater(ButtonBase b, string label, Func<bool> stillWanted, Action<string> failed) =>
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!stillWanted()) return;
            if (!WpfPalette.Press(b)) failed($"could not press {label} (the button went away or was disabled)");
        }));

    /// <summary>The screen rectangle of an element, for a Tdoc picture; null when it is not on screen.</summary>
    internal static ScreenRect? RectOf(FrameworkElement e)
    {
        if (WpfPalette.ScreenRect(e) is not { } r) return null;
        return new ScreenRect((int)r.Left, (int)r.Top, (int)r.Right, (int)r.Bottom);
    }
}
