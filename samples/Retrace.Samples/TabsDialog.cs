using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace Retrace.Samples
{
    /// <summary>
    /// Modal dialog of DRAWTABS: a TabControl with three pages, a field or two on each (General: Name; Size: Width, Height; Style: a check box and a combo box),
    /// then OK / Cancel. OK draws something from every value (<see cref="TabsCommands"/>), so a checkpoint fails when a value of any page is missing.
    /// The controls have names and accessible names, so a recording keys and names them (<c>tab:Size|name:widthBox</c>).
    /// </summary>
    public class TabsDialog : Form
    {
        private readonly TabControl tabs = new() { Name = "pages", Location = new Point(12, 12), Size = new Size(296, 160), TabIndex = 0 };
        private readonly TextBox nameBox = new() { Name = "nameBox", AccessibleName = "Name", Text = "Part-1", Location = new Point(96, 18), Size = new Size(160, 23), TabIndex = 1 };
        private readonly TextBox widthBox = new() { Name = "widthBox", AccessibleName = "Width", Text = "10", Location = new Point(96, 18), Size = new Size(80, 23), TabIndex = 1 };
        private readonly TextBox heightBox = new() { Name = "heightBox", AccessibleName = "Height", Text = "5", Location = new Point(96, 50), Size = new Size(80, 23), TabIndex = 2 };
        private readonly CheckBox markBox = new() { Name = "markBox", Text = "Mark the center", AutoSize = true, Location = new Point(16, 20), TabIndex = 1 };
        private readonly ComboBox markSizeBox = new()
        {
            Name = "markSizeBox", AccessibleName = "Mark radius", DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(96, 52), Size = new Size(80, 23), TabIndex = 2,
        };
        private readonly Button ok = new() { Name = "okButton", Text = "OK", Location = new Point(152, 184), Size = new Size(75, 27), TabIndex = 1 };
        private readonly Button cancel = new() { Name = "cancelButton", Text = "Cancel", Location = new Point(233, 184), Size = new Size(75, 27), TabIndex = 2, DialogResult = DialogResult.Cancel };

        public TabsDialog()
        {
            Text = "Plate";
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(320, 224);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AcceptButton = ok;
            CancelButton = cancel;

            markSizeBox.Items.AddRange(new object[] { "1", "2", "5" });
            markSizeBox.SelectedIndex = 0;

            var general = new TabPage("General") { Name = "generalPage" };
            general.Controls.Add(new Label { Name = "nameLabel", AutoSize = true, Location = new Point(16, 22), Text = "&Name:" });
            general.Controls.Add(nameBox);

            var size = new TabPage("Size") { Name = "sizePage" };
            size.Controls.Add(new Label { Name = "widthLabel", AutoSize = true, Location = new Point(16, 22), Text = "&Width:" });
            size.Controls.Add(widthBox);
            size.Controls.Add(new Label { Name = "heightLabel", AutoSize = true, Location = new Point(16, 54), Text = "&Height:" });
            size.Controls.Add(heightBox);

            var style = new TabPage("Style") { Name = "stylePage" };
            style.Controls.Add(markBox);
            style.Controls.Add(new Label { Name = "markSizeLabel", AutoSize = true, Location = new Point(16, 56), Text = "Mark &radius:" });
            style.Controls.Add(markSizeBox);

            tabs.TabPages.AddRange(new[] { general, size, style });
            Controls.Add(tabs);
            Controls.Add(ok);
            Controls.Add(cancel);
            ok.Click += OkClicked;
        }

        public string PlateName => nameBox.Text.Trim();
        public double PlateWidth => double.Parse(widthBox.Text, CultureInfo.InvariantCulture);
        public double PlateHeight => double.Parse(heightBox.Text, CultureInfo.InvariantCulture);
        public bool MarkCenter => markBox.Checked;
        public double MarkRadius => double.Parse(markSizeBox.Text, CultureInfo.InvariantCulture);

        private void OkClicked(object? sender, EventArgs e)
        {
            if (!double.TryParse(widthBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) || w <= 0
                || !double.TryParse(heightBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var h) || h <= 0)
            {
                tabs.SelectedTab = tabs.TabPages["sizePage"];
                MessageBox.Show(this, "Width and height must be numbers above zero.", "Plate", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
