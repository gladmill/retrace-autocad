using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using WinForms = System.Windows.Forms;
using Wpf = System.Windows.Controls;

namespace Retrace.Samples
{
    /// <summary>
    /// DRAWPALETTE: a palette set with a WinForms page (Box), a WPF page (Circle), a WinForms page with a tree (Parts) and a WinForms page with a tab control (Tabs), the way many plugins keep their UI. The buttons
    /// draw straight from the palette (document lock, no command), so a recording only sees them through the palette itself.
    /// </summary>
    public class SamplePalette
    {
        private static PaletteSet? set;

        [CommandMethod("DRAWPALETTE")]
        public void Show()
        {
            if (set == null)
            {
                set = new PaletteSet("Retrace Samples", new System.Guid("6f1d2c3b-6a51-4a4e-9a52-2f5c1d7e9b10"));
                set.Style = PaletteSetStyles.ShowCloseButton | PaletteSetStyles.ShowAutoHideButton;
                set.MinimumSize = new System.Drawing.Size(240, 260);
                set.Add("Box", new BoxPage());
                set.AddVisual("Circle", CirclePage());
                set.Add("Parts", new PartsPage());
                set.Add("Tabs", new TabsPage());
                set.Add("Menus", new MenusPage());
            }
            set.Visible = true;
        }

