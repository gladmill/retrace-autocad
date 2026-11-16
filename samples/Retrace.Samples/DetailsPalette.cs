using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using WinForms = System.Windows.Forms;

namespace Retrace.Samples
{
    /// <summary>
    /// A palette whose tree reacts to a selection the way a details browser does: about 500 ms after a node is selected the page refreshes a details label, shows a tooltip and touches the tree itself,
    /// without the person doing anything. Made to see whether such programmatic changes are ever recorded as a click.
    /// DRAWDETAILS re-selects the same node (selection cleared, then set again), DRAWDETAILSREBUILD rebuilds all nodes and restores the selection, DRAWDETAILSSLOW blocks the UI thread for 600 ms inside the
    /// selection handler (a slow lookup done synchronously) and then refreshes, DRAWDETAILSPLAIN only refreshes the label and lets a node be renamed in place (a slow second click starts the edit). The "Draw selected" button draws a TEXT with the selected node's name.
    /// </summary>
    public class DetailsPalette
    {
        private static PaletteSet? set;
        private static string? mode;

        [CommandMethod("DRAWDETAILS")] public void Reselect() => Show("reselect");
        [CommandMethod("DRAWDETAILSREBUILD")] public void Rebuild() => Show("rebuild");
        [CommandMethod("DRAWDETAILSSLOW")] public void Slow() => Show("slow");
        [CommandMethod("DRAWDETAILSPLAIN")] public void Plain() => Show("plain");

        private static void Show(string wanted)
        {
            if (set != null && mode != wanted) { set.Visible = false; set.Dispose(); set = null; }
            if (set == null)
            {
                mode = wanted;
                set = new PaletteSet("Retrace Details", new System.Guid("0c4e7a15-52b3-4d8e-9f21-6a8b3d5c7e90"));
                set.Style = PaletteSetStyles.ShowCloseButton;
                set.MinimumSize = new System.Drawing.Size(260, 360);
                set.Size = new System.Drawing.Size(300, 420);
                set.Add("Details", new DetailsPage(wanted));
            }
            set.Visible = true;
        }
    }

    internal class DetailsPage : WinForms.UserControl
    {
        private readonly string mode;
        private readonly WinForms.TreeView tree = new() { Name = "DetailsTree", AccessibleName = "Parts", HideSelection = false, Dock = WinForms.DockStyle.Fill, ShowNodeToolTips = true };
        private readonly WinForms.Label details = new() { Name = "DetailsLabel", AutoSize = true, Text = "Details: (nothing)" };
        private readonly WinForms.Button draw = new() { Name = "DrawSelectedButton", Text = "Draw selected", AutoSize = true };
        private readonly WinForms.ToolTip tip = new() { InitialDelay = 300, ReshowDelay = 100 };
        private readonly WinForms.Timer refresh = new() { Interval = 500 };
        private bool busy;
        private int refreshes;

        public DetailsPage(string mode)
        {
            this.mode = mode;
            tree.LabelEdit = mode == "plain";   // a slow second click on the selected node starts a label edit (no mouse-down of its own)
            Fill();
            tree.AfterSelect += (_, e) =>
            {
                if (busy || e.Node == null) return;
                if (mode == "slow") { System.Threading.Thread.Sleep(600); Refresh(e.Node); return; }
                refresh.Stop(); refresh.Start();
            };
            refresh.Tick += (_, _) => { refresh.Stop(); if (tree.SelectedNode is { } n) Refresh(n); };
            draw.Click += (_, _) =>
            {
                var name = tree.SelectedNode?.Text;
                if (name == null) return;
                SamplePalette.Draw((_, _) => new Autodesk.AutoCAD.DatabaseServices.DBText { TextString = name, Position = Point3d.Origin, Height = 2.5 }, "Details");
            };
            var bottom = new WinForms.FlowLayoutPanel { Dock = WinForms.DockStyle.Bottom, AutoSize = true, FlowDirection = WinForms.FlowDirection.TopDown, Padding = new WinForms.Padding(8) };
            bottom.Controls.Add(details);
            bottom.Controls.Add(draw);
            Controls.Add(tree);
            Controls.Add(bottom);
        }

        private void Fill()
        {
            tree.Nodes.Clear();
            var parts = tree.Nodes.Add("Parts");
            foreach (var n in new[] { "Alpha", "Beta", "Gamma" }) parts.Nodes.Add(new WinForms.TreeNode(n) { ToolTipText = "Tip for " + n });
            var tools = tree.Nodes.Add("Tools");
            foreach (var n in new[] { "Hammer", "Wrench" }) tools.Nodes.Add(new WinForms.TreeNode(n) { ToolTipText = "Tip for " + n });
            tree.ExpandAll();
        }

        private void Refresh(WinForms.TreeNode node)
        {
            busy = true;
            try
            {
                refreshes++;
                details.Text = "Details: " + node.Text + " #" + refreshes;
                var path = node.FullPath;
                if (mode == "reselect")
                {
                    tree.SelectedNode = null;    // the add-in selects the node again to "reload" it
                    tree.SelectedNode = node;
                }
                else if (mode == "rebuild")
                {
                    tree.BeginUpdate();
                    Fill();
                    var again = Find(tree.Nodes, path);
                    if (again != null) tree.SelectedNode = again;
                    tree.EndUpdate();
                }
                if (tree.SelectedNode is { } sel && tree.IsHandleCreated) tip.Show("Details of " + sel.Text, tree, 12, tree.Height - 24, 1500);
            }
            finally { busy = false; }
        }

        private static WinForms.TreeNode? Find(WinForms.TreeNodeCollection nodes, string fullPath)
        {
            foreach (WinForms.TreeNode n in nodes)
            {
                if (n.FullPath == fullPath) return n;
                if (Find(n.Nodes, fullPath) is { } f) return f;
            }
            return null;
        }
    }
}
