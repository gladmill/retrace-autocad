using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

using Retrace.AutoCad;
using Retrace.Engine;
using Retrace.Engine.Map;

namespace Retrace.Ui;

/// <summary>
/// RETRACEMAP: a modeless, code-only WPF window that maps the ribbon to source code. Top: the folders to read (every folder ever added, newest
/// first, ticked or not; kept in map-folders.json) and two options. Then the Map button with its status. Below: the result, one row per ribbon
/// item with the code it runs, which can be filtered and sorted; double-click opens the code at its line. The engine runs in the background
/// (EngineJobs) and checking which folders exist runs on a thread-pool thread, because a network path can hang; reading map-folders.json is
/// quick and done here. The window never touches the AutoCAD API itself: every action goes through <see cref="Commands"/> (Commands.Map.cs).
/// An error in a handler is logged and shown in the window, never thrown into AutoCAD.
/// </summary>
sealed class MapWindow : Window
{
    static readonly Brush Muted = Brushes.DimGray;
    static readonly Brush Line = Rgb(0xDA, 0xDD, 0xE1);
    static readonly Brush Stripe = Rgb(0xF6, 0xF7, 0xF9);
    static readonly Brush MappedColor = Rgb(0x1E, 0x7B, 0x34);
    static readonly Brush NotFoundColor = Rgb(0xA8, 0x4A, 0x00);

    static Brush Rgb(byte r, byte g, byte b) { var brush = new SolidColorBrush(Color.FromRgb(r, g, b)); brush.Freeze(); return brush; }

    /// <summary>One row of the folder list: the ListBoxItem keeps it in its Tag.</summary>
    sealed class FolderItem
    {
        public required string Path;
        public required CheckBox Box;
        public required TextBlock Text;
        public required TextBlock Note;
        public required ListBoxItem Row;
    }

    readonly Commands _core;

