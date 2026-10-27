using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

using Retrace.AutoCad;

namespace Retrace.Ui;

/// <summary>Small modeless always-on-top panel that shows the current/next step. Built in code (no XAML) so the project stays one dll.
/// It opens on the right, away from where palettes usually float, never takes the keyboard focus, and moves to the other side when the UI
/// being driven (a palette, a dialog) would be under it (<see cref="Avoid"/>).</summary>
sealed class ReplayWindow : Window
{
    readonly TextBlock _head = new() { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _body = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8) };
    readonly Button _next = new() { Content = "Next step", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0) };
    readonly Button _stop = new() { Content = "Stop", Padding = new Thickness(14, 4, 14, 4) };

    public event Action? NextClicked;
    public event Action? StopClicked;

    public ReplayWindow()
    {
        Title = "Retrace replay";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowActivated = false;   // AutoCAD keeps the focus: the steps type into it
        var work = SystemParameters.WorkArea;
        Left = work.Right - Width - 40; Top = work.Top + 160;
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(_head);
        panel.Children.Add(_body);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        _next.Click += (_, _) => NextClicked?.Invoke();
        _stop.Click += (_, _) => StopClicked?.Invoke();
        row.Children.Add(_next);
        row.Children.Add(_stop);
        panel.Children.Add(row);
        Content = panel;
        try { new WindowInteropHelper(this).Owner = AcApp.MainWindow.Handle; } catch { /* unowned is fine */ }
        Closed += (_, _) => StopClicked?.Invoke();
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromRect(ref RECT r, uint flags);
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);

    /// <summary>If this window covers the screen rectangle (device pixels) of the UI being driven, move it to the other side of the screen.</summary>
    public void Avoid(int left, int top, int right, int bottom)
    {
        try
        {
            var h = new WindowInteropHelper(this).Handle;
            if (h == IntPtr.Zero || !GetWindowRect(h, out var me)) return;
            if (me.Right <= left || me.Left >= right || me.Bottom <= top || me.Top >= bottom) return;   // not in the way
            var target = new RECT { Left = left, Top = top, Right = right, Bottom = bottom };
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(MonitorFromRect(ref target, 2 /* nearest */), ref mi)) return;
            int w = me.Right - me.Left, work = mi.rcWork.Right - mi.rcWork.Left;
            // the side of the screen the driven UI is not on
            int x = (left + right) / 2 - mi.rcWork.Left < work / 2 ? mi.rcWork.Right - w - 24 : mi.rcWork.Left + 24;
            SetWindowPos(h, IntPtr.Zero, x, me.Top, 0, 0, 0x0001 | 0x0004 | 0x0010);   // SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE
        }
        catch (Exception ex) { Log.Error("replay window avoid", ex); }
    }

    /// <summary>This window's handle (zero before it is shown).</summary>
    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    public void Show(string head, string body, bool nextEnabled)
    {
        _head.Text = head;
        _body.Text = body;
        _next.IsEnabled = nextEnabled;
    }
}
