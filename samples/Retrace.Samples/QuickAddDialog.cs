using System.Drawing;
using System.Windows.Forms;

namespace Retrace.Samples
{
    /// <summary>The dialog of DRAWQUICKADD: a tree (Parts), a large-icon list that a tree selection fills, push buttons Place (closes the dialog, keeps the chosen item) and Close.
    /// The tree and the list have no label of their own on purpose.</summary>
    public class QuickAddDialog : Form
    {
        private readonly TreeView tree = new() { Name = "partsTree", HideSelection = false };
        private readonly ListView list = new() { Name = "partsList", View = View.LargeIcon, MultiSelect = false, HideSelection = false };
        private readonly Button placeButton = new() { Name = "placeButton", Text = "Place" };
        private readonly Button closeButton = new() { Name = "closeButton", Text = "Close" };
        private readonly ImageList icons = new() { ImageSize = new Size(48, 48), ColorDepth = ColorDepth.Depth32Bit };

        public bool Placed { get; private set; }
        public string Chosen { get; private set; } = "";

        public QuickAddDialog()
        {
            Text = "Quick add";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(620, 330);

            tree.Location = new Point(12, 12); tree.Size = new Size(170, 300);
            list.Location = new Point(192, 12); list.Size = new Size(320, 300);
            placeButton.Location = new Point(524, 12); placeButton.Size = new Size(84, 28);
            closeButton.Location = new Point(524, 48); closeButton.Size = new Size(84, 28);
            Controls.AddRange(new Control[] { tree, list, placeButton, closeButton });

            for (int i = 0; i < 4; i++) icons.Images.Add(Icon(i));
            list.LargeImageList = icons;

            var parts = tree.Nodes.Add("Parts");
            var cabinets = parts.Nodes.Add("Cabinets");
            cabinets.Nodes.Add("Base");
            cabinets.Nodes.Add("Wall");
            var hardware = parts.Nodes.Add("Hardware");
            hardware.Nodes.Add("Bolts");
            parts.Expand(); cabinets.Expand();

            tree.AfterSelect += (_, e) => Fill(e.Node);
            placeButton.Click += (_, _) =>
            {
                if (list.SelectedItems.Count == 0) return;
                Chosen = list.SelectedItems[0].Text; Placed = true; Close();
            };
            closeButton.Click += (_, _) => Close();
        }

        /// <summary>The list is empty until a leaf of the tree is selected.</summary>
        private void Fill(TreeNode? node)
        {
            list.Items.Clear();
            if (node == null || node.Nodes.Count > 0) return;
            for (int i = 0; i < 4; i++) list.Items.Add(new ListViewItem(node.Text + " " + (i + 1), i));
        }

        private static Bitmap Icon(int i)
        {
            var bmp = new Bitmap(48, 48);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.White);
            using var b = new SolidBrush(new[] { Color.SteelBlue, Color.OrangeRed, Color.SeaGreen, Color.Goldenrod }[i % 4]);
            g.FillRectangle(b, 6, 6 + i * 2, 36, 36 - i * 2);
            return bmp;
        }
    }
}
