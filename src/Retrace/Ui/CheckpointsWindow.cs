using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using Retrace.AutoCad;
using Retrace.Engine.Recordings;

namespace Retrace.Ui;

/// <summary>
/// RETRACECHECKPOINTS (and after RETRACESTOP): a modeless, code-only WPF window that shows the checkpoints of one recording as cards
/// (its picture, where it was taken, what the drawing held) with an editable name. Save renames them in recording.jsonl and writes the
/// steps again (in the background, through <see cref="Commands"/>). Reading the recording and decoding the pictures run on a thread-pool
/// thread; the window never touches the AutoCAD API.
/// </summary>
sealed class CheckpointsWindow : Window
{
    static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x5F, 0x63, 0x68));
    static readonly Brush CardBack = new SolidColorBrush(Color.FromRgb(0xF6, 0xF7, 0xF9));
    static readonly Brush CardLine = new SolidColorBrush(Color.FromRgb(0xDA, 0xDD, 0xE1));
    static readonly Brush Changed = new SolidColorBrush(Color.FromRgb(0x1A, 0x73, 0xE8));

    readonly Commands _core;
    readonly StackPanel _cards = new();
    readonly TextBlock _heading = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, Text = "Name your checkpoints" };
    readonly TextBlock _sub = new() { Foreground = Muted, Margin = new Thickness(0, 2, 0, 0) };
    readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center };
    readonly Button _save = new() { Content = "Save names", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(8, 0, 0, 0), FontWeight = FontWeights.SemiBold, IsEnabled = false };
    readonly Button _steps = new() { Content = "Open steps", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(8, 0, 0, 0) };
    readonly Button _close = new() { Content = "Close", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(8, 0, 0, 0) };
    readonly List<Card> _list = new();
    bool _busy, _closing;

    /// <summary>The recording.jsonl this window shows.</summary>
    public string Recording { get; }

    /// <summary>True when the window was opened by RETRACESTOP: it closes again by itself if the recording turns out to have no checkpoints.</summary>
    readonly bool _afterStop;

    sealed class Card
    {
        public required CheckpointInfo Info;
        public required TextBox Box;
        public required Button Reset;
        public string Saved = "";   // the name on disk
        public string Wanted => Checkpoints.Clean(Box.Text);
        public bool Dirty => Wanted.Length > 0 ? Wanted != Saved : Saved != Info.AutoName;   // empty = back to the automatic name
    }

    public CheckpointsWindow(Commands core, string recording, bool afterStop)
    {
        _core = core;
        Recording = recording;
        _afterStop = afterStop;
        Title = "Retrace: checkpoints";
        Width = 900; Height = 680; MinWidth = 640; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Background = Brushes.White;

        var root = new DockPanel { Margin = new Thickness(18, 14, 18, 14) };

        var top = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        top.Children.Add(_heading);
        top.Children.Add(_sub);
        top.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Muted, Margin = new Thickness(0, 8, 0, 0),
            Text = "Retrace named each checkpoint after the last thing you did before it. Give them names that say what you checked. " +
                   "A name only changes how the check reads in the steps and results; what is compared when the test runs stays the same.",
        });
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var bottom = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_steps); buttons.Children.Add(_save); buttons.Children.Add(_close);
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);
        bottom.Children.Add(_status);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        root.Children.Add(new ScrollViewer { Content = _cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 4, 0) });
        Content = root;

        _save.Click += (_, _) => Save(closeAfter: false);
        _steps.Click += (_, _) => _core.CheckpointsOpenSteps(Recording, Say);
        _close.Click += (_, _) => Close();
        _save.ToolTip = "Rename the checkpoints and write steps.md (and the Word / Excel files) again. Ctrl+S";
        _steps.ToolTip = "Read the steps with the current names.";
        PreviewKeyDown += OnKey;
        Closing += OnClosing;
        Loaded += (_, _) => Load();
    }

    public Dispatcher UiDispatcher => Dispatcher;

    /// <summary>Closes without the "save?" question (plugin shutdown).</summary>
    public void CloseWithoutAsking() { _closing = true; Close(); }

    public void Say(string text)
    {
        if (Dispatcher.CheckAccess()) _status.Text = text; else Dispatcher.BeginInvoke(() => _status.Text = text);
    }

    // ---- loading ------------------------------------------------------------------------------------------------------

    sealed record Loaded_(List<CheckpointInfo> Items, Dictionary<long, BitmapSource> Pictures, string Test, string? Error);

    public void Load()
    {
        Say("Reading the checkpoints...");
        var rec = Recording;
        Task.Run(() =>
        {
            try
            {
                var items = Checkpoints.Read(rec, out var test);
                var pics = new Dictionary<long, BitmapSource>();
                foreach (var c in items) if (c.Picture != null && Thumb(c.Picture) is { } b) pics[c.N] = b;
                return new Loaded_(items, pics, test, null);
            }
            catch (Exception ex) { Log.Error("checkpoints window: read " + rec, ex); return new Loaded_(new(), new(), TestFolders.NameOf(rec), ex.Message); }
        }).ContinueWith(t => Show(t.Result), TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Decodes a picture into memory (the file is not kept open: the steps writer may rename it), at most 520 px wide.</summary>
    static BitmapSource? Thumb(string file)
    {
        try
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            b.DecodePixelWidth = 520;
            b.UriSource = new Uri(file);
            b.EndInit();
            b.Freeze();
            return b;
        }
        catch (Exception ex) { Log.Error("checkpoints window: picture " + file, ex); return null; }
    }

    void Show(Loaded_ r)
    {
        _cards.Children.Clear();
        _list.Clear();
        _sub.Text = $"{r.Test} · {r.Items.Count} checkpoint{(r.Items.Count == 1 ? "" : "s")}";
        if (r.Error != null) { Say("Could not read the recording: " + r.Error); return; }
        if (r.Items.Count == 0)
        {
            if (_afterStop) { _closing = true; Close(); return; }
            _cards.Children.Add(new TextBlock { Text = "This test has no checkpoints. Click Checkpoint while recording, at each result worth checking.", Foreground = Muted, TextWrapping = TextWrapping.Wrap });
            _save.IsEnabled = false;
            Say("");
            return;
        }
        foreach (var c in r.Items) _cards.Children.Add(CardFor(c, r.Pictures.GetValueOrDefault(c.N)));
        Say("Click a name to change it. Enter goes to the next one; an empty name means the automatic one.");
        UpdateState();
        if (_list.Count > 0) { _list[0].Box.Focus(); _list[0].Box.SelectAll(); }
    }

    UIElement CardFor(CheckpointInfo c, BitmapSource? pic)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // the picture: the screen at the checkpoint; click for the full size
        FrameworkElement picture;
        if (pic != null)
        {
            var img = new Image { Source = pic, Stretch = Stretch.Uniform, MaxHeight = 170, HorizontalAlignment = HorizontalAlignment.Left };
            var frame = new Border { Child = img, BorderBrush = CardLine, BorderThickness = new Thickness(1), Cursor = Cursors.Hand, ToolTip = "Open the full picture", HorizontalAlignment = HorizontalAlignment.Left };
            var file = c.Picture!;
            frame.MouseLeftButtonUp += (_, _) => _core.CheckpointsOpenPicture(file, Say);
            picture = frame;
        }
        else picture = new Border
        {
            Width = 240, Height = 135, BorderBrush = CardLine, BorderThickness = new Thickness(1), Background = Brushes.White,
            Child = new TextBlock { Text = "No picture", Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        Grid.SetColumn(picture, 0);
        grid.Children.Add(picture);

        var right = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };
        right.Children.Add(new TextBlock { Text = $"CHECKPOINT {c.Index} · STEP {c.Step}", Foreground = Muted, FontSize = 11, FontWeight = FontWeights.SemiBold });
        var box = new TextBox
        {
            Text = c.Name, FontSize = 16, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 4, 0, 0), MaxLength = Checkpoints.MaxName,
            ToolTip = $"The name of checkpoint {c.Index}. Leave it empty for the automatic name ({c.AutoName}).",
        };
        AutomationName(box, $"Name of checkpoint {c.Index}");
        right.Children.Add(box);

        var auto = new TextBlock { Foreground = Muted, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
        auto.Inlines.Add(new System.Windows.Documents.Run("Automatic name: "));
        auto.Inlines.Add(new System.Windows.Documents.Run(c.AutoName) { FontFamily = new FontFamily("Consolas") });
        right.Children.Add(auto);
        if (c.After != null) right.Children.Add(Line("Taken after: " + c.After));
        right.Children.Add(Line(c.State != null ? "Checks that " + c.State : "The drawing could not be read at this checkpoint, so it is not checked."));

        var reset = new Button { Content = "Use the automatic name", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        right.Children.Add(reset);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);

        var card = new Card { Info = c, Box = box, Reset = reset, Saved = c.Name };
        _list.Add(card);
        box.TextChanged += (_, _) => UpdateState();
        box.GotKeyboardFocus += (_, _) => box.SelectAll();
        box.LostKeyboardFocus += (_, _) => { if (Checkpoints.Clean(box.Text).Length == 0) box.Text = c.AutoName; };
        reset.Click += (_, _) => { box.Text = c.AutoName; box.Focus(); };

        return new Border
        {
            Child = grid, Background = CardBack, BorderBrush = CardLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 10),
        };
    }

    static TextBlock Line(string text) => new() { Text = text, Foreground = Muted, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };

    static void AutomationName(DependencyObject o, string name) => System.Windows.Automation.AutomationProperties.SetName(o, name);

    // ---- state, keys, save --------------------------------------------------------------------------------------------

    bool AnyDirty => _list.Any(c => c.Dirty);
    /// <summary>Unsaved names, or a save still running: reloading from disk now would drop them.</summary>
    internal bool HasPendingEdits => _busy || AnyDirty;

    void UpdateState()
    {
        foreach (var c in _list)
        {
            c.Box.BorderBrush = c.Dirty ? Changed : CardLine;
            c.Reset.Visibility = Checkpoints.Clean(c.Box.Text) == c.Info.AutoName ? Visibility.Collapsed : Visibility.Visible;
        }
        var n = _list.Count(c => c.Dirty);
        _save.IsEnabled = !_busy && n > 0;
        _save.Content = n > 0 ? $"Save names ({n})" : "Save names";
        Title = "Retrace: checkpoints" + (n > 0 ? " (not saved)" : "");
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { if (_save.IsEnabled) Save(closeAfter: false); e.Handled = true; return; }
        if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
        if (e.Key == Key.Enter && e.OriginalSource is TextBox tb)
        {
            var i = _list.FindIndex(c => ReferenceEquals(c.Box, tb));
            if (i >= 0 && i + 1 < _list.Count) { _list[i + 1].Box.Focus(); _list[i + 1].Box.BringIntoView(); }
            else if (_save.IsEnabled) Save(closeAfter: false);
            e.Handled = true;
        }
    }

    void Save(bool closeAfter)
    {
        if (_busy) return;
        var names = _list.Where(c => c.Dirty).ToDictionary(c => c.Info.N, c => c.Wanted);
        if (names.Count == 0) { if (closeAfter) { _closing = true; Close(); } return; }
        _busy = true;
        UpdateState();
        Say("Saving the names and writing the steps again...");
        var started = _core.CheckpointsSave(Recording, names, (ok, message) =>
        {
            _busy = false;
            if (ok)
            {
                // The names that were sent are saved; a box edited again while saving keeps its new text and stays unsaved.
                foreach (var c in _list.Where(c => names.ContainsKey(c.Info.N)))
                {
                    var sent = names[c.Info.N];
                    c.Saved = sent.Length > 0 ? sent : c.Info.AutoName;
                    if (c.Wanted == sent && c.Box.Text != c.Saved) c.Box.Text = c.Saved;
                }
                Say(message);
                UpdateState();
                if (closeAfter && !AnyDirty) { _closing = true; Close(); }
            }
            else { Say(message); UpdateState(); }
        });
        if (!started) { _busy = false; UpdateState(); }
    }

    void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closing || !AnyDirty) return;
        if (_busy) { e.Cancel = true; Say("Saving... the window closes when you click Close again."); return; }
        var a = MessageBox.Show(this, "Save the new checkpoint names?", "Retrace", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (a == MessageBoxResult.Cancel) { e.Cancel = true; return; }
        if (a == MessageBoxResult.Yes) { e.Cancel = true; Save(closeAfter: true); }
    }
}
