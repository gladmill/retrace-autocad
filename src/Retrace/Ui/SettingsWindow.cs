using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using Retrace.AutoCad;

namespace Retrace.Ui;

/// <summary>
/// Code-only WPF settings dialog (no XAML). One row per path: text box showing the
/// effective value, Browse..., Open (Explorer) and a green/red "exists" mark. Save writes settings.json; defaults are stored as "".
/// Shown modally with Application.ShowModalWindow by the caller.
/// </summary>
sealed class SettingsWindow : Window
{
    sealed class Row
    {
        public string Label = "", Default = "";
        public TextBox Box = new();
        public TextBlock Mark = new();
        public bool Optional, IsEditor;
    }

    readonly List<Row> _rows = new();
    readonly CheckBox _shots = new() { Content = "Take screenshots while recording (saved in the test folder, used by steps.md)", Margin = new Thickness(0, 10, 0, 0) };
    readonly CheckBox _autoFill = new() { Content = "RETRACERUN fills dialogs with the recorded values and closes them (else you do it)", Margin = new Thickness(0, 6, 0, 0) };
    readonly CheckBox _dialogValues = new() { Content = "Record which values you change in dialogs and palettes (shown in the test script)", Margin = new Thickness(0, 6, 0, 0) };
    readonly CheckBox _showUi = new() { Content = "Run test opens rolled-up and auto-hidden palettes while it drives them", Margin = new Thickness(0, 6, 0, 0), ToolTip = "Moves the mouse over the palette (and back after) so you can watch the step. Off: the palette is filled while it stays hidden." };
    readonly CheckBox _review = new() { Content = "After Stop, show the checkpoints so you can rename them", Margin = new Thickness(0, 6, 0, 0), ToolTip = "Opens the checkpoints window when a recording with checkpoints stops. RETRACECHECKPOINTS opens it any time." };
    readonly CheckBox _docx = new() { Content = "Also write Word (.docx)", Margin = new Thickness(0, 6, 0, 0), ToolTip = "steps.docx next to steps.md, made when a recording is stopped." };
    readonly CheckBox _xlsx = new() { Content = "Also write Excel (.xlsx)", Margin = new Thickness(0, 6, 0, 0), ToolTip = "steps.xlsx next to steps.md: one row per step with a PASS / FAIL column." };
    readonly ComboBox _tabPos = new() { MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(8, 0, 0, 0) };
    readonly List<string> _tabPosValues = new();
    readonly TextBox _key = new() { Width = 180, IsReadOnly = true, IsReadOnlyCaretVisible = false, Padding = new Thickness(3), Margin = new Thickness(8, 0, 0, 0), ToolTip = "Click here, then press the keys. Backspace or Delete turns the shortcut off." };
    readonly TextBlock _keyMsg = new() { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    string _keyValue = KeyChord.Default;   // "None" = off; always valid
    public RetraceSettings Result { get; private set; }
    public bool Saved { get; private set; }

    public SettingsWindow(RetraceSettings current)
    {
        Result = current.Clone();
        Title = "Retrace settings";
        Width = 820; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;

        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(new TextBlock { Text = "Everything lives under " + current.RootDir + " unless you change a folder here.", Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap });
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.Children.Add(grid);

        AddRow(grid, "Source folder", current.Source, "", folder: true, optional: true);
        AddRow(grid, "Docs folder", current.Docs, current.DefaultDocs, folder: true);
        AddRow(grid, "Tests folder", current.Tests, current.DefaultTests, folder: true);
        AddRow(grid, "Captures folder", current.Captures, current.DefaultCaptures, folder: true);
        AddRow(grid, "Editor", current.Editor, RetraceSettings.DefaultEditor(), folder: false, editor: true);

        var hint = new TextBlock
        {
            Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray,
            Text = "Editor: the program \"Open in editor\" in the guide palette starts (default: VS Code if found, else Notepad). " +
                   "\"Source folder\" has no default: the folder with your code, CUIX and DCL files. Captures are the pictures of DCL dialogs.",
        };
        root.Children.Add(hint);
        _shots.IsChecked = current.Screenshots;
        root.Children.Add(_shots);
        _dialogValues.IsChecked = current.DialogValues;
        root.Children.Add(_dialogValues);
        _autoFill.IsChecked = current.AutoFillDialogs;
        root.Children.Add(_autoFill);
        _review.IsChecked = current.ReviewCheckpoints;
        root.Children.Add(_review);
        _showUi.IsChecked = current.ShowDrivenUi;
        root.Children.Add(_showUi);
        _docx.IsChecked = current.ExportDocx;
        root.Children.Add(_docx);
        _xlsx.IsChecked = current.ExportXlsx;
        root.Children.Add(_xlsx);

        var posRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        posRow.Children.Add(new TextBlock { Text = "Ribbon tab position", VerticalAlignment = VerticalAlignment.Center });
        FillTabPositions(current.RibbonTabPosition);
        posRow.Children.Add(_tabPos);
        root.Children.Add(posRow);

        var keyRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        keyRow.Children.Add(new TextBlock { Text = "Checkpoint shortcut", VerticalAlignment = VerticalAlignment.Center });
        SetKey(KeyChord.IsOff(current.CheckpointKey) ? "None" : current.CheckpointKey);
        _key.PreviewKeyDown += OnKeyCapture;
        keyRow.Children.Add(_key);
        var keyDefault = Btn("Default"); keyDefault.Click += (_, _) => SetKey(KeyChord.Default);
        keyRow.Children.Add(keyDefault);
        keyRow.Children.Add(_keyMsg);
        root.Children.Add(keyRow);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var reset = Btn("Reset to defaults"); reset.Click += (_, _) => ResetAll();
        var save = Btn("Save"); save.IsDefault = true; save.Click += (_, _) => Save();
        var cancel = Btn("Cancel"); cancel.IsCancel = true; cancel.Click += (_, _) => { Saved = false; Close(); };
        buttons.Children.Add(reset); buttons.Children.Add(save); buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;
    }

    /// <summary>Entries: Right of Home (default), First, Last, one "Right of title" per visible ribbon tab; the stored value is kept as an extra entry if none matches.</summary>
    void FillTabPositions(string stored)
    {
        void Add(string label, string value) { _tabPos.Items.Add(label); _tabPosValues.Add(value); }
        Add("Right of Home (default)", RibbonTabPosition.AfterHome);
        Add("First", RibbonTabPosition.First);
        Add("Last", RibbonTabPosition.Last);
        foreach (var (id, title) in RibbonInstaller.AnchorTabs())
            Add("Right of " + title, RibbonTabPosition.AfterPrefix + (id.Length > 0 ? id : title));
        var cur = RibbonTabPosition.Normalize(stored);
        var i = _tabPosValues.FindIndex(v => string.Equals(v, cur, StringComparison.OrdinalIgnoreCase));
        if (i < 0) { Add("Right of " + cur.Substring(RibbonTabPosition.AfterPrefix.Length) + " (not in this workspace)", cur); i = _tabPosValues.Count - 1; }
        _tabPos.SelectedIndex = i;
    }

    /// <summary>Shows a valid setting value ("None" or a chord) and clears the message.</summary>
    void SetKey(string value)
    {
        value = KeyChord.IsOff(value) ? "None" : KeyChord.TryParse(value, out var c, out _) ? c.Format() : KeyChord.Default;
        _keyValue = value;
        _key.Text = value;
        _keyMsg.Text = value == "None" ? "off" : "";
        _keyMsg.Foreground = Brushes.DimGray;
    }

    /// <summary>The box takes the next key chord: modifiers held plus the key. A lone modifier waits; Backspace / Delete turns it off.</summary>
    void OnKeyCapture(object sender, System.Windows.Input.KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        if (key is System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl or System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift
            or System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt or System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin
            or System.Windows.Input.Key.ImeProcessed or System.Windows.Input.Key.None) return;
        var mods = System.Windows.Input.Keyboard.Modifiers;
        if (mods == System.Windows.Input.ModifierKeys.None && key is System.Windows.Input.Key.Back or System.Windows.Input.Key.Delete) { SetKey("None"); return; }
        if (key == System.Windows.Input.Key.Tab && mods == System.Windows.Input.ModifierKeys.None) { e.Handled = false; return; }   // let Tab leave the box
        var name = key is >= System.Windows.Input.Key.D0 and <= System.Windows.Input.Key.D9 ? ((int)key - (int)System.Windows.Input.Key.D0).ToString() : key.ToString();   // A-Z and F1-F24 print as they are
        var text = (mods.HasFlag(System.Windows.Input.ModifierKeys.Control) ? "Ctrl+" : "") + (mods.HasFlag(System.Windows.Input.ModifierKeys.Shift) ? "Shift+" : "")
                 + (mods.HasFlag(System.Windows.Input.ModifierKeys.Alt) ? "Alt+" : "") + name;
        if (KeyChord.TryParse(text, out var chord, out var error)) SetKey(chord.Format());
        else { _keyMsg.Text = error; _keyMsg.Foreground = Brushes.Firebrick; }
    }

    static Button Btn(string text) => new() { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(4, 0, 0, 0), MinWidth = 64 };

    void AddRow(Grid grid, string label, string value, string dflt, bool folder, bool optional = false, bool editor = false)
    {
        var row = new Row { Label = label, Default = dflt, IsEditor = editor, Optional = optional };
        int r = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var lbl = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 8, 4) };
        Grid.SetRow(lbl, r); Grid.SetColumn(lbl, 0); grid.Children.Add(lbl);

