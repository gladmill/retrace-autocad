using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

using Retrace.AutoCad;
using Retrace.Core;
using Retrace.Engine.Recordings;

namespace Retrace.Ui;

/// <summary>
/// RETRACEEVIDENCE: a person walks a recorded test in AutoCAD and files pictures as evidence. The window lists the steps of the chosen test (the rows of its sign-off sheet), highlights the current one (Previous / Next), and has a
/// camera button and a hotkey (<see cref="HotkeyText"/>, handled while the window is open and the key reaches AutoCAD: a key pressed in another program is that program's): one press hides every window of Retrace's own (this one,
/// Open test ...), captures AutoCAD's window (palettes and dialogs of AutoCAD included), shows them again without taking the keyboard focus from AutoCAD, and files the picture under the current step in the test's
/// <c>evidence\</c> folder. Several pictures per step; Retake and Delete (a removed picture is moved to <c>evidence\removed\</c>). Write workbook writes <c>steps-evidence_&lt;time&gt;.xlsx</c> beside steps.xlsx with every picture
/// fitted in the Evidence column; steps.xlsx is never touched. Tester, Date and each step's Result and Notes are the tester's to fill in here (kept per test while the window is open; Result stays "Not run" in the workbook unless set).
/// Code-only WPF like the other windows. Disk reads run on a pool thread; no exception leaves a handler (the project's rule inside AutoCAD).
/// </summary>
sealed class EvidenceWindow : Window
{
    public const string HotkeyText = "Ctrl+Shift+F12";

    sealed class RowVm
    {
        public string Label = "", Action = "", Result = "Not run", Notes = "";
        public int Pictures;
        public string Text => $"{Label,-5} {Action}   [{Pictures} picture{(Pictures == 1 ? "" : "s")}{(Result == "Not run" ? "" : ", " + Result)}]";
    }