        /// <summary>Draws in model space of the active drawing, from a palette button (outside any command).</summary>
        internal static void Draw(System.Func<Transaction, Database, Entity> make, string layer)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var db = doc.Database;
                var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                if (!layers.Has(layer))
                {
                    layers.UpgradeOpen();
                    var rec = new LayerTableRecord { Name = layer };
                    layers.Add(rec);
                    tr.AddNewlyCreatedDBObject(rec, true);
                }
                var ent = make(tr, db);
                ent.Layer = layer;
                var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                ms.AppendEntity(ent);
                tr.AddNewlyCreatedDBObject(ent, true);
                tr.Commit();
            }
            doc.Editor.WriteMessage("\nDrawn from the palette.");
        }

        private static System.Windows.UIElement CirclePage()
        {
            // a palette has no background of its own: without one the page is black in AutoCAD's dark theme
            var panel = new Wpf.StackPanel { Margin = new System.Windows.Thickness(8), Background = System.Windows.SystemColors.WindowBrush };
            panel.Children.Add(new Wpf.Label { Content = "Radius" });
            var radius = new Wpf.TextBox { Name = "RadiusBox", Text = "5" };
            // a WPF ContextMenu on the radius box: Radius > Ten / Twenty set the text, Reset puts the default back (a recorded test tells them apart by the circle drawn)
            var radiusMenu = new Wpf.ContextMenu();
            var preset = new Wpf.MenuItem { Header = "_Radius" };
            var ten = new Wpf.MenuItem { Header = "_Ten" };
            ten.Click += (_, _) => radius.Text = "10";
            var twenty = new Wpf.MenuItem { Header = "T_wenty" };
            twenty.Click += (_, _) => radius.Text = "20";
            preset.Items.Add(ten);
            preset.Items.Add(twenty);
            // Reset is disabled until the radius box got ContextMenuOpening: a replay that opens the menu without that event finds it disabled
            var reset = new Wpf.MenuItem { Header = "Re_set", IsEnabled = false };
            radius.ContextMenuOpening += (_, _) => reset.IsEnabled = true;
            reset.Click += (_, _) => radius.Text = "5";
            radiusMenu.Items.Add(preset);
            radiusMenu.Items.Add(reset);
            radius.ContextMenu = radiusMenu;
            panel.Children.Add(radius);
            panel.Children.Add(new Wpf.Label { Content = "Layer" });
            var layer = new Wpf.ComboBox { Name = "CircleLayer", IsEditable = false };
            foreach (var l in new[] { "Circles", "Holes", "0" }) layer.Items.Add(l);
            layer.SelectedIndex = 0;
            panel.Children.Add(layer);
            var center = new Wpf.CheckBox { Name = "AtOrigin", Content = "Center at 0,0", IsChecked = true, Margin = new System.Windows.Thickness(0, 6, 0, 6) };
            panel.Children.Add(center);
            var draw = new Wpf.Button { Name = "DrawCircle", Content = "Draw circle" };
            draw.Click += (_, _) =>
            {
                if (!double.TryParse(radius.Text, out var r) || r <= 0) { radius.Text = "5"; return; }
                var c = center.IsChecked == true ? Point3d.Origin : new Point3d(50, 50, 0);
                Draw((_, _) => new Circle(c, Vector3d.ZAxis, r), (string)layer.SelectedItem);
            };
            panel.Children.Add(draw);
            return panel;
        }
    }

    /// <summary>The WinForms page: width, height, layer, closed, and a Draw box button.</summary>
    internal class BoxPage : WinForms.UserControl
    {
        private readonly WinForms.NumericUpDown width = new() { Name = "WidthBox", Minimum = 1, Maximum = 1000, Value = 20 };
        private readonly WinForms.NumericUpDown height = new() { Name = "HeightBox", Minimum = 1, Maximum = 1000, Value = 10 };
        private readonly WinForms.TextBox layer = new() { Name = "LayerBox", Text = "Boxes" };
        private readonly WinForms.ComboBox corner = new() { Name = "CornerBox", DropDownStyle = WinForms.ComboBoxStyle.DropDownList };
        private readonly WinForms.CheckBox closed = new() { Name = "ClosedBox", Text = "Closed", Checked = true };
        private readonly WinForms.Button draw = new() { Name = "DrawBoxButton", Text = "Draw box" };

        public BoxPage()
        {
            corner.Items.AddRange(new object[] { "Lower left at 0,0", "Centered at 0,0" });
            corner.SelectedIndex = 0;
            var grid = new WinForms.TableLayoutPanel { Dock = WinForms.DockStyle.Fill, ColumnCount = 2, Padding = new WinForms.Padding(8) };
            void Row(string label, WinForms.Control c) { grid.Controls.Add(new WinForms.Label { Text = label, AutoSize = true }); grid.Controls.Add(c); }
            Row("Width", width);
            Row("Height", height);
            Row("Layer", layer);
            Row("Corner", corner);
            grid.Controls.Add(closed);
            grid.Controls.Add(draw);
            draw.Click += (_, _) =>
            {
                double w = (double)width.Value, h = (double)height.Value;
                var o = corner.SelectedIndex == 1 ? new Point2d(-w / 2, -h / 2) : Point2d.Origin;
                SamplePalette.Draw((_, _) =>
                {
                    var p = new Polyline();
                    p.AddVertexAt(0, o, 0, 0, 0);
                    p.AddVertexAt(1, new Point2d(o.X + w, o.Y), 0, 0, 0);
                    p.AddVertexAt(2, new Point2d(o.X + w, o.Y + h), 0, 0, 0);
                    p.AddVertexAt(3, new Point2d(o.X, o.Y + h), 0, 0, 0);
                    p.Closed = closed.Checked;
                    return p;
                }, layer.Text.Trim().Length > 0 ? layer.Text.Trim() : "0");
            };
            Controls.Add(grid);
        }
    }

    /// <summary>The WinForms page with a tree: pick a part (some nodes are filled when they are opened), then Draw part adds a TEXT with its name.</summary>
    internal class PartsPage : WinForms.UserControl
    {
        private readonly WinForms.TreeView tree = new() { Name = "PartsTree", AccessibleName = "Catalog", HideSelection = false, Dock = WinForms.DockStyle.Fill };
        private readonly WinForms.Label selected = new() { Name = "PartsSelected", AutoSize = true, Text = "Selected: (nothing)" };
        private readonly WinForms.Button draw = new() { Name = "DrawPartButton", Text = "Draw part", AutoSize = true };

        public PartsPage()
        {
            PartCatalog.Fill(tree);
            tree.AfterSelect += (_, e) => selected.Text = "Selected: " + (e.Node?.Text ?? "(nothing)") + " (" + e.Action + ")";
            draw.Click += (_, _) =>
            {
                var node = tree.SelectedNode;
                if (node == null) return;   // nothing picked: nothing to draw
                var name = node.Text;
                SamplePalette.Draw((_, _) => new DBText { TextString = name, Position = Point3d.Origin, Height = 2.5 }, "Parts");
            };
            // a double-click on a part (an item with no children: a node with children is opened by the tree itself) draws its name with " x2" behind it, on layer Parts
            tree.NodeMouseDoubleClick += (_, e) =>
            {
                if (e.Node == null || e.Node.Nodes.Count > 0) return;
                var name = e.Node.Text + " x2";
                SamplePalette.Draw((_, _) => new DBText { TextString = name, Position = new Point3d(0, -5, 0), Height = 2.5 }, "Parts");
            };
            var bottom = new WinForms.FlowLayoutPanel { Dock = WinForms.DockStyle.Bottom, AutoSize = true, FlowDirection = WinForms.FlowDirection.TopDown, Padding = new WinForms.Padding(8) };
            bottom.Controls.Add(selected);
            bottom.Controls.Add(draw);
            Controls.Add(tree);
            Controls.Add(bottom);
        }
    }

    /// <summary>The WinForms page with a context menu: a tree (<c>MenuTree</c>, "Notes") whose ContextMenuStrip has Edit > Rename, Edit > Duplicate and Properties... (a sub form with a Note field), and a Draw note
    /// button that draws the selected node's text and note on layer <c>Menus</c>, so a replay that skipped a menu choice draws other texts.</summary>
    internal class MenusPage : WinForms.UserControl
    {
        private readonly WinForms.TreeView tree = new() { Name = "MenuTree", AccessibleName = "Notes", HideSelection = false, Dock = WinForms.DockStyle.Fill };
        private readonly WinForms.Label status = new() { Name = "MenuStatus", AutoSize = true, Text = "No menu used yet" };
        private readonly WinForms.Button draw = new() { Name = "DrawNoteButton", Text = "Draw note", AutoSize = true };

        public MenusPage()
        {
            var one = tree.Nodes.Add("Bolts");
            one.Nodes.Add("M6");
            tree.Nodes.Add("Nuts");
            var rename = new WinForms.ToolStripMenuItem("&Rename");
            rename.Click += (_, _) => { if (tree.SelectedNode is { } n) { n.Text += " (renamed)"; status.Text = "Renamed"; } };
            var duplicate = new WinForms.ToolStripMenuItem("&Duplicate");
            duplicate.Click += (_, _) => { if (tree.SelectedNode is { } n) { (n.Parent?.Nodes ?? tree.Nodes).Add(new WinForms.TreeNode(n.Text + " copy")); status.Text = "Duplicated"; } };
            var edit = new WinForms.ToolStripMenuItem("&Edit");
            edit.DropDownItems.Add(rename);
            edit.DropDownItems.Add(duplicate);
            var properties = new WinForms.ToolStripMenuItem("Properties...");
            properties.Click += (_, _) =>
            {
                if (tree.SelectedNode is not { } n) return;
                using var form = new NoteForm(n.Tag as string ?? "");
                if (form.ShowDialog(this) == WinForms.DialogResult.OK) { n.Tag = form.Note; status.Text = "Note set"; }
            };
            var menu = new WinForms.ContextMenuStrip { Name = "NotesMenu" };
            menu.Items.Add(edit);
            menu.Items.Add(properties);
            tree.ContextMenuStrip = menu;
            tree.MouseDown += (_, e) => { if (e.Button == WinForms.MouseButtons.Right && tree.GetNodeAt(e.Location) is { } hit) tree.SelectedNode = hit; };
            draw.Click += (_, _) =>
            {
                var node = tree.SelectedNode;
                if (node == null) return;
                var text = node.Text + " / " + (node.Tag as string ?? "") + " / " + tree.Nodes.Count;
                SamplePalette.Draw((_, _) => new DBText { TextString = text, Position = Point3d.Origin, Height = 2.5 }, "Menus");
            };
            var bottom = new WinForms.FlowLayoutPanel { Dock = WinForms.DockStyle.Bottom, AutoSize = true, FlowDirection = WinForms.FlowDirection.TopDown, Padding = new WinForms.Padding(8) };
            bottom.Controls.Add(status);
            bottom.Controls.Add(draw);
            Controls.Add(tree);
            Controls.Add(bottom);
        }
    }

    /// <summary>The WinForms page with a tab control (palette tabs, a TabControl inside the page): Size tab (width, height), Style tab (Mark the center), and a Draw tabs button outside
    /// the tab control. Drawing uses the values of both tabs, so a replay that missed one draws something else.</summary>
    internal class TabsPage : WinForms.UserControl
    {
        private readonly WinForms.TabControl pages = new() { Name = "SizeStyleTabs", Dock = WinForms.DockStyle.Fill };
        private readonly WinForms.NumericUpDown width = new() { Name = "TabWidthBox", Minimum = 1, Maximum = 1000, Value = 30, Location = new System.Drawing.Point(70, 12), Size = new System.Drawing.Size(80, 23) };
        private readonly WinForms.NumericUpDown height = new() { Name = "TabHeightBox", Minimum = 1, Maximum = 1000, Value = 12, Location = new System.Drawing.Point(70, 44), Size = new System.Drawing.Size(80, 23) };
        private readonly WinForms.CheckBox mark = new() { Name = "TabMarkBox", Text = "Mark the center", AutoSize = true, Location = new System.Drawing.Point(12, 14) };
        private readonly WinForms.Button draw = new() { Name = "DrawTabsButton", Text = "Draw tabs", Dock = WinForms.DockStyle.Bottom };

        public TabsPage()
        {
            var size = new WinForms.TabPage("Size") { Name = "tabSizePage" };
            size.Controls.Add(new WinForms.Label { Text = "Width", AutoSize = true, Location = new System.Drawing.Point(12, 16) });
            size.Controls.Add(width);
            size.Controls.Add(new WinForms.Label { Text = "Height", AutoSize = true, Location = new System.Drawing.Point(12, 48) });
            size.Controls.Add(height);
            var style = new WinForms.TabPage("Style") { Name = "tabStylePage" };
            style.Controls.Add(mark);
            pages.TabPages.AddRange(new[] { size, style });
            draw.Click += (_, _) =>
            {
                double w = (double)width.Value, h = (double)height.Value;
                SamplePalette.Draw((_, _) =>
                {
                    var p = new Polyline();
                    p.AddVertexAt(0, Point2d.Origin, 0, 0, 0);
                    p.AddVertexAt(1, new Point2d(w, 0), 0, 0, 0);
                    p.AddVertexAt(2, new Point2d(w, h), 0, 0, 0);
                    p.AddVertexAt(3, new Point2d(0, h), 0, 0, 0);
                    p.Closed = true;
                    return p;
                }, "Tabs");
                if (mark.Checked) SamplePalette.Draw((_, _) => new Circle(new Point3d(w / 2, h / 2, 0), Vector3d.ZAxis, Math.Min(w, h) / 4), "Tabs");
            };
            Controls.Add(pages);
            Controls.Add(draw);
        }
    }
}
