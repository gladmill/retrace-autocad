using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using Label = System.Windows.Controls.Label;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace Retrace.Samples
{
    /// <summary>
    /// The main window of DRAWWPF, a test for the buttons of a WPF dialog and the dialogs they open, to any depth, in any mix of WPF and WinForms: <b>Add</b> appends Name x Qty to the list and clears Name,
    /// <b>Details...</b> opens a WPF sub window (<see cref="WpfDetailsWindow"/>), which has <b>More...</b>, which opens a WinForms sub-sub dialog (<see cref="WpfDepthDialog"/>). OK (the default button) draws one
    /// TEXT per item and one for the details, so a replay that skips a click draws something else and a checkpoint fails. Built in code, no XAML.
    /// </summary>
    public class WpfItemsWindow : Window
    {
        private readonly TextBox nameBox = new() { Name = "NameBox", Width = 160 };
        private readonly TextBox qtyBox = new() { Name = "QtyBox", Width = 60, Text = "1" };
        private readonly TextBox notesBox = new() { Name = "NotesBox", Width = 330, Height = 48, AcceptsReturn = true, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Visible, Text = "line 1\nline 2\nline 3\nline 4\nline 5\nline 6\nline 7\nline 8\nline 9\nline 10" };
        private readonly TextBlock listText = new() { Name = "ListText" };
        private readonly TextBlock countText = new() { Name = "CountText" };
        private readonly TextBlock detailsText = new() { Name = "DetailsText" };
        private readonly List<string> items = new();

        public IReadOnlyList<string> Items => items;
        public string Note { get; private set; } = "";
        public int Depth { get; private set; }

        public WpfItemsWindow()
        {
            Title = "Wpf items";
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            AutomationProperties.SetName(nameBox, "Name");
            AutomationProperties.SetName(qtyBox, "Qty");
            AutomationProperties.SetName(notesBox, "Notes");

            var add = new Button { Name = "AddButton", Content = "_Add", Width = 75, Margin = new Thickness(8, 0, 0, 0) };
            var details = new Button { Name = "DetailsButton", Content = "_Details...", Width = 90 };
            var ok = new Button { Name = "OkButton", Content = "OK", Width = 75, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Name = "CancelButton", Content = "Cancel", Width = 75, IsCancel = true };

            var row1 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            row1.Children.Add(new Label { Content = "Name:", Target = nameBox });
            row1.Children.Add(nameBox);
            row1.Children.Add(new Label { Content = "Qty:", Target = qtyBox });
            row1.Children.Add(qtyBox);
            row1.Children.Add(add);
            var row2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 8) };
            row2.Children.Add(details);
            row2.Children.Add(new TextBlock { Text = "  ", Width = 8 });
            row2.Children.Add(detailsText);
            var row3 = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            row3.Children.Add(ok);
            row3.Children.Add(cancel);
            var root = new StackPanel { Margin = new Thickness(16) };
            root.Children.Add(row1);
            root.Children.Add(notesBox);   // multi-line with a scroll bar: a click on its arrows is no button click
            root.Children.Add(listText);
            root.Children.Add(countText);
            root.Children.Add(row2);
            root.Children.Add(row3);
            Content = root;

            add.Click += (_, _) => AddItem();
            details.Click += (_, _) => ChooseDetails();
            ok.Click += (_, _) => { DialogResult = true; };
            ShowState();
        }

        /// <summary>Adds Name x Qty to the list and clears Name. An empty Name adds nothing.</summary>
        private void AddItem()
        {
            var name = nameBox.Text.Trim();
            if (name.Length == 0) return;
            items.Add(name + " x" + qtyBox.Text.Trim());
            nameBox.Clear();
            nameBox.Focus();
            ShowState();
        }

        /// <summary>Opens the WPF sub window; OK there keeps its note and the depth its own More... dialog chose.</summary>
        private void ChooseDetails()
        {
            var sub = new WpfDetailsWindow(Note, Depth);
            if (Application.ShowModalWindow(this, sub, false) != true) return;
            Note = sub.Note;
            Depth = sub.Depth;
            ShowState();
        }

        private void ShowState()
        {
            listText.Text = items.Count == 0 ? "(no items)" : string.Join(", ", items);
            countText.Text = items.Count == 1 ? "1 item" : items.Count + " items";
            detailsText.Text = "Note " + (Note.Length == 0 ? "none" : Note) + ", depth " + Depth;
        }
    }

    /// <summary>The WPF sub window of DRAWWPF and DRAWMIX: a note, and <b>More...</b>, which opens a WinForms sub-sub dialog for a depth.</summary>
    public class WpfDetailsWindow : Window
    {
        private readonly TextBox noteBox = new() { Name = "NoteBox", Width = 200 };
        private readonly TextBlock depthText = new() { Name = "DepthText" };

        public string Note => noteBox.Text.Trim();
        public int Depth { get; private set; }

        public WpfDetailsWindow(string note, int depth)
        {
            Title = "Wpf details";
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            noteBox.Text = note;
            Depth = depth;
            AutomationProperties.SetName(noteBox, "Note");

            var more = new Button { Name = "MoreButton", Content = "More...", Width = 90 };
            var ok = new Button { Name = "DetailsOkButton", Content = "OK", Width = 75, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Name = "DetailsCancelButton", Content = "Cancel", Width = 75, IsCancel = true };
            var row1 = new StackPanel { Orientation = Orientation.Horizontal };
            row1.Children.Add(new Label { Content = "Note:", Target = noteBox });
            row1.Children.Add(noteBox);
            var row2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            row2.Children.Add(more);
            row2.Children.Add(new TextBlock { Text = "  " });
            row2.Children.Add(depthText);
            var row3 = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            row3.Children.Add(ok);
            row3.Children.Add(cancel);
            var root = new StackPanel { Margin = new Thickness(16) };
            root.Children.Add(row1);
            root.Children.Add(row2);
            root.Children.Add(row3);
            Content = root;

            more.Click += (_, _) => ChooseDepth();
            ok.Click += (_, _) => { DialogResult = true; };
            ShowDepth();
        }

        private void ChooseDepth()
        {
            using var dialog = new WpfDepthDialog(Depth);
            var owner = new WindowInteropHelper(this).Handle;
            if (Application.ShowModalDialog(new Win32Owner(owner), dialog, false) != System.Windows.Forms.DialogResult.OK) return;
            Depth = dialog.Depth;
            ShowDepth();
        }

        private void ShowDepth() => depthText.Text = "Depth " + Depth;
    }

    /// <summary>A window handle as the owner of a WinForms dialog.</summary>
    internal sealed class Win32Owner : System.Windows.Forms.IWin32Window
    {
        public Win32Owner(IntPtr handle) => Handle = handle;
        public IntPtr Handle { get; }
    }

    /// <summary>The WinForms sub-sub dialog of DRAWWPF: a depth.</summary>
    public class WpfDepthDialog : System.Windows.Forms.Form
    {
        private readonly System.Windows.Forms.NumericUpDown depthBox = new() { Name = "depthBox", Minimum = 0, Maximum = 99 };
        private readonly System.Windows.Forms.Button okButton = new() { Name = "depthOkButton", Text = "OK" };
        private readonly System.Windows.Forms.Button cancelButton = new() { Name = "depthCancelButton", Text = "Cancel", DialogResult = System.Windows.Forms.DialogResult.Cancel };

        public int Depth => (int)depthBox.Value;

        public WpfDepthDialog(int depth)
        {
            Text = "Wpf depth";
            FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            ClientSize = new System.Drawing.Size(250, 100);
            AcceptButton = okButton;
            CancelButton = cancelButton;
            var label = new System.Windows.Forms.Label { Text = "Depth:", AutoSize = true, Location = new System.Drawing.Point(16, 19) };
            depthBox.Location = new System.Drawing.Point(90, 16); depthBox.Size = new System.Drawing.Size(120, 23);
            depthBox.Value = Math.Clamp(depth, 0, 99);
            okButton.Location = new System.Drawing.Point(90, 60); okButton.Size = new System.Drawing.Size(75, 25);
            cancelButton.Location = new System.Drawing.Point(170, 60); cancelButton.Size = new System.Drawing.Size(75, 25);
            Controls.AddRange(new System.Windows.Forms.Control[] { label, depthBox, okButton, cancelButton });
            okButton.Click += (_, _) => { DialogResult = System.Windows.Forms.DialogResult.OK; Close(); };
        }
    }

    /// <summary>The WinForms main dialog of DRAWMIX: a title and <b>Details...</b>, which opens the WPF sub window (<see cref="WpfDetailsWindow"/>), which opens the WinForms depth dialog.</summary>
    public class WpfMixDialog : System.Windows.Forms.Form
    {
        private readonly System.Windows.Forms.TextBox titleBox = new() { Name = "titleBox", AccessibleName = "Title" };
        private readonly System.Windows.Forms.Button detailsButton = new() { Name = "detailsButton", Text = "&Details..." };
        private readonly System.Windows.Forms.Label stateLabel = new() { Name = "stateLabel", AutoSize = true };
        private readonly System.Windows.Forms.Button okButton = new() { Name = "mixOkButton", Text = "OK" };
        private readonly System.Windows.Forms.Button cancelButton = new() { Name = "mixCancelButton", Text = "Cancel", DialogResult = System.Windows.Forms.DialogResult.Cancel };

        public string PlateTitle => titleBox.Text.Trim();
        public string Note { get; private set; } = "";
        public int Depth { get; private set; }

        public WpfMixDialog()
        {
            Text = "Mixed dialogs";
            FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            ClientSize = new System.Drawing.Size(300, 140);
            CancelButton = cancelButton;
            var label = new System.Windows.Forms.Label { Text = "Title:", AutoSize = true, Location = new System.Drawing.Point(16, 19) };
            titleBox.Location = new System.Drawing.Point(80, 16); titleBox.Size = new System.Drawing.Size(190, 23);
            detailsButton.Location = new System.Drawing.Point(16, 52); detailsButton.Size = new System.Drawing.Size(90, 25);
            stateLabel.Location = new System.Drawing.Point(116, 57);
            okButton.Location = new System.Drawing.Point(120, 100); okButton.Size = new System.Drawing.Size(75, 25);
            cancelButton.Location = new System.Drawing.Point(200, 100); cancelButton.Size = new System.Drawing.Size(75, 25);
            Controls.AddRange(new System.Windows.Forms.Control[] { label, titleBox, detailsButton, stateLabel, okButton, cancelButton });
            detailsButton.Click += (_, _) => ChooseDetails();
            okButton.Click += (_, _) => { DialogResult = System.Windows.Forms.DialogResult.OK; Close(); };
            ShowState();
        }

        private void ChooseDetails()
        {
            var sub = new WpfDetailsWindow(Note, Depth);
            if (Application.ShowModalWindow(Handle, sub, false) != true) return;
            Note = sub.Note;
            Depth = sub.Depth;
            ShowState();
        }

        private void ShowState() => stateLabel.Text = "Note " + (Note.Length == 0 ? "none" : Note) + ", depth " + Depth;
    }
}
