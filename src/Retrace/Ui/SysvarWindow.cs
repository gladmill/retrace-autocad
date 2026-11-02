using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using Retrace.Core;

namespace Retrace.Ui;

/// <summary>
/// "Edit system variables...": the system variables the System variables behavior reads (default list plus the user's additions, minus
/// the removals). Works on copies; <see cref="Add"/> / <see cref="Remove"/> hold the result when the dialog returns true (OK).
/// The list logic is <see cref="SysvarListEdit"/>. Code-only WPF, never touches AutoCAD.
/// </summary>
sealed class SysvarWindow : Window
{
    readonly ListBox _list = new() { Height = 300, Margin = new Thickness(0, 6, 0, 0) };
    readonly TextBox _name = new() { Padding = new Thickness(3), MinWidth = 120, CharacterCasing = CharacterCasing.Upper };
    readonly TextBlock _msg = new() { Foreground = Brushes.Firebrick, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _summary = new() { Foreground = Brushes.DimGray };

    public List<string> Add { get; private set; }
    public List<string> Remove { get; private set; }

    public SysvarWindow(IEnumerable<string> add, IEnumerable<string> remove)
    {
        Add = new List<string>(add); Remove = new List<string>(remove);
        Title = "Retrace: system variables";
        Width = 480; MinWidth = 480; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "The system variables Retrace reads for the System variables behavior. Names AutoCAD does not know are skipped when recording.",
        });
        root.Children.Add(_summary);
        root.Children.Add(_list);

        // the name box takes what the buttons leave, so nothing clips at any scaling
        var row = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var add1 = Btn("Add"); add1.Click += (_, _) => AddName();
        var rem = Btn("Remove selected"); rem.Click += (_, _) => RemoveSelected();
        Grid.SetColumn(_name, 0); Grid.SetColumn(add1, 1); Grid.SetColumn(rem, 2);
        row.Children.Add(_name); row.Children.Add(add1); row.Children.Add(rem);
        root.Children.Add(row);
        root.Children.Add(_msg);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var reset = Btn("Default list"); reset.Click += (_, _) => { SysvarListEdit.Reset(Add, Remove); Fill(); };
        var ok = Btn("OK"); ok.IsDefault = true; ok.Click += (_, _) => { DialogResult = true; };
        var cancel = Btn("Cancel"); cancel.IsCancel = true;
        buttons.Children.Add(reset); buttons.Children.Add(ok); buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;

        _name.KeyDown += (_, e) => { if (e.Key == Key.Enter) { AddName(); e.Handled = true; } };
        _list.KeyDown += (_, e) => { if (e.Key == Key.Delete) { RemoveSelected(); e.Handled = true; } };
        Fill();
        Loaded += (_, _) => _name.Focus();
    }

    static Button Btn(string text) => new() { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(4, 0, 0, 0), MinWidth = 64 };

    void Fill()
    {
        _list.Items.Clear();
        foreach (var n in SysVarDefaults.Effective(Add, Remove)) _list.Items.Add(n);
        _summary.Text = SysvarListEdit.Summary(Add, Remove);
    }

    void AddName()
    {
        if (SysvarListEdit.Add(Add, Remove, _name.Text, out var message))
        {
            var name = _name.Text.Trim().ToUpperInvariant();
            _name.Clear(); _msg.Text = "";
            Fill();
            _list.SelectedItem = name; _list.ScrollIntoView(name);
        }
        else _msg.Text = message;
    }

    void RemoveSelected()
    {
        if (_list.SelectedItem is string n && SysvarListEdit.Remove(Add, Remove, n))
        {
            var i = _list.SelectedIndex;
            _msg.Text = "";
            Fill();
            if (_list.Items.Count > 0) _list.SelectedIndex = Math.Min(i, _list.Items.Count - 1);
        }
    }
}
