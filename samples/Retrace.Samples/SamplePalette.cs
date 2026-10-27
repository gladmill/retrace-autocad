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
    /// DRAWPALETTE: a palette set with a WinForms page (Box) and a WPF page (Circle), the way many plugins keep their UI. The buttons
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
}
