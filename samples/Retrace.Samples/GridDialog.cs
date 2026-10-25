using System.Windows.Forms;

namespace Retrace.Samples
{
    /// <summary>Small modal dialog for DRAWGRID. Controls are laid out in GridDialog.Designer.cs.</summary>
    public partial class GridDialog : Form
    {
        public GridDialog()
        {
            InitializeComponent();
        }

        public int Rows => (int)rowsBox.Value;
        public int Columns => (int)columnsBox.Value;
        public double Spacing => (double)spacingBox.Value;
        public string LayerName => layerBox.Text.Trim();

        private void okButton_Click(object? sender, EventArgs e)
        {
            if (LayerName.Length == 0 || LayerName.IndexOfAny(new[] { '<', '>', '/', '\\', '"', ':', ';', '?', '*', '|', ',', '=', '`' }) >= 0)
            {
                MessageBox.Show(this, "Enter a valid layer name.", "Grid", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
