using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using Retrace.AutoCad;

namespace Retrace.Ui;

/// <summary>
/// RETRACETDOC: a modeless, code-only WPF window (like <see cref="OpenTestWindow"/>, so the project stays one dll). The tests of the Tests folder, newest
/// first, each with a check box; Up / Down put the selected one where it should run. A title, Start and Settings. The tests run in the order of the list,
/// the ticked ones only. The folder is read on a thread-pool thread; the window never touches the AutoCAD API itself: Start goes through <see cref="Commands"/>.
/// </summary>
sealed class TdocWindow : Window
{
    readonly Commands _core;
    readonly TextBox _title = new() { Padding = new Thickness(4), Text = "Training", ToolTip = "The title of the Word document. It is also part of its file name." };
    readonly ListBox _list = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 8, 0, 0) };
    readonly Button _start = new() { Content = "Start", Height = 38, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 6), ToolTip = "Replay the ticked tests one after another and write the training document." };
    int _loadId;

    public TdocWindow(Commands core)
    {
        _core = core;
        Title = "Retrace: training document";
        Width = 820; Height = 560; MinWidth = 640; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;

        var root = new Grid { Margin = new Thickness(12) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var left = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        var heading = new TextBlock { Text = "Tick the tests to include. They run from the top down.", Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(heading, Dock.Top);
        left.Children.Add(heading);
        left.Children.Add(_list);
        Grid.SetColumn(left, 0); root.Children.Add(left);

        var right = new StackPanel();
        right.Children.Add(new TextBlock { Text = "Title of the document", Margin = new Thickness(0, 0, 0, 4) });
        right.Children.Add(_title);
        right.Children.Add(Side("Move up", "Run the selected test earlier.", () => Move(-1)));
        right.Children.Add(Side("Move down", "Run the selected test later.", () => Move(1)));
        right.Children.Add(Side("Tick all", "Include every test in the list.", () => TickAll(true)));
        right.Children.Add(Side("Untick all", "Include none.", () => TickAll(false)));
        right.Children.Add(Side("Settings", "Open the Retrace settings (the Tdoc group: front, boxes, pause).", () => Command("RETRACESETTINGS")));
        _start.Click += (_, _) => Run(Start);
        right.Children.Add(_start);
        Grid.SetColumn(right, 1); root.Children.Add(right);

        var bottom = new DockPanel { LastChildFill = true };
        var refresh = new Button { Content = "Refresh list", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(8, 8, 0, 0) };
        refresh.Click += (_, _) => Reload();
        var close = new Button { Content = "Close", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(8, 8, 0, 0), IsCancel = true };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right); DockPanel.SetDock(refresh, Dock.Right);
        bottom.Children.Add(close); bottom.Children.Add(refresh); bottom.Children.Add(_status);
        Grid.SetRow(bottom, 1); Grid.SetColumnSpan(bottom, 2); root.Children.Add(bottom);
        Content = root;

        Loaded += (_, _) => Reload();
    }

    Button Side(string text, string tip, Action click)
    {
        var b = new Button { Content = text, Height = 32, Margin = new Thickness(0, 6, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 0, 12, 0), ToolTip = tip };
        b.Click += (_, _) => Run(click);
        return b;
    }

    void Run(Action a)
    {
        try { a(); }
        catch (Exception ex) { Log.Error("Tdoc window", ex); Say("Failed: " + ex.Message); _core.PrintLine("RETRACETDOC: " + ex.Message); }
    }

    public void Say(string text)
    {
        if (Dispatcher.CheckAccess()) _status.Text = text; else Dispatcher.BeginInvoke(() => _status.Text = text);
    }

    IEnumerable<CheckBox> Rows => _list.Items.OfType<CheckBox>();

    /// <summary>Reads the Tests folder on a thread-pool thread, then fills the list (ticks and order are kept for the tests still there).</summary>
    public void Reload()
    {
        var id = ++_loadId;
        var root = RetraceSettings.Load().Tests;
        var ticked = Rows.Where(r => r.IsChecked == true).Select(r => ((TestEntry)r.Tag).Folder).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // the order shown now (Move up / down): tests still there keep it, new ones come after them in the Tests folder's order
        var order = Rows.Select((r, i) => (((TestEntry)r.Tag).Folder, i)).GroupBy(x => x.Folder, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().i, StringComparer.OrdinalIgnoreCase);
        Say("Loading the tests in " + root + " ...");
        Task.Run(() => TestList.Read(root)).ContinueWith(t =>
        {
            if (id != _loadId) return;
            var all = t.IsCompletedSuccessfully ? t.Result : new();
            all = all.Select((e, i) => (e, i)).OrderBy(x => order.TryGetValue(x.e.Folder, out var k) ? k : order.Count + x.i).Select(x => x.e).ToList();
            _list.Items.Clear();
            foreach (var e in all)
            {
                var row = new CheckBox { Content = e.Label, Tag = e, IsChecked = ticked.Contains(e.Folder), Margin = new Thickness(0, 2, 0, 2), Padding = new Thickness(4, 0, 0, 0) };
                row.GotFocus += (_, _) => _list.SelectedItem = row;   // a click on the check box selects the row too (Move up / down)
                _list.Items.Add(row);
            }
            Say(all.Count == 0 ? $"No tests in {root} yet. Record one with Record / Stop." : $"{all.Count} test(s) in {root}.");
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void Move(int by)
    {
        var i = _list.SelectedIndex;
        var j = i + by;
        if (i < 0 || j < 0 || j >= _list.Items.Count) return;
        var item = _list.Items[i];
        _list.Items.RemoveAt(i);
        _list.Items.Insert(j, item);
        _list.SelectedIndex = j;
    }

    void TickAll(bool on) { foreach (var r in Rows) r.IsChecked = on; }

    void Command(string name)
    {
        var doc = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
        if (doc == null) { Say("Open a drawing first: the command is typed into the active drawing."); return; }
        doc.SendStringToExecute("_" + name + " ", true, false, false);
    }

    void Start()
    {
        var picked = Rows.Where(r => r.IsChecked == true).Select(r => ((TestEntry)r.Tag).Recording).ToList();
        if (picked.Count == 0) { Say("Tick at least one test."); return; }
        if (_core.TdocStart(_title.Text, picked, Say)) Close();
    }
}