        row.Box.Text = value; row.Box.Margin = new Thickness(0, 4, 4, 4); row.Box.Padding = new Thickness(3);
        row.Box.TextChanged += (_, _) => Refresh(row);
        Grid.SetRow(row.Box, r); Grid.SetColumn(row.Box, 1); grid.Children.Add(row.Box);

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        var browse = Btn("Browse...");
        browse.Click += (_, _) => Browse(row, folder);
        panel.Children.Add(browse);
        if (editor)
        {
            var useDefault = Btn("Use default"); useDefault.Click += (_, _) => row.Box.Text = RetraceSettings.DefaultEditor(); panel.Children.Add(useDefault);
        }
        var open = Btn("Open"); open.Click += (_, _) => OpenInExplorer(row); panel.Children.Add(open);
        row.Mark.Width = 70; row.Mark.VerticalAlignment = VerticalAlignment.Center; row.Mark.Margin = new Thickness(8, 0, 0, 0);
        panel.Children.Add(row.Mark);
        Grid.SetRow(panel, r); Grid.SetColumn(panel, 2); grid.Children.Add(panel);

        _rows.Add(row);
        Refresh(row);
    }

    void Refresh(Row row)
    {
        var t = Environment.ExpandEnvironmentVariables(row.Box.Text.Trim().Trim('"'));
        bool exists = t.Length > 0 && (Directory.Exists(t) || File.Exists(t) || (row.IsEditor && !t.Contains('\\') && !t.Contains('/')));
        if (t.Length == 0 && row.Optional) { row.Mark.Text = "not set"; row.Mark.Foreground = Brushes.DimGray; return; }
        row.Mark.Text = exists ? "exists" : (row.IsEditor ? "missing" : "created on use");
        row.Mark.Foreground = exists ? Brushes.ForestGreen : (row.IsEditor ? Brushes.Firebrick : Brushes.DimGray);
    }

    void Browse(Row row, bool folder)
    {
        try
        {
            if (folder)
            {
                var d = new Microsoft.Win32.OpenFolderDialog { Title = row.Label, Multiselect = false };
                var cur = Environment.ExpandEnvironmentVariables(row.Box.Text.Trim().Trim('"'));
                if (Directory.Exists(cur)) d.InitialDirectory = cur;
                if (d.ShowDialog(this) == true) row.Box.Text = d.FolderName;
            }
            else
            {
                var d = new Microsoft.Win32.OpenFileDialog { Title = row.Label, Filter = "Programs (*.exe;*.cmd;*.bat)|*.exe;*.cmd;*.bat|All files|*.*" };
                if (d.ShowDialog(this) == true) row.Box.Text = d.FileName;
            }
        }
        catch (Exception ex) { Log.Error("settings browse", ex); }
    }

    static void OpenInExplorer(Row row)
    {
        try
        {
            var t = Environment.ExpandEnvironmentVariables(row.Box.Text.Trim().Trim('"'));
            if (t.Length == 0) return;
            if (File.Exists(t)) { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{t}\"") { UseShellExecute = true }); return; }
            Directory.CreateDirectory(t);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{t}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Error("settings open", ex); }
    }

    void ResetAll()
    {
        // the source folder (row 0) has no default: it keeps what is typed
        for (int i = 1; i < _rows.Count; i++) _rows[i].Box.Text = _rows[i].Default;
        _shots.IsChecked = true;
        _dialogValues.IsChecked = true;
        _autoFill.IsChecked = true;
        _review.IsChecked = true;
        _showUi.IsChecked = true;
        _docx.IsChecked = false;
        _xlsx.IsChecked = false;
        _tabPos.SelectedIndex = 0;
        SetKey(KeyChord.Default);
    }

    void Save()
    {
        var s = Result;
        s.SourceFolder = _rows[0].Box.Text.Trim().Trim('"');
        s.DocsFolder = RetraceSettings.Normalize(_rows[1].Box.Text, s.DefaultDocs);
        s.TestsFolder = RetraceSettings.Normalize(_rows[2].Box.Text, s.DefaultTests);
        s.CapturesFolder = RetraceSettings.Normalize(_rows[3].Box.Text, s.DefaultCaptures);
        s.EditorPath = RetraceSettings.Normalize(_rows[4].Box.Text, RetraceSettings.DefaultEditor());
        s.Screenshots = _shots.IsChecked == true;
        s.DialogValues = _dialogValues.IsChecked == true;
        s.AutoFillDialogs = _autoFill.IsChecked == true;
        s.ReviewCheckpoints = _review.IsChecked == true;
        s.ShowDrivenUi = _showUi.IsChecked == true;
        s.ExportDocx = _docx.IsChecked == true;
        s.ExportXlsx = _xlsx.IsChecked == true;
        var pos = _tabPos.SelectedIndex >= 0 ? _tabPosValues[_tabPos.SelectedIndex] : RibbonTabPosition.AfterHome;
        s.RibbonTabPosition = pos == RibbonTabPosition.AfterHome ? "" : pos;
        s.CheckpointKey = _keyValue;
        Saved = true;
        Close();
    }
}
