using System.Drawing;
using System.Windows.Forms;

namespace Retrace.Samples
{
    /// <summary>
    /// Modal dialog of DRAWINSERT: a tree of parts on the left, a row of three image buttons (no text; they pick a variant) and the buttons Add and Quick add on the right,
    /// both of which close the dialog (OK is not used). A command that asks for a point and an angle follows it. <see cref="Low"/> opens it with its lower part below the
    /// screen, so a Tdoc run has to move it onto the screen before the picture.
    /// </summary>
    public class InsertDialog : Form
    {
        /// <summary>Open the next dialog partly below the bottom of the screen (DRAWINSERTLOW).</summary>
        public static bool Low;

        private readonly TreeView tree = new() { Name = "PartsTree", AccessibleName = "Parts", HideSelection = false, Location = new Point(16, 36), Size = new Size(200, 190), TabIndex = 1 };
        private readonly Label selected = new() { Name = "SelectedLabel", AutoSize = true, Location = new Point(16, 234), Text = "Selected: (nothing)" };
        private readonly Button add = new() { Name = "addButton", Text = "Add", Location = new Point(232, 36), Size = new Size(90, 27), TabIndex = 5 };
        private readonly Button quick = new() { Name = "quickButton", Text = "Quick add", Location = new Point(232, 70), Size = new Size(90, 27), TabIndex = 6 };
        private readonly Button cancel = new() { Name = "cancelButton", Text = "Cancel", Location = new Point(232, 262), Size = new Size(90, 27), TabIndex = 7, DialogResult = DialogResult.Cancel };

        private readonly ListView cabinets = new() { Name = "CabinetsList", View = View.LargeIcon, HideSelection = false, MultiSelect = false, Location = new Point(192, 36), Size = new Size(210, 190), TabIndex = 2 };

        /// <param name="withList">DRAWINSERTLIST: the tree, then a list view of large icons (cabinets) between it and the buttons, both with no label of their own (a control the recording can only name by its id), as a vendor dialog has them</param>
        public InsertDialog(string title = "Insert part", bool withList = false)
        {
            Text = title;
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = withList ? new Size(524, 306) : new Size(340, 306);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            low = Low;
            CancelButton = cancel;
            if (!withList) Controls.Add(new Label { Name = "partsLabel", AutoSize = true, Location = new Point(16, 14), Text = "&Parts:" });
            Controls.Add(tree);
            Controls.Add(selected);
            if (withList)
            {
                tree.Size = new Size(160, 190);
                tree.AccessibleName = null;
                var images = new ImageList { ImageSize = new Size(32, 32), ColorDepth = ColorDepth.Depth32Bit };
                string[] names = ["Base cabinet", "Wall cabinet", "Tall cabinet", "Corner cabinet", "Drawer unit", "Sink unit"];
                for (int i = 0; i < names.Length; i++)
                {
                    var bmp = new Bitmap(32, 32);
                    using (var g = Graphics.FromImage(bmp)) { g.Clear(Color.FromArgb(50 + 30 * i, 130, 210 - 25 * i)); g.DrawRectangle(Pens.Black, 3, 3, 25, 25); }
                    images.Images.Add(bmp);
                    cabinets.Items.Add(new ListViewItem(names[i], i));
                }
                cabinets.LargeImageList = images;
                Controls.Add(cabinets);
                var finishes = new ListBox { Name = "FinishList", Location = new Point(416, 110), Size = new Size(90, 100), TabIndex = 3 };   // a list box, no label either
                finishes.Items.AddRange(["Oak", "Walnut", "Maple", "Birch", "Ash"]);
                Controls.Add(finishes);
                add.Location = new Point(416, 36); quick.Location = new Point(416, 70); cancel.Location = new Point(416, 262);
            }
            else
            for (int i = 0; i < 3; i++)
            {
                var bmp = new Bitmap(24, 24);
                using (var g = Graphics.FromImage(bmp)) { g.Clear(Color.FromArgb(40 * (i + 1), 120, 200 - 50 * i)); g.DrawRectangle(Pens.Black, 4, 4, 15, 15); }
                Controls.Add(new Button { Name = "variant" + (i + 1), AccessibleName = "Variant " + (i + 1), Image = bmp, Location = new Point(232 + 32 * i, 120), Size = new Size(30, 30), TabIndex = 2 + i });
            }
            Controls.Add(add);
            Controls.Add(quick);
            Controls.Add(cancel);
            PartCatalog.Fill(tree);
            tree.AfterSelect += (_, e) => selected.Text = "Selected: " + (e.Node?.Text ?? "(nothing)");
            add.Click += (_, _) => Done();
            quick.Click += (_, _) => Done();
        }

        private readonly bool low;

        /// <summary>AutoCAD centres a modal dialog on its own, so the low position is set once it shows.</summary>
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (!low) return;
            var area = Screen.PrimaryScreen!.WorkingArea;
            Location = new Point(area.Left + 200, area.Bottom - 160);
        }

        /// <summary>The text of the selected node, or "" when there is none.</summary>
        public string PartName => tree.SelectedNode?.Text ?? "";

        private void Done()
        {
            if (tree.SelectedNode == null) { MessageBox.Show(this, "Select a part first.", "Insert part", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