    readonly ComboBox _tests = new() { Width = 300, Margin = new Thickness(0, 0, 8, 4) };
    readonly TextBox _tester = new() { Width = 120, Margin = new Thickness(0, 0, 12, 4) };
    readonly TextBox _date = new() { Width = 100, Text = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
    readonly ListBox _list = new() { DisplayMemberPath = nameof(RowVm.Text), MinHeight = 160, FontFamily = new FontFamily("Consolas") };
    readonly ListBox _pics = new() { Height = 70, Margin = new Thickness(0, 6, 0, 0) };
    readonly ComboBox _result = new() { Width = 100, ItemsSource = ExportXlsx.Results, Margin = new Thickness(0, 0, 8, 0) };
    readonly TextBox _notes = new() { MinWidth = 200 };
    readonly TextBlock _count = new() { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) };
    readonly TextBlock _warn = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Firebrick, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
    readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 6, 0, 0) };
    readonly Button _camera = new() { Content = "Camera: take a picture (" + HotkeyText + ")", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 4), FontWeight = FontWeights.SemiBold };
    readonly Button _write = new() { Content = "Write workbook", Padding = new Thickness(14, 6, 14, 6) };
    readonly DispatcherTimer _wait = new() { Interval = TimeSpan.FromMilliseconds(450) };

    readonly string? _first;
    readonly Dictionary<string, Dictionary<string, ManualRow>> _saved = new(StringComparer.OrdinalIgnoreCase);   // Result and Notes typed per test folder, kept while the window is open
    List<TestEntry> _all = [];
    string? _folder;
    StepsDoc? _doc;
    string _stepsText = "";
    List<RowVm> _rows = [];
    int _loadId;
    bool _busy, _loading, _closed, _subscribed;
    PictureWindows? _hidden;   // Retrace's windows while a picture is being taken
    string? _retakeOf;   // the picture a Retake replaces: removed only once the new one is on disk

    public EvidenceWindow(string? testFolder)
    {
        _first = testFolder;
        Title = "Retrace: evidence";
        Width = 560; Height = 620; MinWidth = 480; MinHeight = 480;
        Retrace.AutoCad.WindowPlacement.MainInterface(this);   // owned by AutoCAD, centred on it, size remembered
        ShowInTaskbar = false;
        ShowActivated = false;   // AutoCAD keeps the keyboard focus when this window shows again after a picture

        var root = new DockPanel { Margin = new Thickness(12) };
        var top = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        top.Children.Add(new TextBlock { Text = "Test", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 4) });
        top.Children.Add(_tests);
        var refresh = new Button { Content = "Refresh", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 12, 4), ToolTip = "Read the list of tests again." };
        refresh.Click += (_, _) => Guard("evidence refresh", ReloadTests);
        top.Children.Add(refresh);
        top.Children.Add(new TextBlock { Text = "Tester", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 4) });
        top.Children.Add(_tester);
        top.Children.Add(new TextBlock { Text = "Date", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 4) });
        top.Children.Add(_date);
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);

        var nav = new WrapPanel { Margin = new Thickness(0, 6, 0, 6) };
        var prev = new Button { Content = "Previous step", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 4) };
        var next = new Button { Content = "Next step", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 16, 4) };
        prev.Click += (_, _) => Guard("evidence previous", () => Move(-1)); next.Click += (_, _) => Guard("evidence next", () => Move(+1));
        var retake = new Button { Content = "Retake", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 4), ToolTip = "Take another picture of this step; the step's last picture is removed once the new one is filed." };
        retake.Click += (_, _) => Guard("evidence retake", () => { _retakeOf = LastPicture(); Capture(); });
        _camera.Click += (_, _) => Guard("evidence camera", Capture);
        nav.Children.Add(prev); nav.Children.Add(next); nav.Children.Add(_camera); nav.Children.Add(retake);
        DockPanel.SetDock(nav, Dock.Top); root.Children.Add(nav);

        var bottom = new StackPanel();
        var resultRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        resultRow.Children.Add(new TextBlock { Text = "This step: Result", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        resultRow.Children.Add(_result);
        resultRow.Children.Add(new TextBlock { Text = "Notes", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        resultRow.Children.Add(_notes);
        bottom.Children.Add(resultRow);
        bottom.Children.Add(new TextBlock { Text = "Pictures of this step (the picked one can be deleted; it is moved to evidence\\removed):", Margin = new Thickness(0, 8, 0, 0), Foreground = Brushes.DimGray });
        bottom.Children.Add(_pics);
        var del = new Button { Content = "Delete picture", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 4, 8, 0), HorizontalAlignment = HorizontalAlignment.Left };
        del.Click += (_, _) => Guard("evidence delete", RemovePicked);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        actions.Children.Add(del); actions.Children.Add(_write);
        var openFolder = new Button { Content = "Open evidence folder", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(8, 4, 0, 0) };
        openFolder.Click += (_, _) => Guard("evidence open folder", () => OpenPath(_folder == null ? null : EvidenceFiles.FolderOf(_folder)));
        actions.Children.Add(openFolder);
        bottom.Children.Add(actions);
        bottom.Children.Add(_count);
        bottom.Children.Add(_warn);
        bottom.Children.Add(_status);
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        root.Children.Add(_list);
        Content = root;

        _write.Click += (_, _) => Guard("evidence write", WriteWorkbook);
        _list.SelectionChanged += (_, _) => Guard("evidence select", ShowCurrent);
        _tests.SelectionChanged += (_, _) => Guard("evidence test", () => { if (!_loading && _tests.SelectedItem is TestEntry e) LoadTest(e.Folder); });
        _result.SelectionChanged += (_, _) => Guard("evidence result", () => { if (Current is { } r && _result.SelectedItem is string s && r.Result != s) { r.Result = s; Refresh(); } });
        _notes.TextChanged += (_, _) => Guard("evidence notes", () => { if (Current is { } r) r.Notes = _notes.Text; });
        _wait.Tick += (_, _) => { _wait.Stop(); Guard("evidence shot", Shoot); };
        SourceInitialized += (_, _) => Guard("evidence hotkey", Subscribe);
        Closed += (_, _) => Guard("evidence closed", OnClosed);
        Loaded += (_, _) => Guard("evidence load", ReloadTests);
    }

    RowVm? Current => _list.SelectedItem as RowVm;

    /// <summary>Runs a handler's work; whatever it throws goes to retrace.log and one line in the window (no exception leaves a handler inside AutoCAD).</summary>
    void Guard(string where, Action a)
    {
        try { a(); }
        catch (Exception ex) { Log.Error(where, ex); try { Say("Failed: " + ex.Message); } catch (Exception) { } }
    }

    void OnClosed()
    {
        _closed = true;
        _wait.Stop();
        if (_subscribed) { AcApp.PreTranslateMessage -= OnPreTranslate; _subscribed = false; }
        EvidenceKey.Active = false;
        _hidden?.Restore(); _hidden = null;
    }

    // ---- the hotkey ---------------------------------------------------------------------------------------------------

    void Subscribe()
    {
        if (_subscribed) return;
        if (EvidenceKey.CheckpointUsesIt()) { Say($"{HotkeyText} is also your checkpoint shortcut (setting checkpointKey): the checkpoint wins. Use the camera button."); return; }
        AcApp.PreTranslateMessage += OnPreTranslate;   // only messages that reach AutoCAD's own UI thread: a key pressed in another program never gets here
        _subscribed = true;
        EvidenceKey.Active = true;   // the recorder leaves the chord alone
    }

    // Runs for every UI-thread message: keep it allocation-free.
    void OnPreTranslate(object? s, PreTranslateMessageEventArgs e)
    {
        try
        {
            if (!EvidenceKey.IsChord(e.Message.message, e.Message.wParam)) return;
            e.Handled = true;   // the shortcut is ours
            if (((long)e.Message.lParam & (1L << 30)) != 0) return;   // auto-repeat: swallowed, not a second picture
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => Guard("evidence hotkey", Capture)));
        }
        catch (Exception ex) { Log.Error("evidence pretranslate", ex); }
    }

    // ---- tests and steps ------------------------------------------------------------------------------------------------

    void Say(string text) { if (Dispatcher.CheckAccess()) _status.Text = text; else Dispatcher.BeginInvoke(() => _status.Text = text); }

    /// <summary>Shows another test (a test folder).</summary>
    public void ChooseTest(string folder)
    {
        var e = _all.FirstOrDefault(x => string.Equals(x.Folder, folder, StringComparison.OrdinalIgnoreCase));
        if (e != null) _tests.SelectedItem = e; else LoadTest(folder);
    }

    void ReloadTests()
    {
        var root = RetraceSettings.Load().Tests;
        Say("Loading the tests in " + root + " ...");
        Task.Run(() => TestList.Read(root)).ContinueWith(t => Guard("evidence tests", () =>
        {
            if (_closed) return;
            _all = t.IsCompletedSuccessfully ? t.Result : [];
            var keep = _folder;
            _loading = true;
            _tests.ItemsSource = _all;
            _loading = false;
            var pick = keep ?? _first;
            var e = (pick == null ? null : _all.FirstOrDefault(x => string.Equals(x.Folder, pick, StringComparison.OrdinalIgnoreCase))) ?? _all.FirstOrDefault();
            if (e == null) { Say($"No tests in {root} yet. Record one with Record / Stop."); return; }
            _loading = true; _tests.SelectedItem = e; _loading = false;
            if (keep == null || !string.Equals(keep, e.Folder, StringComparison.OrdinalIgnoreCase)) LoadTest(e.Folder);
            else Say($"{_all.Count} test(s).");
        }), TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>What the tester typed for the test shown, kept for when they come back to it.</summary>
    void SaveTyped()
    {
        if (_folder == null || _rows.Count == 0) return;
        _saved[_folder] = _rows.Where(r => r.Result != "Not run" || r.Notes.Length > 0).ToDictionary(r => r.Label, r => new ManualRow(r.Result, r.Notes));
    }

    void LoadTest(string folder)
    {
        SaveTyped();
        var id = ++_loadId;
        _folder = folder; _doc = null; _rows = []; _list.ItemsSource = null; _warn.Visibility = Visibility.Collapsed;
        Say("Reading the steps of " + Path.GetFileName(folder) + " ...");
        Task.Run(() =>
        {
            var warnings = new List<string>();
            var steps = Exporter.ResolveSteps(folder, warnings, out var err) ?? throw new IOException(err ?? "no steps");
            var text = File.ReadAllText(steps);
            return (Doc: StepsDoc.Load(steps), Text: text, Unchanged: EvidenceFiles.StepsUnchanged(folder, text));
        }).ContinueWith(t => Guard("evidence steps", () =>
        {
            if (id != _loadId || _closed) return;
            if (!t.IsCompletedSuccessfully) { Say("The steps could not be read: " + (t.Exception?.GetBaseException().Message ?? "?")); return; }
            _doc = t.Result.Doc; _stepsText = t.Result.Text;
            var counts = EvidenceFiles.List(folder);
            _saved.TryGetValue(folder, out var typed);
            _rows = _doc.Rows.Select(r =>
            {
                var vm = new RowVm { Label = r.Label, Action = r.Action, Pictures = counts.TryGetValue(r.Label, out var l) ? l.Count : 0 };
                if (typed != null && typed.TryGetValue(r.Label, out var said)) { vm.Result = said.Result; vm.Notes = said.Notes; }
                return vm;
            }).ToList();
            _list.ItemsSource = _rows;
            if (_rows.Count > 0) _list.SelectedIndex = 0;
            UpdateCount();
            if (!t.Result.Unchanged) { _warn.Text = "The steps of this test have changed since its pictures were taken (it was recorded again, or steps.md was edited): a picture belongs to a step by its number, so some may sit on the wrong step. Check them before writing the workbook."; _warn.Visibility = Visibility.Visible; }
            Say($"{_rows.Count} step(s). Do the step in AutoCAD, then press the camera button or {HotkeyText}.");
        }), TaskScheduler.FromCurrentSynchronizationContext());
    }

    void Move(int delta)
    {
        if (_rows.Count == 0) return;
        _list.SelectedIndex = Math.Clamp(Math.Max(_list.SelectedIndex, 0) + delta, 0, _rows.Count - 1);
        _list.ScrollIntoView(_list.SelectedItem);
    }

    void Refresh()
    {
        var i = _list.SelectedIndex;
        _list.ItemsSource = null; _list.ItemsSource = _rows;
        _list.SelectedIndex = i;
        UpdateCount();
    }

    void ShowCurrent()
    {
        var r = Current;
        _result.SelectedItem = r?.Result; _notes.Text = r?.Notes ?? "";
        List<string?>? names = null;
        if (r != null && _folder != null)
        {
            try { names = EvidenceFiles.List(_folder).GetValueOrDefault(r.Label)?.Select(Path.GetFileName).ToList(); }
            catch (Exception ex) { Log.Error("evidence list", ex); Say("The pictures could not be listed: " + ex.Message); }
        }
        _pics.ItemsSource = names;
    }

    void UpdateCount()
    {
        int pics = _rows.Sum(r => r.Pictures), steps = _rows.Count(r => r.Pictures > 0);
        _count.Text = $"{pics} picture(s) taken, in {steps} of {_rows.Count} step(s)";
    }

    // ---- the camera ------------------------------------------------------------------------------------------------------

    /// <summary>Camera (button or hotkey): every window of Retrace's own goes away (this one, Open test ...), AutoCAD's window to the front, a moment for the screen to repaint, the picture, the windows back (not activated: AutoCAD
    /// keeps the keyboard focus, so a tester in the middle of a command goes on typing into it).</summary>
    void Capture()
    {
        if (_closed) return;
        if (_busy) return;
        if (Current == null || _folder == null) { Say("Choose a test and a step first."); return; }
        _busy = true;
        try
        {
            _hidden = PictureWindows.Hide();
            Native.Front(AcApp.MainWindow.Handle);
            _wait.Start();
        }
        catch (Exception ex) { Log.Error("evidence camera", ex); _hidden?.Restore(); _hidden = null; _busy = false; }
    }

    void Shoot()
    {
        var row = Current; var folder = _folder; var retakeOf = _retakeOf; _retakeOf = null;
        try
        {
            var p = _closed ? null : EvidenceShot.Take(AcApp.MainWindow.Handle);
            _hidden?.Restore(); _hidden = null;
            if (_closed) return;
            if (p == null || row == null || folder == null) { Say("AutoCAD's window could not be captured (minimized or hidden?)."); return; }
            var path = EvidenceFiles.NewPath(folder, row.Label, DateTime.Now);
            EvidenceShot.WritePng(path, p.Value.Pixels, p.Value.W, p.Value.H);   // a few tens of ms for one picture: written here so the list is right at once
            row.Pictures++;
            EvidenceFiles.StampSteps(folder, _stepsText);
            if (retakeOf != null && File.Exists(retakeOf)) { EvidenceFiles.Remove(retakeOf); row.Pictures = Math.Max(0, row.Pictures - 1); }   // the new one is on disk: the old one goes
            Refresh();
            Say($"Step {row.Label}: picture filed as {Path.GetFileName(path)}");
        }
        catch (Exception ex) { Log.Error("evidence shot", ex); Say("The picture could not be filed: " + ex.Message); }
        finally { _hidden?.Restore(); _hidden = null; _busy = false; }
    }

    string? LastPicture()
    {
        var row = Current;
        if (row == null || _folder == null) return null;
        return EvidenceFiles.List(_folder).GetValueOrDefault(row.Label)?.LastOrDefault();
    }

    void RemovePicked()
    {
        var row = Current;
        if (row == null || _folder == null || _pics.SelectedItem is not string name) { Say("Pick a picture in the list first."); return; }
        var file = EvidenceFiles.List(_folder).GetValueOrDefault(row.Label)?.FirstOrDefault(f => Path.GetFileName(f) == name);
        if (file == null) return;
        EvidenceFiles.Remove(file);
        row.Pictures = Math.Max(0, row.Pictures - 1);
        Refresh();
        Say($"Removed {Path.GetFileName(file)} (moved to evidence\\removed).");
    }

    // ---- the workbook ------------------------------------------------------------------------------------------------------

    void WriteWorkbook()
    {
        if (_doc == null || _folder == null) return;
        var doc = _doc; var folder = _folder; var tester = _tester.Text.Trim(); var date = _date.Text.Trim();
        var said = _rows.Where(r => r.Result != "Not run" || r.Notes.Length > 0).ToDictionary(r => r.Label, r => new ManualRow(r.Result, r.Notes));
        _write.IsEnabled = false;
        Say("Writing the workbook ...");
        Task.Run(() =>
        {
            var ev = EvidenceBook.FromTester(doc, folder, said, tester, date);
            return EvidenceBook.Write(doc, folder, ev, DateTime.Now);
        }).ContinueWith(t => Guard("evidence workbook", () =>
        {
            _write.IsEnabled = true;
            if (_closed) return;
            if (!t.IsCompletedSuccessfully) { var m = t.Exception?.GetBaseException().Message ?? "?"; Log.Error("evidence workbook", t.Exception!); Say("The workbook could not be written: " + m); return; }
            Say("Written: " + t.Result + "  (steps.xlsx was not touched)");
            Commands.Print("Evidence workbook: " + t.Result);
        }), TaskScheduler.FromCurrentSynchronizationContext());
    }

    static void OpenPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
