using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using WinForms = System.Windows.Forms;

namespace Retrace.Samples
{
    /// <summary>
    /// The page of DRAWPICK (a palette set of its own, "Retrace Pick", sized to show all of it): the controls a person clicks on the way to a drawing, each click doing something visible that only a real mouse click does, so a replay that skips one draws less:
    /// a tree (<c>PickTree</c>, "Catalog": Cabinets > Base > B12, B24; Cabinets > Wall > W30; Doors > Shaker, Slab) whose leaf click draws the item's text on layer <c>PickTree</c> and whose +/- buttons draw a line
    /// on <c>PickExpand</c> / <c>PickCollapse</c> (only when the mouse did it: a program that expands a node itself draws nothing); three picture boxes with images and no text (two with a tool tip, one with nothing
    /// to name it) that each draw a circle of their own radius on <c>PickImage</c>; a list view in large-icon view (<c>PickList</c>, "Finish": Oak, Pine, Birch) whose item click draws the item's text on
    /// <c>PickList</c>; and a clickable panel (<c>Swatch</c>) that draws a circle on <c>PickSwatch</c> at the point clicked (as a fraction of the panel). Nothing else is on the page: no button, so the final
    /// drawing is made of clicks alone, except a small second tree (<c>PickRootTree</c>, ShowRootLines = false: Alpha > A1, A2; Beta > B1) whose top-level nodes have no +/- button (an expand draws a line on <c>PickRootExpand</c>, a leaf click its name on <c>PickRoot</c>).
    /// </summary>
    internal class PickPage : WinForms.UserControl
    {
        private readonly System.ComponentModel.IContainer components = new System.ComponentModel.Container();
        private readonly WinForms.TreeView tree = new() { Name = "PickTree", HideSelection = false, Dock = WinForms.DockStyle.Top, Height = 130 };
        private readonly WinForms.TreeView rootTree = new() { Name = "PickRootTree", HideSelection = false, ShowRootLines = false, Dock = WinForms.DockStyle.Bottom, Height = 70 };
        private readonly WinForms.ListView list = new() { Name = "PickList", AccessibleName = "Finish", View = WinForms.View.LargeIcon, Dock = WinForms.DockStyle.Fill };

        private static System.Drawing.Bitmap Picture(System.Drawing.Color c, int size)
        {
            var bmp = new System.Drawing.Bitmap(size, size);
            using (var g = System.Drawing.Graphics.FromImage(bmp)) { g.Clear(System.Drawing.Color.White); using var b = new System.Drawing.SolidBrush(c); g.FillRectangle(b, 4, 4, size - 8, size - 8); }
            return bmp;
        }

