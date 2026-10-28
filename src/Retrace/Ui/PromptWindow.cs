using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Retrace.Ui;

/// <summary>
/// The window Retrace asks in when the setting promptStyle is "ui" (<see cref="Retrace.AutoCad.Ask"/>): a code-only WPF window that stays on
/// top of AutoCAD. Text: the prompt and a text box. File / folder: the same, plus Browse... (the Windows file or folder dialog, which
/// has its own Quick access) and Retrace's recent places for this kind of prompt (click fills the box, double-click takes it).
/// Choices (keywords): one button per choice, the default one first and focused. Enter = OK, Esc = Cancel. It never touches AutoCAD.
/// </summary>
sealed class PromptWindow : Window
{
    public enum Kind { Text, File, Folder, Choice }

    readonly TextBox _box = new() { Padding = new Thickness(4), MinWidth = 420 };
    readonly ListBox _recent = new() { MaxHeight = 190, Margin = new Thickness(0, 4, 0, 0) };
    readonly Kind _kind;
    readonly string? _filter, _browseFrom;

    /// <summary>The text typed or the choice clicked; null = cancelled.</summary>
    public string? Result { get; private set; }

    /// <param name="message">the prompt, as the command line would show it (its "&lt;default&gt;" and hints included)</param>
    /// <param name="choices">Choice: the keywords; the default first</param>
    /// <param name="recent">File / Folder: recent places, newest first</param>
    /// <param name="filter">File: the file dialog's filter, e.g. "Retrace recordings (*.jsonl)|*.jsonl"</param>
    public PromptWindow(string title, string message, Kind kind, string initial = "", IReadOnlyList<string>? choices = null,
        IReadOnlyList<string>? recent = null, string? filter = null, string? browseFrom = null)
    {
        _kind = kind; _filter = filter; _browseFrom = browseFrom;
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        MaxWidth = 900;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Topmost = true;

        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(new TextBlock { Text = message.Trim(), TextWrapping = TextWrapping.Wrap, MaxWidth = 640, Margin = new Thickness(0, 0, 0, 8) });

        if (kind == Kind.Choice)
        {
            var row = new WrapPanel();
            Button? first = null;
            foreach (var c in choices ?? [])
            {
                var b = new Button { Content = c, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0), MinWidth = 70 };
                b.Click += (_, _) => Done(c);
                first ??= b;
                row.Children.Add(b);
            }
            if (first != null) { first.IsDefault = true; Loaded += (_, _) => first.Focus(); }
            var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 4, 14, 4), IsCancel = true };
            cancel.Click += (_, _) => Done(null);
            row.Children.Add(cancel);
            root.Children.Add(row);
            Content = root;
            return;
        }

        _box.Text = initial;
        var line = new DockPanel();
        if (kind is Kind.File or Kind.Folder)
        {
            var browse = new Button { Content = "Browse...", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
            browse.Click += (_, _) => Browse();
            DockPanel.SetDock(browse, Dock.Right);
            line.Children.Add(browse);
        }
        line.Children.Add(_box);
        root.Children.Add(line);

        if (kind is Kind.File or Kind.Folder && recent is { Count: > 0 })
        {
            root.Children.Add(new TextBlock { Text = "Recent places", Foreground = Brushes.DimGray, Margin = new Thickness(0, 10, 0, 0) });
            foreach (var r in recent) _recent.Items.Add(r);
            _recent.SelectionChanged += (_, _) => { if (_recent.SelectedItem is string s) _box.Text = s; };
            _recent.MouseDoubleClick += (_, _) => { if (_recent.SelectedItem is string s) Done(s); };
            root.Children.Add(_recent);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = "OK", Padding = new Thickness(18, 3, 18, 3), IsDefault = true };
        ok.Click += (_, _) => Done(_box.Text);
        var no = new Button { Content = "Cancel", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        no.Click += (_, _) => Done(null);
        buttons.Children.Add(ok); buttons.Children.Add(no);
        root.Children.Add(buttons);
        Content = root;
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    void Done(string? result) { Result = result; DialogResult = result != null; }

    /// <summary>The Windows file or folder dialog, starting where the box points (or its folder), else at the newest recent place.</summary>
    void Browse()
    {
        var start = _box.Text.Trim().Trim('"');
        if (start.Length == 0 && _recent.Items.Count > 0) start = (string)_recent.Items[0];
        if (start.Length == 0 && _browseFrom != null) start = _browseFrom;
        string? dir = Directory.Exists(start) ? start : File.Exists(start) ? Path.GetDirectoryName(start) : null;
        Topmost = false;   // the Windows dialog must come above this window
        try
        {
            if (_kind == Kind.Folder)
            {
                var d = new Microsoft.Win32.OpenFolderDialog { Title = Title };
                if (dir != null) d.InitialDirectory = dir;
                if (d.ShowDialog(this) == true) _box.Text = d.FolderName;
            }
            else
            {
                var d = new Microsoft.Win32.OpenFileDialog { Title = Title, Filter = _filter ?? "All files (*.*)|*.*", CheckFileExists = false };
                if (dir != null) d.InitialDirectory = dir;
                if (d.ShowDialog(this) == true) _box.Text = d.FileName;
            }
        }
        finally { Topmost = true; }
        _box.Focus();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Enter in the recent list takes the place, as a double-click does
        if (e.Key == Key.Enter && _recent.IsKeyboardFocusWithin && _recent.SelectedItem is string s) { e.Handled = true; Done(s); return; }
        base.OnPreviewKeyDown(e);
    }
}
