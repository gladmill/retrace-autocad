using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Windows;
using WinForms = System.Windows.Forms;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Retrace.PartsFinder
{
    /// <summary>One product from the API.</summary>
    public sealed record Part(int Id, string Title, string Category, double Price, int Stock, double Rating)
    {
        public override string ToString() => $"{Title} - {Price.ToString("0.00", CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// The Parts Finder palette set: a WinForms page with filter controls whose data comes from an HTTP API (dummyjson.com, a public
    /// test API) through async calls. Floating, it rolls up when the mouse leaves it (AutoRollUp); docked, it is anchored (auto-hide).
    /// </summary>
    public static class FinderPalette
    {
        static PaletteSet? set;
        static FinderPage? page;

        public static Part? Selected => page?.SelectedPart;

        public static void Show(bool docked)
        {
            if (set == null)
            {
                set = new PaletteSet("Parts Finder", new Guid("b7d3a0c4-2f1e-4c55-9d0a-5a6e0f2c7b31"));
                set.Style = PaletteSetStyles.ShowAutoHideButton | PaletteSetStyles.ShowCloseButton | PaletteSetStyles.ShowPropertiesMenu;
                set.MinimumSize = new System.Drawing.Size(260, 420);
                set.DockEnabled = DockSides.Left | DockSides.Right | DockSides.None;
                page = new FinderPage();
                set.Add("Search", page);
            }
            set.Visible = true;
            var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
            try
            {
                // Anchored (auto-hide) is read-only. Seen in R25.1: a floating palette set with AutoRollUp that is then docked becomes anchored
                // (a tab at the side that slides out on hover); AutoRollUp set on an already docked one changes nothing.
                set.Dock = DockSides.None; set.AutoRollUp = true;
                if (docked) set.Dock = DockSides.Left;
            }
            catch (System.Exception ex) { ed?.WriteMessage($"\nParts Finder: could not set the {(docked ? "docked auto-hide" : "auto roll-up")} mode: {ex.Message}"); }
            ed?.WriteMessage($"\nParts Finder: {(docked ? "docked, auto-hide" : "floating, auto roll-up")} (Dock={set.Dock}, Anchored={Try(() => set.Anchored)}, AutoRollUp={Try(() => set.AutoRollUp)}).");
        }

        static string Try(Func<bool> f) { try { return f().ToString(); } catch (System.Exception ex) { return "? " + ex.GetType().Name; } }
    }

    /// <summary>Calls to the parts API. Every call is async; <c>delay</c> asks the API to answer that many ms late (dummyjson's own parameter).</summary>
    public static class PartsApi
    {
        static readonly HttpClient Http = new() { BaseAddress = new Uri("https://dummyjson.com/"), Timeout = TimeSpan.FromSeconds(20) };

        public static async Task<List<string>> CategoriesAsync(int delayMs)
        {
            using var s = await Http.GetStreamAsync($"products/category-list?delay={delayMs}").ConfigureAwait(false);
            return (await JsonSerializer.DeserializeAsync<List<string>>(s).ConfigureAwait(false)) ?? new();
        }

        public static async Task<List<Part>> SearchAsync(string text, string? category, int delayMs)
        {
            var url = category == null
                ? $"products/search?q={Uri.EscapeDataString(text)}&limit=50&delay={delayMs}"
                : $"products/category/{Uri.EscapeDataString(category)}?limit=50&delay={delayMs}";
            using var s = await Http.GetStreamAsync(url).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(s).ConfigureAwait(false);
            var parts = new List<Part>();
            foreach (var p in doc.RootElement.GetProperty("products").EnumerateArray())
                parts.Add(new Part(p.GetProperty("id").GetInt32(), p.GetProperty("title").GetString() ?? "", p.GetProperty("category").GetString() ?? "",
                    p.GetProperty("price").GetDouble(), p.GetProperty("stock").GetInt32(), p.GetProperty("rating").GetDouble()));
            // the category endpoint has no text filter: filter here
            return category == null || text.Length == 0 ? parts : parts.Where(p => p.Title.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
        }
    }

    /// <summary>Search text, category (loaded from the API), max price, in stock only, API delay; Search, results, Insert, Place....</summary>
    internal class FinderPage : WinForms.UserControl
    {
        const string All = "(all categories)";
        readonly WinForms.TextBox search = new() { Name = "SearchBox", Text = "", Width = 150 };
        readonly WinForms.ComboBox category = new() { Name = "CategoryBox", DropDownStyle = WinForms.ComboBoxStyle.DropDownList, Width = 150 };
        readonly WinForms.NumericUpDown maxPrice = new() { Name = "MaxPriceBox", Minimum = 0, Maximum = 100000, Value = 1000, Width = 100 };
        readonly WinForms.CheckBox inStock = new() { Name = "InStockBox", Text = "In stock only", AutoSize = true };
        readonly WinForms.NumericUpDown delay = new() { Name = "DelayBox", Minimum = 0, Maximum = 5000, Increment = 250, Value = 1500, Width = 100 };
        readonly WinForms.Button searchButton = new() { Name = "SearchButton", Text = "Search", AutoSize = true };
        readonly WinForms.Label status = new() { Name = "StatusLabel", Text = "Loading categories...", AutoSize = true };
        // AccessibleName: the label Retrace uses; without it the list would be named after the status text above it ("21 parts")
        readonly WinForms.ListBox results = new() { Name = "ResultsList", AccessibleName = "Results", Width = 230, Height = 150 };
        readonly WinForms.Button insert = new() { Name = "InsertButton", Text = "Insert", AutoSize = true, Enabled = false };
        readonly WinForms.Button place = new() { Name = "PlaceButton", Text = "Place...", AutoSize = true, Enabled = false };

        public Part? SelectedPart => results.SelectedItem as Part;

        public FinderPage()
        {
            var grid = new WinForms.TableLayoutPanel { Dock = WinForms.DockStyle.Fill, ColumnCount = 2, Padding = new WinForms.Padding(6), AutoScroll = true };
            grid.ColumnStyles.Add(new WinForms.ColumnStyle(WinForms.SizeType.AutoSize));
            grid.ColumnStyles.Add(new WinForms.ColumnStyle(WinForms.SizeType.Percent, 100));
            void Row(string label, WinForms.Control c) { grid.Controls.Add(new WinForms.Label { Text = label, AutoSize = true, Anchor = WinForms.AnchorStyles.Left }); grid.Controls.Add(c); }
            Row("Search", search);
            Row("Category", category);
            Row("Max price", maxPrice);
            grid.Controls.Add(new WinForms.Label { Text = "" }); grid.Controls.Add(inStock);
            Row("API delay (ms)", delay);
            grid.Controls.Add(searchButton); grid.Controls.Add(status);
            grid.Controls.Add(results); grid.SetColumnSpan(results, 2);
            grid.Controls.Add(insert); grid.Controls.Add(place);
            Controls.Add(grid);

            category.Items.Add(All);
            category.SelectedIndex = 0;
            searchButton.Click += async (_, _) => await SearchAsync();
            results.SelectedIndexChanged += (_, _) => insert.Enabled = place.Enabled = results.SelectedItem is Part;
            insert.Click += (_, _) => { if (SelectedPart is { } p) Drawing.AddLabel(Application.DocumentManager.MdiActiveDocument, p, null); };
            place.Click += (_, _) => Application.DocumentManager.MdiActiveDocument?.SendStringToExecute("_PARTSPLACE ", true, false, false);
            HandleCreated += async (_, _) => await LoadCategoriesAsync();
        }

        /// <summary>Runs <paramref name="a"/> on the palette's thread: an await in AutoCAD may come back on a pool thread.</summary>
        void Ui(Action a) { if (IsDisposed) return; if (InvokeRequired) BeginInvoke(a); else a(); }

        async Task LoadCategoriesAsync()
        {
            try
            {
                var list = await PartsApi.CategoriesAsync((int)delay.Value);
                Ui(() =>
                {
                    foreach (var c in list) category.Items.Add(c);
                    status.Text = $"{list.Count} categories";
                });
            }
            catch (System.Exception ex) { Ui(() => status.Text = "Categories failed: " + ex.Message); }
        }

        async Task SearchAsync()
        {
            searchButton.Enabled = false;
            results.Items.Clear();
            status.Text = "Searching...";
            string text = search.Text.Trim(), cat = category.Text;
            decimal max = maxPrice.Value; bool stock = inStock.Checked; int ms = (int)delay.Value;
            try
            {
                var found = await PartsApi.SearchAsync(text, cat == All ? null : cat, ms);
                var shown = found.Where(p => (double)max <= 0 || p.Price <= (double)max).Where(p => !stock || p.Stock > 0).ToList();
                Ui(() =>
                {
                    foreach (var p in shown) results.Items.Add(p);
                    status.Text = $"{shown.Count} parts";
                    searchButton.Enabled = true;
                });
            }
            catch (System.Exception ex) { Ui(() => { status.Text = "Search failed: " + ex.Message; searchButton.Enabled = true; }); }
        }
    }

    public static class Drawing
    {
        static int count;

        /// <summary>A TEXT "title (category) price" on layer PARTS, at <paramref name="at"/> or under the previous one.</summary>
        public static void AddLabel(Document? doc, Part p, Point3d? at)
        {
            if (doc == null) return;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var db = doc.Database;
                var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                if (!layers.Has("PARTS"))
                {
                    layers.UpgradeOpen();
                    var rec = new LayerTableRecord { Name = "PARTS" };
                    layers.Add(rec);
                    tr.AddNewlyCreatedDBObject(rec, true);
                }
                var text = new DBText
                {
                    Position = at ?? new Point3d(0, -5 * count++, 0), Height = 2.5, Layer = "PARTS",
                    TextString = $"{p.Title} ({p.Category}) {p.Price.ToString("0.00", CultureInfo.InvariantCulture)}",
                };
                var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                ms.AppendEntity(text);
                tr.AddNewlyCreatedDBObject(text, true);
                tr.Commit();
            }
            doc.Editor.WriteMessage($"\nInserted {p.Title}.");
        }
    }
}
