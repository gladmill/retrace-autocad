using System.Drawing;
using System.Windows.Forms;

namespace Retrace.Samples
{
    /// <summary>
    /// Modal dialog of DRAWPART: a TreeView of parts (some nodes are filled when they are opened, see <see cref="PartCatalog"/>) and OK / Cancel.
    /// The tree has a Name and an AccessibleName, so a recording keys and names it. OK needs a selected part.
    /// </summary>
    public class TreeDialog : Form
    {
        private readonly TreeView tree = new()
        {
            Name = "CatalogTree", AccessibleName = "Catalog", HideSelection = false,
            Location = new Point(16, 36), Size = new Size(288, 190), TabIndex = 1,
        };
        private readonly Label selected = new() { Name = "SelectedLabel", AutoSize = true, Location = new Point(16, 234), Text = "Selected: (nothing)" };
        private readonly Button ok = new() { Name = "okButton", Text = "OK", Location = new Point(148, 264), Size = new Size(75, 27), TabIndex = 2 };
        private readonly Button cancel = new() { Name = "cancelButton", Text = "Cancel", Location = new Point(229, 264), Size = new Size(75, 27), TabIndex = 3, DialogResult = DialogResult.Cancel };

        public TreeDialog()
        {
            Text = "Choose part";
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(320, 306);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(new Label { Name = "catalogLabel", AutoSize = true, Location = new Point(16, 14), Text = "&Catalog:" });
            Controls.Add(tree);
            Controls.Add(selected);
            Controls.Add(ok);
            Controls.Add(cancel);
            PartCatalog.Fill(tree);
            tree.AfterSelect += (_, e) => selected.Text = "Selected: " + (e.Node?.Text ?? "(nothing)") + " (" + e.Action + ")";   // ByMouse, ByKeyboard or Unknown (a program's own selection)
            ok.Click += OkClicked;
        }

        /// <summary>The text of the selected node (the part), or "" when there is none.</summary>
        public string PartName => tree.SelectedNode?.Text ?? "";

        private void OkClicked(object? sender, EventArgs e)
        {
            if (tree.SelectedNode == null)
            {
                MessageBox.Show(this, "Select a part first.", "Choose part", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