    // folders and options
    readonly ListBox _folderList = new() { Height = 128, HorizontalContentAlignment = HorizontalAlignment.Stretch, BorderBrush = Line, SelectionMode = SelectionMode.Single };
    readonly TextBlock _folderHint = new() { Text = "No folders yet. Click \"Add folders...\" or drop folders here.", Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    readonly Button _add = SideButton("Add folders...", "Pick one or more folders. Each goes to the top of the list, ticked. You can also drop folders from Explorer onto this window.");
    readonly Button _remove = SideButton("Remove from list", "Forget the selected folder. Nothing on disk is touched.");
    readonly Button _tickAll = SideButton("Tick all", "Tick every folder in the list.");
    readonly Button _untickAll = SideButton("Untick all", "Untick every folder in the list.");
    readonly CheckBox _live = new() { Content = new TextBlock { Text = "Also map the ribbon AutoCAD shows now (tabs that plugins build in code, and the CUIX files AutoCAD has loaded)", TextWrapping = TextWrapping.Wrap }, Margin = new Thickness(0, 8, 0, 0) };
    readonly CheckBox _allTabs = new() { Content = "List every live tab, also AutoCAD's own", Margin = new Thickness(20, 4, 0, 0), ToolTip = "Off: a tab that only the live ribbon has is listed only when something in it maps to your code, so AutoCAD's own tabs stay out." };

    // the map run
    readonly Button _map = new() { Content = "Map", FontSize = 16, FontWeight = FontWeights.SemiBold, Height = 40, MinWidth = 120, Padding = new Thickness(24, 0, 24, 0), IsDefault = true, ToolTip = "Read the ticked folders and map every ribbon item to the code it runs. AutoCAD stays usable." };
    readonly Button _cancel = new() { Content = "Cancel", Height = 40, Padding = new Thickness(18, 0, 18, 0), Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed };
    readonly ProgressBar _busy = new() { IsIndeterminate = true, Width = 110, Height = 8, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Muted, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

    // the result
    readonly TextBox _filter = new() { Padding = new Thickness(4), ToolTip = "Type part of a tab, button, command, file or method to narrow the list." };
    readonly ComboBox _which = new() { Width = 150, Margin = new Thickness(8, 0, 0, 0) };
    readonly TextBlock _count = new() { Margin = new Thickness(12, 0, 0, 0), MinWidth = 70, VerticalAlignment = VerticalAlignment.Center, Foreground = Muted };
    readonly DataGrid _grid = new();
    readonly TextBlock _gridHint = new() { Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    readonly Expander _warnings = new() { Header = "Warnings", Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    readonly TextBox _warningText = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 110, BorderThickness = new Thickness(0), Padding = new Thickness(4) };

    // the bottom: what the last button did, then the buttons
    readonly TextBlock _note = new() { Foreground = Muted, Margin = new Thickness(0, 8, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    readonly Button _openCode = Bottom("Open code", "Open the first place that defines what the selected item runs, in your editor, at its line. Double-clicking a row or pressing Enter does the same.");
    readonly Button _openDefined = Bottom("Open where defined", "Open the ribbon item itself: the code that builds it, or its CUIX file in Explorer.");
    readonly Button _copy = Bottom("Copy row", "Copy the selected row as tab-separated text.");
    readonly Button _report = Bottom("Report (.md)", "Open ribbon-map.md, the report of the last map.");
    readonly Button _excel = Bottom("Excel (.csv)", "Open ribbon-map.csv, the last map as a spreadsheet.");
    readonly Button _folder = Bottom("Map folder", "Open the folder with the three files of the last map.");
    readonly Button _close = Bottom("Close", "Close this window.");

    MapFolders? _folders;
    readonly Dictionary<string, bool> _exists = new(StringComparer.OrdinalIgnoreCase);   // folder -> exists, as last checked (absent = not checked yet)
    List<MapRowView> _rows = new();
    ICollectionView? _view;
    string[] _words = [];
    bool _running, _idle = true, _closed, _hasMap;
    int _checking;

    public MapWindow(Commands core)
    {
        _core = core;
        Title = "Retrace: map the ribbon to source";
        Width = 1100; Height = 720; MinWidth = 860; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        AllowDrop = true;

        var root = new Grid { Margin = new Thickness(12) };
        for (var i = 0; i < 9; i++) root.RowDefinitions.Add(new RowDefinition { Height = i == 5 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });

        // 0 what to do
        var intro = new TextBlock { Text = "Tick the folders with your CUI and CUIX files and your LISP, C++, C# and VB.NET code. Every folder you add stays in this list, newest first.", TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(intro, 0); root.Children.Add(intro);

        // 1 the folders: the list, its hint when it is empty, and a column of buttons
        var folderRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ScrollViewer.SetHorizontalScrollBarVisibility(_folderList, ScrollBarVisibility.Disabled);   // long paths are trimmed, not scrolled
        var listHost = new Grid();
        listHost.Children.Add(_folderList); listHost.Children.Add(_folderHint);
        folderRow.Children.Add(listHost);
        var side = new StackPanel { Width = 150, Margin = new Thickness(10, 0, 0, 0) };
        side.Children.Add(_add); side.Children.Add(_remove); side.Children.Add(_tickAll); side.Children.Add(_untickAll);
        Grid.SetColumn(side, 1); folderRow.Children.Add(side);
        Grid.SetRow(folderRow, 1); root.Children.Add(folderRow);

        // 2 the two options
        var options = new StackPanel();
        options.Children.Add(_live); options.Children.Add(_allTabs);
        Grid.SetRow(options, 2); root.Children.Add(options);

        // 3 Map, Cancel and the status
        var run = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        for (var i = 0; i < 3; i++) run.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        run.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_cancel, 1); Grid.SetColumn(_busy, 2); Grid.SetColumn(_status, 3);
        run.Children.Add(_map); run.Children.Add(_cancel); run.Children.Add(_busy); run.Children.Add(_status);
        Grid.SetRow(run, 3); root.Children.Add(run);

        // 4 the filter
        var filterRow = new Grid { Margin = new Thickness(0, 12, 0, 6) };
        filterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        filterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var filterLabel = new TextBlock { Text = "Filter:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        Grid.SetColumn(_filter, 1); Grid.SetColumn(_which, 2); Grid.SetColumn(_count, 3);
        filterRow.Children.Add(filterLabel); filterRow.Children.Add(_filter); filterRow.Children.Add(_which); filterRow.Children.Add(_count);
        Grid.SetRow(filterRow, 4); root.Children.Add(filterRow);
        foreach (var w in new[] { "All items", "Mapped to code", "Not found", "No action" }) _which.Items.Add(w);
        _which.SelectedIndex = 0;

        // 5 the result
        ConfigureGrid();
        var gridHost = new Grid();
        gridHost.Children.Add(_grid); gridHost.Children.Add(_gridHint);
        Grid.SetRow(gridHost, 5); root.Children.Add(gridHost);

        // 6 the warnings, folded away
        _warnings.Content = _warningText;
        Grid.SetRow(_warnings, 6); root.Children.Add(_warnings);

        // 7 what the last button did
        Grid.SetRow(_note, 7); root.Children.Add(_note);

        // 8 the buttons
        var bottom = new DockPanel { LastChildFill = false };
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        _openCode.Margin = new Thickness(0, 8, 0, 0);   // flush with the left edge
        left.Children.Add(_openCode); left.Children.Add(_openDefined); left.Children.Add(_copy);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(_report); right.Children.Add(_excel); right.Children.Add(_folder); right.Children.Add(_close);
        DockPanel.SetDock(left, Dock.Left); DockPanel.SetDock(right, Dock.Right);
        bottom.Children.Add(left); bottom.Children.Add(right);
        Grid.SetRow(bottom, 8); root.Children.Add(bottom);
        Content = root;

        _close.IsCancel = true;   // Esc closes
        AutomationName(_folderList, "Folders to map"); AutomationName(_filter, "Filter the results"); AutomationName(_grid, "Ribbon items and the code they run");

        // folders
        _add.Click += (_, _) => Act("add folders", AddFolders);
        _remove.Click += (_, _) => Act("remove folder", RemoveSelected);
        _tickAll.Click += (_, _) => Act("tick all", () => SetAll(true));
        _untickAll.Click += (_, _) => Act("untick all", () => SetAll(false));
        _folderList.SelectionChanged += (_, _) => Act("select folder", UpdateButtons);
        _folderList.PreviewKeyDown += (_, e) => Act("folder key", () => OnFolderKey(e));
        DragEnter += OnDrag; DragOver += OnDrag; Drop += OnDrop;
        // options
        _live.Click += (_, _) => Act("option", () => { _folders!.LiveRibbon = _live.IsChecked == true; _allTabs.IsEnabled = _live.IsChecked == true; SaveFolders(); });
        _allTabs.Click += (_, _) => Act("option", () => { _folders!.AllTabs = _allTabs.IsChecked == true; SaveFolders(); });
        // the run
        _map.Click += (_, _) => Act("map", MapClicked);
        _cancel.Click += (_, _) => Act("cancel", CancelClicked);
        // the result
        _filter.TextChanged += (_, _) => Act("filter", ApplyFilter);
        _filter.PreviewKeyDown += (_, e) => Act("filter key", () => OnFilterKey(e));
        _which.SelectionChanged += (_, _) => Act("filter", ApplyFilter);
        _grid.SelectionChanged += (_, _) => Act("select row", UpdateButtons);
        _grid.PreviewKeyDown += (_, e) => Act("grid key", () => OnGridKey(e));
        // the buttons
        _openCode.Click += (_, _) => Act("open code", OpenCode);
        _openDefined.Click += (_, _) => Act("open where defined", OpenDefined);
        _copy.Click += (_, _) => Act("copy row", CopyRow);
        _report.Click += (_, _) => Act("report", () => _core.OpenMapFile(RibbonMapWriter.MarkdownFile, Say));
        _excel.Click += (_, _) => Act("excel", () => _core.OpenMapFile(RibbonMapWriter.CsvFile, Say));
        _folder.Click += (_, _) => Act("map folder", () => _core.OpenMapFile("", Say));
        _close.Click += (_, _) => Close();

        Loaded += (_, _) => Act("open", Start);
        Activated += (_, _) => Act("check folders", () => { if (_folders != null && _checking == 0 && !_closed) CheckFolders(); });   // a drive may be back
        Closed += (_, _) => _closed = true;
    }

    // ---- small helpers --------------------------------------------------------------------------------------------------------

    static Button SideButton(string text, string tip) => new() { Content = text, ToolTip = tip, Height = 28, Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(10, 3, 10, 3), HorizontalContentAlignment = HorizontalAlignment.Left };
    static Button Bottom(string text, string tip) => new() { Content = text, ToolTip = tip, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(8, 8, 0, 0) };
    static void AutomationName(DependencyObject o, string name) => System.Windows.Automation.AutomationProperties.SetName(o, name);

    /// <summary>Runs a handler's work; a failure is logged and shown in the window, never thrown into AutoCAD.</summary>
    void Act(string what, Action a)
    {
        try { a(); }
        catch (Exception ex) { Log.Error("map window: " + what, ex); try { Say("Failed: " + ex.Message); } catch (Exception) { } }
    }

    /// <summary>Shows what the last button did (always on the UI thread).</summary>
    public void Say(string text)
    {
        void Set() { _note.Text = text; _note.ToolTip = text; }
        if (Dispatcher.CheckAccess()) Set(); else Dispatcher.BeginInvoke(Set);
    }

    /// <summary>The status line next to Map. <paramref name="idle"/>: the hint before a map, which follows the ticks until something else is said.</summary>
    void SetStatus(string text, bool bad = false, bool idle = false)
    {
        _idle = idle;
        _status.Text = text;
        _status.Foreground = bad ? Brushes.Firebrick : Muted;
    }

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    // ---- opening, folders -----------------------------------------------------------------------------------------------------

    void Start()
    {
        Reload();
        _running = _core.MapRunning;
        if (_running) SetStatus("A map is running. AutoCAD stays usable.");
        else if (_core.LastMap is { } last) ShowResult(last, earlier: true);
        UpdateButtons();
        if (_folders!.Folders.Count == 0) _add.Focus();
        else if (_map.IsEnabled) _map.Focus();
        else _folderList.Focus();
    }

    /// <summary>Reads map-folders.json again (it is quick) and shows its folders and options; which folders exist is checked again.</summary>
    public void Reload()
    {
        _folders = MapFolders.Load();
        _live.IsChecked = _folders.LiveRibbon;
        _allTabs.IsChecked = _folders.AllTabs;
        _allTabs.IsEnabled = _live.IsChecked == true;
        RenderFolders();
        CheckFolders();
    }

    FolderItem? SelectedFolder => (_folderList.SelectedItem as ListBoxItem)?.Tag as FolderItem;

    IEnumerable<FolderItem> FolderItems => _folderList.Items.OfType<ListBoxItem>().Select(i => (FolderItem)i.Tag);

    /// <summary>The ticked folders that may exist, in list order (newest first): what Map reads.</summary>
    List<string> Ticked() => FolderItems.Where(f => f.Box.IsChecked == true && f.Box.IsEnabled).Select(f => f.Path).ToList();

    void RenderFolders(string? select = null)
    {
        if (_folders == null) return;
        var keep = select ?? SelectedFolder?.Path;
        var index = _folderList.SelectedIndex;
        var hadFocus = _folderList.IsKeyboardFocusWithin;
        _folderList.Items.Clear();
        foreach (var f in _folders.Folders) _folderList.Items.Add(MakeFolderRow(f));
        _folderHint.Visibility = _folders.Folders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var pick = keep == null ? null : _folderList.Items.OfType<ListBoxItem>().FirstOrDefault(i => string.Equals(((FolderItem)i.Tag).Path, keep, StringComparison.OrdinalIgnoreCase));
        if (pick != null) _folderList.SelectedItem = pick;
        else if (_folderList.Items.Count > 0) _folderList.SelectedIndex = Math.Clamp(index, 0, _folderList.Items.Count - 1);
        UpdateButtons();
        if (hadFocus && _folderList.SelectedItem is ListBoxItem row) { _folderList.UpdateLayout(); row.Focus(); }   // Delete again, Space again
    }

    ListBoxItem MakeFolderRow(MapFolder f)
    {
        var text = new TextBlock { Text = f.Path, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var box = new CheckBox { Content = text, Focusable = false, VerticalAlignment = VerticalAlignment.Center, ToolTip = f.Path };   // the row has the focus: Space ticks it
        var note = new TextBlock { Text = "(not found)", Foreground = Muted, Margin = new Thickness(10, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(note, 1);
        grid.Children.Add(box); grid.Children.Add(note);
        var row = new ListBoxItem { Content = grid, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(2, 3, 2, 3) };
        var item = new FolderItem { Path = f.Path, Box = box, Text = text, Note = note, Row = row };
        row.Tag = item;
        AutomationName(box, "Map " + f.Path);
        ApplyState(item, _exists.TryGetValue(f.Path, out var e) ? e : null, f.Checked);
        box.Click += (_, _) => Act("tick", () => OnTick(item));
        return row;
    }

    /// <summary>A folder that is not there shows "(not found)" in grey and cannot be ticked (its ticked state is kept for when it is back).</summary>
    static void ApplyState(FolderItem item, bool? exists, bool ticked)
    {
        var missing = exists == false;
        item.Box.IsEnabled = !missing;
        item.Box.IsChecked = ticked && !missing;
        item.Note.Visibility = missing ? Visibility.Visible : Visibility.Collapsed;
        if (missing) item.Text.Foreground = Muted; else item.Text.ClearValue(TextBlock.ForegroundProperty);
    }

    void OnTick(FolderItem item)
    {
        _folders!.SetChecked(item.Path, item.Box.IsChecked == true);
        _folderList.SelectedItem = item.Row;   // a click on the box selects its row too, so Remove acts on the folder you just clicked
        SaveFolders();
        UpdateButtons();
    }

    void SaveFolders()
    {
        if (!_folders!.Save(out var error)) SetStatus("Could not save the folder list: " + error, bad: true);
    }

    void AddFolders()
    {
        var d = new Microsoft.Win32.OpenFolderDialog { Title = "Add folders to map", Multiselect = true };
        var newest = _folders!.Folders.Count > 0 ? _folders.Folders[0].Path : null;
        var parent = newest == null ? null : Path.GetDirectoryName(newest);
        if (!string.IsNullOrEmpty(parent)) d.InitialDirectory = parent;   // next to the folder you used last
        if (d.ShowDialog(this) != true) return;
        AddPaths(d.FolderNames);
    }

    /// <summary>Puts folders that exist on top of the list, ticked; the first given ends first.</summary>
    void AddPaths(IEnumerable<string> paths)
    {
        var list = paths.Select(MapFolders.Normalize).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) return;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            _folders!.Add(list[i]);
            _exists[list[i]] = true;
        }
        SaveFolders();
        RenderFolders(list[0]);
        Say($"Added {MapText.Plural(list.Count, "folder")}, ticked, at the top of the list.");
    }

    void RemoveSelected()
    {
        if (SelectedFolder is not { } item) return;
        _folders!.Remove(item.Path);
        SaveFolders();
        RenderFolders();
        Say("Took " + item.Path + " off the list. The folder itself is untouched.");
    }

    void SetAll(bool on)
    {
        foreach (var f in _folders!.Folders.ToList()) _folders.SetChecked(f.Path, on && _exists.GetValueOrDefault(f.Path, true));
        SaveFolders();
        RenderFolders();
    }

    /// <summary>Space ticks the selected folder, Delete takes it off the list.</summary>
    void OnFolderKey(KeyEventArgs e)
    {
        if (SelectedFolder is not { } item) return;
        if (e.Key == Key.Space)
        {
            e.Handled = true;
            if (item.Box.IsEnabled) { item.Box.IsChecked = item.Box.IsChecked != true; OnTick(item); }
        }
        else if (e.Key == Key.Delete) { e.Handled = true; RemoveSelected(); }
    }

    // ---- drag and drop (folders, or files: their folder) ----------------------------------------------------------------------

    void OnDrag(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        try
        {
            e.Handled = true;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] dropped || dropped.Length == 0) return;
            var ui = Dispatcher;
            Task.Run(() =>
            {
                try
                {
                    var folders = dropped.Select(FolderOf).OfType<string>().ToList();
                    ui.BeginInvoke(() => Act("drop", () =>
                    {
                        if (_closed) return;
                        if (folders.Count > 0) AddPaths(folders); else Say("Nothing to add: drop folders, or files from the folders you want.");
                    }));
                }
                catch (Exception ex) { Log.Error("map window: drop", ex); }
            });
        }
        catch (Exception ex) { Log.Error("map window: drop", ex); }
    }

    /// <summary>The dropped folder, or the folder of a dropped file; null for anything else. Reads the disk: not on the UI thread.</summary>
    static string? FolderOf(string path)
    {
        try { return Directory.Exists(path) ? path : File.Exists(path) ? Path.GetDirectoryName(path) : null; }
        catch (Exception) { return null; }
    }

    // ---- which folders exist (off the UI thread) ------------------------------------------------------------------------------

    /// <summary>Checks the folders on thread-pool threads, four at a time, and updates each row when its answer comes. A path on a network
    /// drive that is away can take long to answer; the others do not wait for it.</summary>
    void CheckFolders()
    {
        var paths = _folders!.Folders.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (paths.Count == 0) return;
        _checking++;
        var ui = Dispatcher;
        Task.Run(() =>
        {
            try
            {
                Parallel.ForEach(paths, new ParallelOptions { MaxDegreeOfParallelism = 4 }, p =>
                {
                    bool ok;
                    try { ok = Directory.Exists(p); } catch (Exception) { ok = false; }
                    ui.BeginInvoke(() => Act("folder check", () => FolderChecked(p, ok)));
                });
            }
            catch (Exception ex) { Log.Error("map window: check folders", ex); }
            finally { ui.BeginInvoke(() => { _checking--; }); }
        });
    }

    void FolderChecked(string path, bool ok)
    {
        if (_closed || (_exists.TryGetValue(path, out var was) && was == ok)) return;
        _exists[path] = ok;
        var item = FolderItems.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
        if (item == null) return;
        ApplyState(item, ok, _folders!.Folders.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))?.Checked ?? false);
        UpdateButtons();
    }

    // ---- the map ----------------------------------------------------------------------------------------------------------------

    void MapClicked()
    {
        var folders = Ticked();
        if (_running || folders.Count == 0) return;
        _running = true;
        _cancel.IsEnabled = true;
        SetStatus(EngineJobs.IsRunning ? "Another Retrace job is running; the map starts when it is done. AutoCAD stays usable."
                                       : $"Mapping {MapText.Plural(folders.Count, "folder")}... AutoCAD stays usable.");
        UpdateButtons();
        if (!_core.StartMap(folders, _live.IsChecked == true, _allTabs.IsChecked == true, _core.MapDoneForWindow, out var error))
        {
            _running = false;
            SetStatus(error, bad: true);
            UpdateButtons();
            return;
        }
        Reload();   // the folders of this map moved to the top of the list
    }

    void CancelClicked()
    {
        _cancel.IsEnabled = false;
        SetStatus("Cancelling...");
        EngineJobs.Cancel("map");
    }

    /// <summary>The map has finished, failed or been cancelled (called by <see cref="Commands"/> on the UI thread).</summary>
    public void MapDone(EngineResult r)
    {
        if (_closed) return;
        _running = false;
        Act("show result", () => ShowResult(r, earlier: false));
        UpdateButtons();
    }

    void ShowResult(EngineResult r, bool earlier)
    {
        if (r.Cancelled) { SetStatus("Cancelled."); return; }
        SetWarnings(r.Warnings);
        if (r.RibbonMap is not { } map) { SetStatus(r.Error ?? "The map failed.", bad: true); return; }
        ShowRows(map.Rows);
        var text = MapText.Summary(map, r.Warnings.Count);
        if (earlier) text = $"Map from {map.Generated.ToLocalTime():HH:mm}: {text}";
        if (r.Success) SetStatus(text);
        else SetStatus($"{text} Could not write the files: {r.Error}", bad: true);
    }

    void SetWarnings(IReadOnlyList<string> warnings)
    {
        _warnings.Header = $"Warnings ({warnings.Count})";
        _warningText.Text = string.Join("\n", warnings);
        _warnings.Visibility = warnings.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- the result grid --------------------------------------------------------------------------------------------------------

    MapRowView? Selected => _grid.SelectedItem as MapRowView;

    void ConfigureGrid()
    {
        _grid.AutoGenerateColumns = false;
        _grid.IsReadOnly = true;
        _grid.CanUserAddRows = false;
        _grid.CanUserDeleteRows = false;
        _grid.SelectionMode = DataGridSelectionMode.Single;
        _grid.SelectionUnit = DataGridSelectionUnit.FullRow;
        _grid.HeadersVisibility = DataGridHeadersVisibility.Column;
        _grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
        _grid.HorizontalGridLinesBrush = Line;
        _grid.BorderBrush = Line;
        _grid.Background = Brushes.White;
        _grid.AlternationCount = 2;
        _grid.AlternatingRowBackground = Stripe;
        _grid.EnableRowVirtualization = true;

        _grid.Columns.Add(Column("Tab", nameof(MapRowView.Tab), new DataGridLength(110), 70));
        _grid.Columns.Add(Column("Panel", nameof(MapRowView.Panel), new DataGridLength(100), 70));
        _grid.Columns.Add(Column("Item", nameof(MapRowView.Item), Star(1.0), 110, tip: nameof(MapRowView.ItemTip)));
        _grid.Columns.Add(Column("Runs", nameof(MapRowView.Runs), Star(1.1), 110));
        _grid.Columns.Add(Column("Code", nameof(MapRowView.Code), Star(1.5), 140, tip: nameof(MapRowView.CodeTip)));
        _grid.Columns.Add(Column("File", nameof(MapRowView.File), Star(1.2), 120, tip: nameof(MapRowView.FileTip)));
        _grid.Columns.Add(Column("Found by", nameof(MapRowView.FoundBy), Star(0.9), 110));
        _grid.Columns.Add(StatusColumn());

        var rows = new Style(typeof(DataGridRow));
        rows.Setters.Add(new EventSetter(Control.MouseDoubleClickEvent, new MouseButtonEventHandler(OnRowDoubleClick)));
        _grid.RowStyle = rows;
        UpdateGridHint();
    }

    static DataGridLength Star(double n) => new(n, DataGridLengthUnitType.Star);

    static DataGridTextColumn Column(string header, string path, DataGridLength width, double minWidth, string? tip = null) => new()
    {
        Header = header, Binding = new Binding(path) { Mode = BindingMode.OneWay }, Width = width, MinWidth = minWidth, ElementStyle = CellStyle(tip),
    };

    /// <summary>The Status column: the status in words, coloured when the row is not selected (a selected row keeps the highlight's text colour).</summary>
    static DataGridTextColumn StatusColumn()
    {
        var style = CellStyle(nameof(MapRowView.StatusTip));
        void Colour(MapStatus status, Brush brush)
        {
            var t = new MultiDataTrigger();
            t.Conditions.Add(new Condition(new Binding(nameof(MapRowView.Status)), status));
            t.Conditions.Add(new Condition(new Binding("IsSelected") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(DataGridCell), 1) }, false));
            t.Setters.Add(new Setter(TextBlock.ForegroundProperty, brush));
            style.Triggers.Add(t);
        }
        Colour(MapStatus.Mapped, MappedColor);
        Colour(MapStatus.NotFound, NotFoundColor);
        Colour(MapStatus.NoAction, Muted);
        return new DataGridTextColumn
        {
            Header = "Status", Binding = new Binding(nameof(MapRowView.StatusText)) { Mode = BindingMode.OneWay }, SortMemberPath = nameof(MapRowView.Status),
            Width = new DataGridLength(90), MinWidth = 80, ElementStyle = style,
        };
    }

    /// <summary>A cell's text: one line, trimmed with "..." when the column is narrow, with the tooltip from the row's property <paramref name="tip"/>.</summary>
    static Style CellStyle(string? tip)
    {
        var s = new Style(typeof(TextBlock));
        s.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        s.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(4, 2, 4, 2)));
        s.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        if (tip != null) s.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(tip)));
        return s;
    }

    void ShowRows(IReadOnlyList<MapRow> rows)
    {
        _hasMap = true;
        _rows = rows.Select(r => new MapRowView(r)).ToList();
        _grid.ItemsSource = _rows;
        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = o => o is MapRowView v && Passes(v);
        foreach (var c in _grid.Columns) c.SortDirection = null;   // the new rows come in the ribbon's order
        ApplyFilter();
    }

    bool Passes(MapRowView v) => _which.SelectedIndex switch
    {
        1 => v.Status == MapStatus.Mapped,
        2 => v.Status == MapStatus.NotFound,
        3 => v.Status == MapStatus.NoAction,
        _ => true,
    } && v.Matches(_words);

    void ApplyFilter()
    {
        _words = MapText.Words(_filter.Text);
        _view?.Refresh();
        var shown = _rows.Count(Passes);
        _count.Text = _rows.Count == 0 ? "" : $"{shown} of {_rows.Count}";
        UpdateGridHint(shown);
        UpdateButtons();
    }

    /// <summary>The words in the empty grid: what to do before a map, or why nothing shows.</summary>
    void UpdateGridHint(int shown = 0)
    {
        _gridHint.Text = !_hasMap ? "Press Map to list every ribbon item with the code it runs."
                       : _rows.Count == 0 ? "No ribbon items were found."
                       : "No item matches the filter.";
        _gridHint.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGridRow { Item: MapRowView row }) return;
        e.Handled = true;
        _grid.SelectedItem = row;
        Act("open code", OpenCode);
    }

    /// <summary>Enter opens the code of the selected row (and never starts a map from here); Ctrl+C copies the row.</summary>
    void OnGridKey(KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; if (Selected != null) OpenCode(); }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && Selected != null) { e.Handled = true; CopyRow(); }
    }

    /// <summary>Esc empties the filter first and closes the window when it is empty. Enter must not start a map from here.</summary>
    void OnFilterKey(KeyEventArgs e)
    {
        if (e.Key == Key.Enter) e.Handled = true;
        else if (e.Key == Key.Escape && _filter.Text.Length > 0) { e.Handled = true; _filter.Clear(); }
    }

    // ---- the buttons ------------------------------------------------------------------------------------------------------------

    void OpenCode()
    {
        if (Selected?.FirstSource is { } src) _core.OpenSource(src, Say);
        else Say("Nothing was found for this item, so there is no code to open.");
    }

    void OpenDefined()
    {
        if (Selected?.Row.DefinedIn is { } src) _core.OpenSource(src, Say);
        else Say("Only the live ribbon has this item: no file defines it.");
    }

    void CopyRow()
    {
        if (Selected is not { } row) return;
        try
        {
            Clipboard.SetText(row.CopyText());
            Say("Copied the row.");
        }
        catch (Exception ex) { Log.Error("map window: clipboard", ex); Say("Could not copy: " + ex.Message); }   // another program may hold the clipboard
    }

    void UpdateButtons()
    {
        if (_closed || _folders == null) return;
        var ticked = Ticked().Count;
        _remove.IsEnabled = SelectedFolder != null;
        _tickAll.IsEnabled = _untickAll.IsEnabled = _folders.Folders.Count > 0;
        _map.IsEnabled = !_running && ticked > 0;
        _cancel.Visibility = _busy.Visibility = _running ? Visibility.Visible : Visibility.Collapsed;
        _openCode.IsEnabled = Selected?.FirstSource != null;
        _openDefined.IsEnabled = Selected?.Row.DefinedIn != null;
        _copy.IsEnabled = Selected != null;
        if (_idle) SetStatus(MapText.IdleStatus(_folders.Folders.Count, ticked), idle: true);
    }
}
