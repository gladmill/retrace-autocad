using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Label = System.Windows.Controls.Label;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using TreeView = System.Windows.Controls.TreeView;
using TabControl = System.Windows.Controls.TabControl;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Retrace.Samples
{
    /// <summary>
    /// The main window of DRAWWPFTABS, a test for what a WPF dialog holds besides text boxes and buttons: static texts (a <see cref="Label"/>, TextBlocks, one that follows the tree), a
    /// <see cref="TabControl"/> (General: Name; Size: Width, Height; Style: Mark the center) and a <see cref="TreeView"/> (Parts, Tools) whose selection OK draws, next to a plain Qty field and
    /// <b>Details...</b>, which opens the WPF sub window of DRAWWPF. OK draws from every tab, the tree and the details, so a replay that missed one draws other texts. Built in code, no XAML.
    /// </summary>
    public class WpfTabsWindow : Window
    {
        private readonly TextBox nameBox = new() { Name = "NameBox", Width = 160 };
        private readonly TextBox widthBox = new() { Name = "WidthBox", Width = 60, Text = "10" };
        private readonly TextBox heightBox = new() { Name = "HeightBox", Width = 60, Text = "5" };
        private readonly CheckBox markBox = new() { Name = "MarkBox", Content = "Mark the center" };
        private readonly TextBox qtyBox = new() { Name = "QtyBox", Width = 60, Text = "1" };
        private readonly TreeView tree = new() { Name = "PartsTree", Height = 110, Width = 200 };
        private readonly TextBlock selectedText = new() { Name = "SelectedText", Text = "Selected: nothing" };
        private readonly TextBlock detailsText = new() { Name = "DetailsText" };

        public string PlateName => nameBox.Text.Trim();
        public string Size => widthBox.Text.Trim() + "x" + heightBox.Text.Trim();
        public bool Mark => markBox.IsChecked == true;
        public string Qty => qtyBox.Text.Trim();
        public string Part { get; private set; } = "none";
        public string Note { get; private set; } = "";
        public int Depth { get; private set; }

        public WpfTabsWindow()
        {
            Title = "Wpf tabs";
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            AutomationProperties.SetName(nameBox, "Name");
            AutomationProperties.SetName(widthBox, "Width");
            AutomationProperties.SetName(heightBox, "Height");
            AutomationProperties.SetName(qtyBox, "Qty");

            var parts = new TreeViewItem { Header = "Parts", IsExpanded = true };
            var fasteners = new TreeViewItem { Header = "Fasteners" };
            var bolts = new TreeViewItem { Header = "Bolts" };
            bolts.Items.Add(new TreeViewItem { Header = "M6" });
            bolts.Items.Add(new TreeViewItem { Header = "M8" });
            fasteners.Items.Add(bolts);
            fasteners.Items.Add(new TreeViewItem { Header = "Nuts" });
            parts.Items.Add(fasteners);
            parts.Items.Add(new TreeViewItem { Header = "Frames" });
            var tools = new TreeViewItem { Header = "Tools" };
            tools.Items.Add(new TreeViewItem { Header = "Hammer" });
            tools.Items.Add(new TreeViewItem { Header = "Wrench" });
            tree.Items.Add(parts);
            tree.Items.Add(tools);
            tree.SelectedItemChanged += (_, _) =>
            {
                Part = tree.SelectedItem is TreeViewItem t ? Path(t) : "none";
                selectedText.Text = "Selected: " + Part;
            };

            var tabs = new TabControl { Name = "MainTabs", Height = 90, Margin = new Thickness(0, 8, 0, 8) };
            tabs.Items.Add(new TabItem { Header = "General", Content = Row(new Label { Content = "Name:", Target = nameBox }, nameBox) });
            var size = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8) };
            size.Children.Add(new Label { Content = "Width:", Target = widthBox });
            size.Children.Add(widthBox);
            size.Children.Add(new Label { Content = "Height:", Target = heightBox });
            size.Children.Add(heightBox);
            tabs.Items.Add(new TabItem { Header = "Size", Content = size });
            tabs.Items.Add(new TabItem { Header = "Style", Content = Row(markBox) });

            var details = new Button { Name = "DetailsButton", Content = "_Details...", Width = 90 };
            var ok = new Button { Name = "OkButton", Content = "OK", Width = 75, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Name = "CancelButton", Content = "Cancel", Width = 75, IsCancel = true };
            var qtyRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            qtyRow.Children.Add(new Label { Content = "Qty:", Target = qtyBox });
            qtyRow.Children.Add(qtyBox);
            var detailsRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 8) };
            detailsRow.Children.Add(details);
            detailsRow.Children.Add(new TextBlock { Text = "  " });
            detailsRow.Children.Add(detailsText);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            var root = new StackPanel { Margin = new Thickness(16) };
            root.Children.Add(new Label { Content = "Plate designer", FontWeight = FontWeights.Bold });
            root.Children.Add(new TextBlock { Text = "Pick the plate's data, then a part from the tree." });
            root.Children.Add(tabs);
            root.Children.Add(qtyRow);
            root.Children.Add(new TextBlock { Text = "Nuts & Bolts catalog" });   // "&" is literal in WPF: a static text keeps it
            root.Children.Add(tree);
            root.Children.Add(selectedText);
            root.Children.Add(detailsRow);
            root.Children.Add(buttons);
            Content = root;

            details.Click += (_, _) => ChooseDetails();
            ok.Click += (_, _) => { DialogResult = true; };
            ShowState();
        }

        private static StackPanel Row(params UIElement[] items)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8) };
            foreach (var i in items) p.Children.Add(i);
            return p;
        }

        /// <summary>The header texts from the root of the tree down to <paramref name="t"/>, joined with " > ".</summary>
        private static string Path(TreeViewItem t)
        {
            var names = new List<string>();
            for (ItemsControl? c = t; c is TreeViewItem item; c = ItemsControl.ItemsControlFromItemContainer(item))
                names.Insert(0, item.Header?.ToString() ?? "");
            return string.Join(" > ", names);
        }

        private void ChooseDetails()
        {
            var sub = new WpfDetailsWindow(Note, Depth);
            if (Application.ShowModalWindow(this, sub, false) != true) return;
            Note = sub.Note;
            Depth = sub.Depth;
            ShowState();
        }

        private void ShowState() => detailsText.Text = "Note " + (Note.Length == 0 ? "none" : Note) + ", depth " + Depth;
    }
}