        public PickPage()
        {
            // the tree's label is the text of the label above it, and that text changes with the filter box: a recording must find the tree by its Name, never by its label
            var treeLabel = new WinForms.Label { Name = "PickTreeLabel", Text = "Catalog: (Filter = '*')", Dock = WinForms.DockStyle.Top, AutoSize = false, Height = 18 };
            var filter = new WinForms.TextBox { Name = "PickFilter", Dock = WinForms.DockStyle.Top };
            filter.TextChanged += (_, _) => treeLabel.Text = "Catalog: (Filter = '" + filter.Text + "*')";
            filterBox = filter; labelAbove = treeLabel;
            var cabinets = tree.Nodes.Add("Cabinets");
            var bas = cabinets.Nodes.Add("Base");
            bas.Nodes.Add("B12");
            bas.Nodes.Add("B24");
            cabinets.Nodes.Add("Wall").Nodes.Add("W30");
            var doors = tree.Nodes.Add("Doors");
            doors.Nodes.Add("Shaker");
            doors.Nodes.Add("Slab");
            // a leaf click draws the item's name; an expand or a collapse made by the mouse draws a line (the Action says who did it)
            tree.NodeMouseClick += (_, e) =>
            {
                if (e.Button != WinForms.MouseButtons.Left || e.Node == null || e.Node.Nodes.Count > 0) return;
                var name = e.Node.Text;
                SamplePalette.Draw((_, _) => new DBText { TextString = name, Position = new Point3d(0, 0, 0), Height = 2.5 }, "PickTree");
            };
            tree.AfterExpand += (_, e) => { if (e.Action == WinForms.TreeViewAction.ByMouse) SamplePalette.Draw((_, _) => new Line(Point3d.Origin, new Point3d(10, 0, 0)), "PickExpand"); };
            tree.AfterCollapse += (_, e) => { if (e.Action == WinForms.TreeViewAction.ByMouse) SamplePalette.Draw((_, _) => new Line(Point3d.Origin, new Point3d(0, 10, 0)), "PickCollapse"); };

            // a second tree without root lines (ShowRootLines = false): comctl32 draws no +/- button on its top-level nodes, so they are opened by a double-click or the keyboard; any expand draws a line on PickRootExpand
            // (the action is not looked at: a replay opens such a node by message), a leaf click draws its name on PickRoot
            var a = rootTree.Nodes.Add("Alpha"); a.Nodes.Add("A1"); a.Nodes.Add("A2");
            rootTree.Nodes.Add("Beta").Nodes.Add("B1");
            rootTree.AfterExpand += (_, _) => SamplePalette.Draw((_, _) => new Line(Point3d.Origin, new Point3d(-10, 0, 0)), "PickRootExpand");
            rootTree.NodeMouseClick += (_, e) =>
            {
                if (e.Button != WinForms.MouseButtons.Left || e.Node == null || e.Node.Nodes.Count > 0) return;
                var name = e.Node.Text;
                SamplePalette.Draw((_, _) => new DBText { TextString = name, Position = new Point3d(0, -10, 0), Height = 2.5 }, "PickRoot");
            };

            var tips = new WinForms.ToolTip(components);
            var pictures = new WinForms.FlowLayoutPanel { Dock = WinForms.DockStyle.Top, AutoSize = true, Padding = new WinForms.Padding(6) };
            WinForms.PictureBox Make(string? name, System.Drawing.Color c, double radius, string? tip)
            {
                var p = new WinForms.PictureBox { Image = Picture(c, 40), SizeMode = WinForms.PictureBoxSizeMode.CenterImage, Width = 44, Height = 44, BorderStyle = WinForms.BorderStyle.FixedSingle };
                if (name != null) p.Name = name;
                if (tip != null) tips.SetToolTip(p, tip);
                p.Click += (_, _) => SamplePalette.Draw((_, _) => new Circle(new Point3d(radius * 10, 0, 0), Vector3d.ZAxis, radius), "PickImage");
                pictures.Controls.Add(p);
                return p;
            }
            Make(null, System.Drawing.Color.Red, 2, "Door style 1");
            Make(null, System.Drawing.Color.Green, 3, "Door style 2");
            Make(null, System.Drawing.Color.Blue, 4, null);   // nothing names it: a recording says "image no. 3"

            var images = new WinForms.ImageList(components) { ImageSize = new System.Drawing.Size(32, 32) };
            images.Images.Add(Picture(System.Drawing.Color.Peru, 32));
            images.Images.Add(Picture(System.Drawing.Color.Khaki, 32));
            images.Images.Add(Picture(System.Drawing.Color.Wheat, 32));
            list.LargeImageList = images;
            list.Items.Add(new WinForms.ListViewItem("Oak", 0));
            list.Items.Add(new WinForms.ListViewItem("Pine", 1));
            list.Items.Add(new WinForms.ListViewItem("Birch", 2));
            list.MouseClick += (_, e) =>
            {
                if (list.GetItemAt(e.X, e.Y) is not { } item) return;
                var name = item.Text;
                SamplePalette.Draw((_, _) => new DBText { TextString = name, Position = new Point3d(0, 10, 0), Height = 2.5 }, "PickList");
            };

            var swatch = new WinForms.Panel { Name = "Swatch", Height = 40, Dock = WinForms.DockStyle.Bottom, BackColor = System.Drawing.Color.Gainsboro };
            swatch.MouseClick += (_, e) =>
            {
                double fx = System.Math.Round(100.0 * e.X / System.Math.Max(1, swatch.Width)), fy = System.Math.Round(100.0 * e.Y / System.Math.Max(1, swatch.Height));
                SamplePalette.Draw((_, _) => new Circle(new Point3d(fx, fy, 0), Vector3d.ZAxis, 1.5), "PickSwatch");
            };

            Controls.Add(list);
            Controls.Add(rootTree);
            Controls.Add(swatch);
            Controls.Add(pictures);
            Controls.Add(tree);
            Controls.Add(labelAbove);
            Controls.Add(filterBox);
        }

        private readonly WinForms.TextBox filterBox;
        private readonly WinForms.Label labelAbove;

        protected override void Dispose(bool disposing) { if (disposing) components.Dispose(); base.Dispose(disposing); }
    }
}
