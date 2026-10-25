using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using Retrace.AutoCad;

namespace Retrace.Ui;

/// <summary>
/// The palette's content (code-only WPF): toolbar, page tree on the left, preview (WebView2) and, in edit mode, a text editor on the right.
/// All file work is small and synchronous; WebView2 calls are async. Commands go out through RetraceApp.SendCommand (SendStringToExecute).
/// </summary>
sealed class GuideControl : UserControl
{
    // layout
    readonly TreeView _tree = new() { BorderThickness = new Thickness(0, 0, 1, 0) };
    readonly TextBox _editor = new()
    {
        AcceptsReturn = true, AcceptsTab = true, FontFamily = new FontFamily("Consolas"), FontSize = 13,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        TextWrapping = TextWrapping.NoWrap, BorderThickness = new Thickness(0),
    };
    readonly GridSplitter _editSplit = new() { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.LightGray };
    readonly Grid _content = new();
    readonly ColumnDefinition _editorCol = new() { Width = new GridLength(0) };
    readonly ColumnDefinition _splitCol = new() { Width = new GridLength(0) };
    readonly TextBlock _status = new() { Margin = new Thickness(8, 3, 8, 3), Foreground = Brushes.DimGray, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly StackPanel _empty = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(24), Visibility = Visibility.Collapsed };
    readonly TextBlock _emptyText = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 420, FontSize = 14, Margin = new Thickness(0, 0, 0, 12) };
    readonly TextBlock _webNote = new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12), Visibility = Visibility.Collapsed };
    readonly Border _previewHost = new();

    readonly Button _back = Tool("Back", "Previous page");
    readonly Button _forward = Tool("Forward", "Next page");
    readonly ToggleButton _editToggle = new() { Content = "Edit", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(2), ToolTip = "Switch between reading and editing this page" };
    readonly Button _save = Tool("Save", "Save the page (Ctrl+S)");
    readonly Button _revert = Tool("Revert", "Throw away unsaved changes");

    // state
    RetraceSettings _settings = new();
    string _root = "";           // the output folder (virtual host root)
    string _guideDir = "";       // <root>/guide
    string? _current;            // full path of the open page
    string _diskText = "";       // what the file held when it was loaded or last saved
    DateTime _diskStamp;
    readonly List<string> _history = new();
    int _historyPos = -1;
    bool _syncing;
    bool _editing;
    bool _webStarted;
    WebPreview? _preview;
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(400) };

    static Button Tool(string text, string tip) => new() { Content = text, ToolTip = tip, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(2) };

    public GuideControl()
    {
        var bar = new WrapPanel { Margin = new Thickness(4, 2, 4, 2) };
        bar.Children.Add(_back); bar.Children.Add(_forward);
        bar.Children.Add(Tool("Refresh", "Re-read the page list from the guide folder", RefreshClicked));
        bar.Children.Add(_editToggle); bar.Children.Add(_save); bar.Children.Add(_revert);
        bar.Children.Add(Tool("Open in editor", "Open this page in your external editor (Settings: Editor)", OpenInEditor));
        bar.Children.Add(Tool("Open folder", "Show this page's folder in Explorer", OpenFolder));
        bar.Children.Add(Tool("Build docs", "Run RETRACEDOCS: rebuild the generated parts of the guide, reference and test plans", BuildGuide));

        _back.Click += (_, _) => Step(-1);
        _forward.Click += (_, _) => Step(+1);
        _editToggle.Checked += (_, _) => SetEditing(true);
        _editToggle.Unchecked += (_, _) => SetEditing(false);
        _save.Click += (_, _) => Save();
        _revert.Click += (_, _) => Revert();

        _content.ColumnDefinitions.Add(_editorCol);
        _content.ColumnDefinitions.Add(_splitCol);
        _content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_editor, 0); _content.Children.Add(_editor);
        Grid.SetColumn(_editSplit, 1); _content.Children.Add(_editSplit);
        var right = new Grid();
        right.Children.Add(_previewHost);
        right.Children.Add(_webNote);
        _webNote.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(right, 2); _content.Children.Add(right);
        _editor.Visibility = Visibility.Collapsed; _editSplit.Visibility = Visibility.Collapsed;

        _empty.Children.Add(_emptyText);
        var build = new Button { Content = "Build guide", Padding = new Thickness(14, 5, 14, 5), HorizontalAlignment = HorizontalAlignment.Left };
        build.Click += (_, _) => BuildGuide(null, null!);
        _empty.Children.Add(build);
        right.Children.Add(_empty);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230), MinWidth = 120 });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_tree, 0); body.Children.Add(_tree);
        var split = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.LightGray };
        Grid.SetColumn(split, 1); body.Children.Add(split);
        Grid.SetColumn(_content, 2); body.Children.Add(_content);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar);
        DockPanel.SetDock(_status, Dock.Bottom); root.Children.Add(_status);
        root.Children.Add(body);
        Content = root;

        _tree.SelectedItemChanged += TreeSelected;
        _editor.TextChanged += (_, _) => EditorChanged();
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = UpdatePreviewAsync(); };
        PreviewKeyDown += OnKeyDown;
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) OnPaletteActivated(); };
        Loaded += (_, _) => StartWeb();
        UpdateButtons();
    }

    Button Tool(string text, string tip, RoutedEventHandler click)
    {
        var b = Tool(text, tip);
        b.Click += click;
        return b;
    }

    // ---- guide folder and tree --------------------------------------------------------------------------------------

    /// <summary>Called when the palette is shown or activated again: re-reads settings and the page list, and reloads the open page if it changed on disk (and has no unsaved edits).</summary>
    public void OnPaletteActivated()
    {
        try
        {
            RefreshTree();
            if (_current != null && !IsDirty && File.Exists(_current) && File.GetLastWriteTimeUtc(_current) != _diskStamp) LoadPage(_current, null, false);
        }
        catch (Exception ex) { Log.Error("activate", ex); }
    }

    void RefreshClicked(object? s, RoutedEventArgs e) => RefreshTree();

    public void RefreshTree()
    {
        try
        {
            _settings = RetraceSettings.Load();
            var newRoot = _settings.Docs;
            if (!string.Equals(newRoot, _root, StringComparison.OrdinalIgnoreCase)) { _root = newRoot; _preview?.SetRootSafe(_root); }
            _guideDir = Path.Combine(_root, "guide");
            var nodes = GuideTree.Build(_guideDir);

            _syncing = true;
            _tree.Items.Clear();
            TreeViewItem? toSelect = null;
            foreach (var n in nodes) _tree.Items.Add(MakeItem(n, ref toSelect));
            _syncing = false;

            if (nodes.Count == 0)
            {
                ShowEmpty("There is no guide yet.\n\nIt is built from your code, captures and recordings and written to:\n" + _guideDir
                          + "\n\nClick Build docs (set the source folder in Settings first if you have not).");
                _current = null; _diskText = "";
                UpdateButtons();
                return;
            }
            _empty.Visibility = Visibility.Collapsed;
            _previewHost.Visibility = Visibility.Visible;

            if (_current != null && File.Exists(_current)) SelectInTree(_current);
            else
            {
                var first = nodes.SelectMany(n => n.Flatten()).FirstOrDefault(n => n.Path != null);
                if (first?.Path != null) LoadPage(first.Path, null, true);
            }
            _status.Text = _current == null ? "" : StatusText();
        }
        catch (Exception ex) { _syncing = false; Log.Error("refresh", ex); _status.Text = "Refresh failed: " + ex.Message; }
    }

    TreeViewItem MakeItem(PageNode n, ref TreeViewItem? toSelect)
    {
        var item = new TreeViewItem { Header = n.Title, Tag = n.Path, IsExpanded = true, ToolTip = n.Path };
        if (n.Path == null) item.FontWeight = FontWeights.SemiBold;
        foreach (var c in n.Children) item.Items.Add(MakeItem(c, ref toSelect));
        return item;
    }

    void ShowEmpty(string text)
    {
        _emptyText.Text = text;
        _empty.Visibility = Visibility.Visible;
        _previewHost.Visibility = Visibility.Collapsed;
        _status.Text = "";
    }

    TreeViewItem? FindItem(ItemsControl parent, string path)
    {
        foreach (var o in parent.Items)
        {
            if (o is not TreeViewItem t) continue;
            if (t.Tag is string p && string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) return t;
            var inner = FindItem(t, path);
            if (inner != null) return inner;
        }
        return null;
    }

    void SelectInTree(string path)
    {
        var item = FindItem(_tree, path);
        if (item == null) return;
        _syncing = true;
        try { item.IsSelected = true; item.BringIntoView(); } finally { _syncing = false; }
    }

    void TreeSelected(object? s, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_syncing || e.NewValue is not TreeViewItem { Tag: string path }) return;
        if (string.Equals(path, _current, StringComparison.OrdinalIgnoreCase)) return;
        if (!ConfirmLeave()) { if (_current != null) SelectInTree(_current); return; }
        LoadPage(path, null, true);
    }

    // ---- pages and history ------------------------------------------------------------------------------------------

    bool IsDirty => _current != null && PageFile.Normalize(_editor.Text) != PageFile.Normalize(_diskText);

    /// <summary>Opens a page (after the caller has handled unsaved changes). addHistory false for Back/Forward and reloads.</summary>
    void LoadPage(string path, string? fragment, bool addHistory)
    {
        try
        {
            _diskText = File.Exists(path) ? File.ReadAllText(path) : "";
            _diskStamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
            _current = path;
            _editor.Text = _diskText;     // TextChanged -> dirty check sees equal text
            if (addHistory)
            {
                if (_historyPos < _history.Count - 1) _history.RemoveRange(_historyPos + 1, _history.Count - _historyPos - 1);
                if (_history.Count == 0 || !string.Equals(_history[^1], path, StringComparison.OrdinalIgnoreCase)) { _history.Add(path); _historyPos = _history.Count - 1; }
            }
            SelectInTree(path);
            _preview?.Navigate(GuideLinks.ToUrl(path, _root, fragment));
            _status.Text = StatusText();
            UpdateButtons();
        }
        catch (Exception ex) { Log.Error("load " + path, ex); _status.Text = "Could not open " + path + ": " + ex.Message; }
    }

    string StatusText() => (_current == null ? "" : Path.GetRelativePath(_root, _current)) + (IsDirty ? "   (unsaved changes)" : "");

    void Step(int delta)
    {
        var pos = _historyPos + delta;
        if (pos < 0 || pos >= _history.Count) return;
        if (!ConfirmLeave()) return;
        _historyPos = pos;
        LoadPage(_history[pos], null, false);
    }

    /// <summary>A link to another .md was clicked inside the preview.</summary>
    void OnMarkdownLink(string file, string? fragment)
    {
        if (string.Equals(file, _current, StringComparison.OrdinalIgnoreCase))
        {
            _preview?.Navigate(GuideLinks.ToUrl(file, _root, fragment));
            return;
        }
        if (!ConfirmLeave()) return;
        LoadPage(file, fragment, true);
    }

    /// <summary>True when it is fine to leave the page: nothing unsaved, or the user saved or discarded.</summary>
    bool ConfirmLeave()
    {
        if (!IsDirty) return true;
        var r = MessageBox.Show("Save your changes to " + Path.GetFileName(_current) + "?", "Retrace Guide", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.Yes) return Save();
        return true;   // No: discard
    }

    /// <summary>Called when AutoCAD shuts down: gives unsaved edits one last chance.</summary>
    public void ConfirmShutdown() { try { ConfirmLeave(); } catch (Exception) { } _preview?.Close(); }

    // ---- editing ---------------------------------------------------------------------------------------------------

    void SetEditing(bool on)
    {
        _editing = on;
        _editor.Visibility = _editSplit.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _editorCol.Width = on ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        _splitCol.Width = on ? GridLength.Auto : new GridLength(0);
        if (!on && IsDirty)
        {
            // switching back to reading with unsaved text: keep the text, the preview shows it until saved or reverted
            _status.Text = StatusText();
        }
        UpdateButtons();
        if (on) _editor.Focus();
        _ = UpdatePreviewAsync();
    }

    void EditorChanged()
    {
        if (_current == null) return;
        _status.Text = StatusText();
        UpdateButtons();
        if (_editing) { _debounce.Stop(); _debounce.Start(); }
    }

    async Task UpdatePreviewAsync()
    {
        if (_preview == null || !_preview.IsReady || _current == null) return;
        if (!await _preview.UpdateBodyAsync(CurrentPreviewText())) _preview.Reload();
    }

    /// <summary>What the preview shows: the editor text while editing or while there are unsaved edits, else the file.</summary>
    string CurrentPreviewText() => _editor.Text;

    void Revert()
    {
        if (_current == null || !IsDirty) return;
        if (MessageBox.Show("Throw away your unsaved changes to " + Path.GetFileName(_current) + "?", "Retrace Guide", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        LoadPage(_current, null, false);
    }

    /// <summary>Saves the open page. False if cancelled or failed.</summary>
    bool Save()
    {
        if (_current == null) return false;
        try
        {
            var onDisk = File.Exists(_current) ? File.ReadAllText(_current) : "";
            var edited = GeneratedBlocks.EditedBlocks(onDisk, _editor.Text);
            if (edited.Count > 0)
            {
                var r = MessageBox.Show(
                    "You edited generated text in block " + string.Join(", ", edited) + "; the next Build guide will replace it.\n\nWrite your own text outside the engine:begin / engine:end lines instead.",
                    "Retrace Guide", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
                if (r != MessageBoxResult.OK) return false;
            }
            PageFile.Save(_current, _editor.Text);
            _diskText = File.ReadAllText(_current);
            _diskStamp = File.GetLastWriteTimeUtc(_current);
            _status.Text = "Saved " + Path.GetRelativePath(_root, _current);
            UpdateButtons();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("save", ex);
            MessageBox.Show("Could not save " + _current + ":\n" + ex.Message, "Retrace Guide", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    void OnKeyDown(object? s, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control && _editing) { Save(); e.Handled = true; }
    }

    void UpdateButtons()
    {
        _back.IsEnabled = _historyPos > 0;
        _forward.IsEnabled = _historyPos < _history.Count - 1;
        _save.IsEnabled = _editing && IsDirty;
        _revert.IsEnabled = IsDirty;
        _editToggle.IsEnabled = _current != null;
    }

    // ---- toolbar actions ---------------------------------------------------------------------------------------------

    void OpenInEditor(object? s, RoutedEventArgs e)
    {
        if (_current == null) return;
        try
        {
            if (!ConfirmLeave()) return;
            var editor = _settings.Editor;
            Process.Start(new ProcessStartInfo(editor, "\"" + _current + "\"") { UseShellExecute = true });
            _status.Text = "Opened in " + editor + ". Come back here and the page reloads if you saved changes.";
        }
        catch (Exception ex) { Log.Error("open in editor", ex); _status.Text = "Could not start the editor (" + _settings.Editor + "): " + ex.Message + ". Change it in Settings."; }
    }

    void OpenFolder(object? s, RoutedEventArgs e)
    {
        try
        {
            var dir = _current != null ? Path.GetDirectoryName(_current)! : _guideDir;
            if (!Directory.Exists(dir)) dir = _root;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Error("open folder", ex); _status.Text = "Could not open the folder: " + ex.Message; }
    }

    void BuildGuide(object? s, RoutedEventArgs e)
    {
        if (RetraceApp.SendCommand("_RETRACEDOCS ", out var err))
            _status.Text = "Building the guide: watch the AutoCAD command line. The page list refreshes when you come back here.";
        else _status.Text = err;
    }

    // ---- WebView2 -------------------------------------------------------------------------------------------------

    async void StartWeb()
    {
        if (_webStarted) return;
        _webStarted = true;
        try
        {
            _settings = RetraceSettings.Load();
            _root = _settings.Docs;
            var p = new WebPreview { TextFor = TextForRequest, MarkdownLinkClicked = (f, frag) => Dispatcher.BeginInvoke(() => OnMarkdownLink(f, frag)) };
            _previewHost.Child = p;
            await p.InitAsync(_root);
            _preview = p;
            if (_current != null) p.Navigate(GuideLinks.ToUrl(_current, _root));
            else RefreshTree();
        }
        catch (Exception ex)
        {
            Log.Error("webview2", ex);
            _preview = null;
            _previewHost.Child = null;
            _webNote.Visibility = Visibility.Visible;
            _webNote.Text = "The preview needs the Microsoft Edge WebView2 Runtime, which could not be started (" + ex.GetType().Name + ": " + ex.Message
                            + ").\nInstall it from https://developer.microsoft.com/microsoft-edge/webview2/ . Editing still works: switch to Edit, or use Open in editor.";
            if (_current == null) RefreshTree();
        }
    }

    /// <summary>The unsaved editor text of the page being requested, so a reload or a switch to Preview shows what is typed.</summary>
    string? TextForRequest(string file) =>
        _current != null && string.Equals(file, _current, StringComparison.OrdinalIgnoreCase) && IsDirty ? _editor.Text : null;
}

static class WebPreviewExt
{
    public static void SetRootSafe(this WebPreview p, string root)
    {
        try { p.SetRoot(root); } catch (Exception ex) { Log.Error("set root", ex); }
    }
}
