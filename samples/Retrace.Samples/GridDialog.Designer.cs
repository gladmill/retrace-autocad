#nullable disable
namespace Retrace.Samples
{
    partial class GridDialog
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && components != null) components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.rowsLabel = new System.Windows.Forms.Label();
            this.rowsBox = new System.Windows.Forms.NumericUpDown();
            this.columnsLabel = new System.Windows.Forms.Label();
            this.columnsBox = new System.Windows.Forms.NumericUpDown();
            this.spacingLabel = new System.Windows.Forms.Label();
            this.spacingBox = new System.Windows.Forms.NumericUpDown();
            this.layerLabel = new System.Windows.Forms.Label();
            this.layerBox = new System.Windows.Forms.TextBox();
            this.okButton = new System.Windows.Forms.Button();
            this.cancelButton = new System.Windows.Forms.Button();
            ((System.ComponentModel.ISupportInitialize)(this.rowsBox)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.columnsBox)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.spacingBox)).BeginInit();
            this.SuspendLayout();
            //
            // rowsLabel
            //
            this.rowsLabel.AutoSize = true;
            this.rowsLabel.Location = new System.Drawing.Point(16, 19);
            this.rowsLabel.Name = "rowsLabel";
            this.rowsLabel.Size = new System.Drawing.Size(37, 15);
            this.rowsLabel.TabIndex = 0;
            this.rowsLabel.Text = "Rows:";
            //
            // rowsBox
            //
            this.rowsBox.Location = new System.Drawing.Point(110, 16);
            this.rowsBox.Maximum = new decimal(new int[] { 100, 0, 0, 0 });
            this.rowsBox.Minimum = new decimal(new int[] { 1, 0, 0, 0 });
            this.rowsBox.Name = "rowsBox";
            this.rowsBox.Size = new System.Drawing.Size(120, 23);
            this.rowsBox.TabIndex = 1;
            this.rowsBox.Value = new decimal(new int[] { 3, 0, 0, 0 });
            //
            // columnsLabel
            //
            this.columnsLabel.AutoSize = true;
            this.columnsLabel.Location = new System.Drawing.Point(16, 51);
            this.columnsLabel.Name = "columnsLabel";
            this.columnsLabel.Size = new System.Drawing.Size(58, 15);
            this.columnsLabel.TabIndex = 2;
            this.columnsLabel.Text = "Columns:";
            //
            // columnsBox
            //
            this.columnsBox.Location = new System.Drawing.Point(110, 48);
            this.columnsBox.Maximum = new decimal(new int[] { 100, 0, 0, 0 });
            this.columnsBox.Minimum = new decimal(new int[] { 1, 0, 0, 0 });
            this.columnsBox.Name = "columnsBox";
            this.columnsBox.Size = new System.Drawing.Size(120, 23);
            this.columnsBox.TabIndex = 3;
            this.columnsBox.Value = new decimal(new int[] { 4, 0, 0, 0 });
            //
            // spacingLabel
            //
            this.spacingLabel.AutoSize = true;
            this.spacingLabel.Location = new System.Drawing.Point(16, 83);
            this.spacingLabel.Name = "spacingLabel";
            this.spacingLabel.Size = new System.Drawing.Size(52, 15);
            this.spacingLabel.TabIndex = 4;
            this.spacingLabel.Text = "Spacing:";
            //
            // spacingBox
            //
            this.spacingBox.DecimalPlaces = 2;
            this.spacingBox.Location = new System.Drawing.Point(110, 80);
            this.spacingBox.Maximum = new decimal(new int[] { 100000, 0, 0, 0 });
            this.spacingBox.Minimum = new decimal(new int[] { 1, 0, 0, 131072 });
            this.spacingBox.Name = "spacingBox";
            this.spacingBox.Size = new System.Drawing.Size(120, 23);
            this.spacingBox.TabIndex = 5;
            this.spacingBox.Value = new decimal(new int[] { 10, 0, 0, 0 });
            //
            // layerLabel
            //
            this.layerLabel.AutoSize = true;
            this.layerLabel.Location = new System.Drawing.Point(16, 115);
            this.layerLabel.Name = "layerLabel";
            this.layerLabel.Size = new System.Drawing.Size(77, 15);
            this.layerLabel.TabIndex = 6;
            this.layerLabel.Text = "Layer name:";
            //
            // layerBox
            //
            this.layerBox.Location = new System.Drawing.Point(110, 112);
            this.layerBox.Name = "layerBox";
            this.layerBox.Size = new System.Drawing.Size(160, 23);
            this.layerBox.TabIndex = 7;
            this.layerBox.Text = "GRID";
            //
            // okButton
            //
            this.okButton.Location = new System.Drawing.Point(114, 156);
            this.okButton.Name = "okButton";
            this.okButton.Size = new System.Drawing.Size(75, 25);
            this.okButton.TabIndex = 8;
            this.okButton.Text = "OK";
            this.okButton.UseVisualStyleBackColor = true;
            this.okButton.Click += new System.EventHandler(this.okButton_Click);
            //
            // cancelButton
            //
            this.cancelButton.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            this.cancelButton.Location = new System.Drawing.Point(195, 156);
            this.cancelButton.Name = "cancelButton";
            this.cancelButton.Size = new System.Drawing.Size(75, 25);
            this.cancelButton.TabIndex = 9;
            this.cancelButton.Text = "Cancel";
            this.cancelButton.UseVisualStyleBackColor = true;
            //
            // GridDialog
            //
            this.AcceptButton = this.okButton;
            this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.CancelButton = this.cancelButton;
            this.ClientSize = new System.Drawing.Size(290, 197);
            this.Controls.Add(this.rowsLabel);
            this.Controls.Add(this.rowsBox);
            this.Controls.Add(this.columnsLabel);
            this.Controls.Add(this.columnsBox);
            this.Controls.Add(this.spacingLabel);
            this.Controls.Add(this.spacingBox);
            this.Controls.Add(this.layerLabel);
            this.Controls.Add(this.layerBox);
            this.Controls.Add(this.okButton);
            this.Controls.Add(this.cancelButton);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "GridDialog";
            this.ShowInTaskbar = false;
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "Draw grid";
            ((System.ComponentModel.ISupportInitialize)(this.rowsBox)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.columnsBox)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.spacingBox)).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        private System.Windows.Forms.Label rowsLabel;
        private System.Windows.Forms.NumericUpDown rowsBox;
        private System.Windows.Forms.Label columnsLabel;
        private System.Windows.Forms.NumericUpDown columnsBox;
        private System.Windows.Forms.Label spacingLabel;
        private System.Windows.Forms.NumericUpDown spacingBox;
        private System.Windows.Forms.Label layerLabel;
        private System.Windows.Forms.TextBox layerBox;
        private System.Windows.Forms.Button okButton;
        private System.Windows.Forms.Button cancelButton;
    }
}
