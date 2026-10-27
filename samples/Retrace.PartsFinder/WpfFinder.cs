using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Autodesk.AutoCAD.Windows;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;
// the project also uses WinForms (implicit usings): say which toolkit's types these are
using SystemColors = System.Windows.SystemColors;
using Label = System.Windows.Controls.Label;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using CheckBox = System.Windows.Controls.CheckBox;
using Button = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;
using Orientation = System.Windows.Controls.Orientation;
using Binding = System.Windows.Data.Binding;

namespace Retrace.PartsFinder
{
    /// <summary>
    /// The WPF Parts Finder: the same search as the WinForms one, written the way WPF palettes usually are, with a view model, bindings (the text
    /// boxes update their source only when they lose focus, WPF's default), an async-loaded category list, a list with a data template, and
    /// commands whose CanExecute enables the buttons. PARTSFINDERWPF opens it floating with auto roll-up.
    /// </summary>
    public static class WpfFinder
    {
        static PaletteSet? set;
        static FinderModel? model;

        public static Part? Selected => model?.SelectedPart;

        public static void Show()
        {
            if (set == null)
            {
                set = new PaletteSet("Parts Finder WPF", new Guid("3c9e8a51-7d2b-4f60-8a1e-9b4c2d6e0f73"));
                set.Style = PaletteSetStyles.ShowAutoHideButton | PaletteSetStyles.ShowCloseButton | PaletteSetStyles.ShowPropertiesMenu;
                set.MinimumSize = new System.Drawing.Size(280, 460);
                model = new FinderModel();
                set.AddVisual("Find", View(model));
            }
            set.Visible = true;
            set.Dock = DockSides.None;
            set.AutoRollUp = true;
        }

