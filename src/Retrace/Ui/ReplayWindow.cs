using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

using Retrace.AutoCad;

namespace Retrace.Ui;

/// <summary>Small modeless always-on-top panel that shows the current/next step. Built in code (no XAML) so the project stays one dll.</summary>
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
        Left = 40; Top = 120;
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

    public void Show(string head, string body, bool nextEnabled)
    {
        _head.Text = head;
        _body.Text = body;
        _next.IsEnabled = nextEnabled;
    }
}