        static UIElement View(FinderModel m)
        {
            var grid = new Grid { Margin = new Thickness(8), Background = SystemColors.WindowBrush, DataContext = m };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            int row = 0;
            void Add(UIElement e, int col, int span = 1)
            {
                while (grid.RowDefinitions.Count <= row) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(e, row); Grid.SetColumn(e, col); Grid.SetColumnSpan(e, span);
                grid.Children.Add(e);
            }
            void Labeled(string label, FrameworkElement c) { Add(new Label { Content = label, Target = c }, 0); c.Margin = new Thickness(2); Add(c, 1); row++; }

            var search = new TextBox { Name = "SearchText" };
            search.SetBinding(TextBox.TextProperty, new Binding(nameof(FinderModel.SearchText)));   // updates the model on focus loss
            Labeled("Search", search);

            var category = new ComboBox { Name = "CategoryList" };
            category.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(FinderModel.Categories)));
            category.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, new Binding(nameof(FinderModel.Category)));
            Labeled("Category", category);

            var max = new TextBox { Name = "MaxPrice" };
            max.SetBinding(TextBox.TextProperty, new Binding(nameof(FinderModel.MaxPrice)) { StringFormat = "0.##", ConverterCulture = CultureInfo.InvariantCulture });
            Labeled("Max price", max);

            var stock = new CheckBox { Name = "InStockOnly", Content = "In stock only", Margin = new Thickness(2, 6, 2, 6) };
            stock.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(nameof(FinderModel.InStockOnly)));
            Add(stock, 1); row++;

            var searchButton = new Button { Name = "SearchButton", Content = "Search", Margin = new Thickness(2), Command = m.SearchCommand };
            var status = new TextBlock { Name = "Status", Margin = new Thickness(6, 4, 2, 2) };
            status.SetBinding(TextBlock.TextProperty, new Binding(nameof(FinderModel.Status)));
            Add(searchButton, 0); Add(status, 1); row++;

            var results = new ListBox { Name = "ResultsList", Height = 170, Margin = new Thickness(2) };
            System.Windows.Automation.AutomationProperties.SetName(results, "Results");
            results.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(FinderModel.Results)));
            results.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, new Binding(nameof(FinderModel.SelectedPart)));
            var item = new FrameworkElementFactory(typeof(StackPanel));
            item.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            var title = new FrameworkElementFactory(typeof(TextBlock));
            title.SetBinding(TextBlock.TextProperty, new Binding(nameof(Part.Title)));
            var price = new FrameworkElementFactory(typeof(TextBlock));
            price.SetBinding(TextBlock.TextProperty, new Binding(nameof(Part.Price)) { StringFormat = "  {0:0.00}", ConverterCulture = CultureInfo.InvariantCulture });
            item.AppendChild(title); item.AppendChild(price);
            results.ItemTemplate = new DataTemplate { VisualTree = item };
            Add(results, 0, 2); row++;

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(new Button { Name = "InsertButton", Content = "Insert", Margin = new Thickness(2), MinWidth = 70, Command = m.InsertCommand });
            buttons.Children.Add(new Button { Name = "PlaceButton", Content = "Place...", Margin = new Thickness(2), MinWidth = 70, Command = m.PlaceCommand });
            Add(buttons, 0, 2);
            return grid;
        }
    }

    /// <summary>The view model: filters, results, selection, and the commands behind the buttons.</summary>
    public sealed class FinderModel : INotifyPropertyChanged
    {
        const string All = "(all categories)";
        string searchText = "", status = "Loading categories...";
        string? category = All;
        double maxPrice = 1000;
        bool inStockOnly;
        Part? selected;

        public event PropertyChangedEventHandler? PropertyChanged;
        void Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { field = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }

        public string SearchText { get => searchText; set => Set(ref searchText, value); }
        public ObservableCollection<string> Categories { get; } = new() { All };
        public string? Category { get => category; set => Set(ref category, value); }
        public double MaxPrice { get => maxPrice; set => Set(ref maxPrice, value); }
        public bool InStockOnly { get => inStockOnly; set => Set(ref inStockOnly, value); }
        public string Status { get => status; set => Set(ref status, value); }
        public ObservableCollection<Part> Results { get; } = new();
        public Part? SelectedPart { get => selected; set { Set(ref selected, value); CommandManager.InvalidateRequerySuggested(); } }

        public ICommand SearchCommand { get; }
        public ICommand InsertCommand { get; }
        public ICommand PlaceCommand { get; }

        public FinderModel()
        {
            SearchCommand = new Relay(async () => await SearchAsync(), () => !busy);
            InsertCommand = new Relay(() => { if (SelectedPart is { } p) Drawing.AddLabel(Application.DocumentManager.MdiActiveDocument, p, null); return Task.CompletedTask; }, () => SelectedPart != null);
            PlaceCommand = new Relay(() => { Application.DocumentManager.MdiActiveDocument?.SendStringToExecute("_PARTSPLACE ", true, false, false); return Task.CompletedTask; }, () => SelectedPart != null);
            _ = LoadCategoriesAsync();
        }

        bool busy;

        async Task LoadCategoriesAsync()
        {
            try
            {
                var list = await PartsApi.CategoriesAsync(1500);   // back on the UI thread: WPF's dispatcher context
                foreach (var c in list) Categories.Add(c);
                Status = $"{list.Count} categories";
            }
            catch (Exception ex) { Status = "Categories failed: " + ex.Message; }
        }

        async Task SearchAsync()
        {
            busy = true; CommandManager.InvalidateRequerySuggested();
            Results.Clear();
            Status = "Searching...";
            try
            {
                var found = await PartsApi.SearchAsync(SearchText.Trim(), Category == All ? null : Category, 1500);
                foreach (var p in found.Where(p => MaxPrice <= 0 || p.Price <= MaxPrice).Where(p => !InStockOnly || p.Stock > 0)) Results.Add(p);
                Status = $"{Results.Count} parts";
            }
            catch (Exception ex) { Status = "Search failed: " + ex.Message; }
            finally { busy = false; CommandManager.InvalidateRequerySuggested(); }
        }
    }

    /// <summary>A command from a (possibly async) action and a CanExecute.</summary>
    public sealed class Relay : ICommand
    {
        readonly Func<Task> run;
        readonly Func<bool> can;
        public Relay(Func<Task> run, Func<bool> can) { this.run = run; this.can = can; }
        public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
        public bool CanExecute(object? parameter) => can();
        public async void Execute(object? parameter) => await run();
    }
}
