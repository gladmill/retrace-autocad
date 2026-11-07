using System.Windows.Forms;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Retrace.Samples
{
    /// <summary>
    /// The dialog of DRAWITEMS, a test for buttons inside a dialog: <b>Add</b> appends the text box to the list and clears it, <b>Options...</b> opens a
    /// nested dialog (<see cref="ItemsOptionsDialog"/>), a label counts the items ("2 items"). OK draws one TEXT per item, so a replay that skips an
    /// Add or the Options dialog draws something else and a checkpoint fails. Controls are built in code, with names (the replay finds them by name).
    /// </summary>
    public class ItemsDialog : Form
    {
        private readonly TextBox itemBox = new() { Name = "itemBox", AccessibleName = "Item" };
        private readonly Button addButton = new() { Name = "addButton", Text = "&Add" };
        private readonly ListBox itemList = new() { Name = "itemList", AccessibleName = "Added items", SelectionMode = SelectionMode.None };
        private readonly Label countLabel = new() { Name = "countLabel", AutoSize = true };
        private readonly Button optionsButton = new() { Name = "optionsButton", Text = "&Options..." };
        private readonly Button clearButton = new() { Name = "clearButton", Text = "&Clear" };
        private readonly Label optionsLabel = new() { Name = "optionsLabel", AutoSize = true };
        private readonly Button okButton = new() { Name = "okButton", Text = "OK" };
        private readonly Button cancelButton = new() { Name = "cancelButton", Text = "Cancel", DialogResult = DialogResult.Cancel };

        private readonly List<string> items = new();

        public IReadOnlyList<string> Items => items;
        public double Spacing { get; private set; } = 5;
        public string Prefix { get; private set; } = "";

        public ItemsDialog()
        {
            Text = "Draw items";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new System.Drawing.Size(330, 250);
            AcceptButton = addButton;   // Enter in the text box adds the item (OK is pressed with the mouse); Alt+A and Alt+O are the mnemonics
            CancelButton = cancelButton;

            var itemLabel = new Label { Text = "Item:", AutoSize = true, Location = new System.Drawing.Point(16, 19) };
            itemBox.Location = new System.Drawing.Point(80, 16); itemBox.Size = new System.Drawing.Size(150, 23);
            addButton.Location = new System.Drawing.Point(240, 15); addButton.Size = new System.Drawing.Size(75, 25);
            itemList.Location = new System.Drawing.Point(16, 52); itemList.Size = new System.Drawing.Size(299, 90);
            countLabel.Location = new System.Drawing.Point(16, 152);
            optionsButton.Location = new System.Drawing.Point(240, 146); optionsButton.Size = new System.Drawing.Size(75, 25);
            clearButton.Location = new System.Drawing.Point(159, 146); clearButton.Size = new System.Drawing.Size(75, 25);
            optionsLabel.Location = new System.Drawing.Point(16, 178);
            okButton.Location = new System.Drawing.Point(153, 212); okButton.Size = new System.Drawing.Size(75, 25);
            cancelButton.Location = new System.Drawing.Point(240, 212); cancelButton.Size = new System.Drawing.Size(75, 25);
            Controls.AddRange(new Control[] { itemLabel, itemBox, addButton, itemList, countLabel, clearButton, optionsButton, optionsLabel, okButton, cancelButton });

            addButton.Click += (_, _) => AddItem();
            optionsButton.Click += (_, _) => ChooseOptions();
            clearButton.Click += (_, _) => ClearItems();
            okButton.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
            ShowCount();
            ShowOptions();
        }

        /// <summary>Adds the text box's text to the list and clears the box. An empty box adds nothing.</summary>
        private void AddItem()
        {
            var text = itemBox.Text.Trim();
            if (text.Length == 0) return;
            items.Add(text);
            itemList.Items.Add(text);
            itemBox.Clear();
            itemBox.Focus();
            ShowCount();
        }

        /// <summary>Asks "Remove all items?" in a message box (Yes / No: a nested dialog of buttons and a text, no fields), empties the list on Yes and says so in a second message box
        /// (OK): two nested dialogs, one after the other, from one click.</summary>
        private void ClearItems()
        {
            if (items.Count == 0) return;
            var count = items.Count;
            if (MessageBox.Show(this, "Remove all " + count + " items?", "Remove items", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            items.Clear();
            itemList.Items.Clear();
            ShowCount();
            MessageBox.Show(this, count + " items removed.", "Items removed", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>Opens the options as a nested modal dialog; OK keeps its values.</summary>
        private void ChooseOptions()
        {
            using var dialog = new ItemsOptionsDialog(Spacing, Prefix);
            if (Application.ShowModalDialog(this, dialog, false) != DialogResult.OK) return;
            Spacing = dialog.Spacing;
            Prefix = dialog.Prefix;
            ShowOptions();
        }

        private void ShowCount() => countLabel.Text = items.Count == 1 ? "1 item" : items.Count + " items";

        private void ShowOptions() => optionsLabel.Text = "Spacing " + Spacing.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + ", prefix " + (Prefix.Length == 0 ? "none" : Prefix);
    }

    /// <summary>The nested dialog of DRAWITEMS: the row spacing and a prefix put before every text.</summary>
    public class ItemsOptionsDialog : Form
    {
        private readonly NumericUpDown spacingBox = new() { Name = "spacingBox", DecimalPlaces = 1, Minimum = 1, Maximum = 100 };
        private readonly TextBox prefixBox = new() { Name = "prefixBox" };
        private readonly Button okButton = new() { Name = "okButton", Text = "OK" };
        private readonly Button cancelButton = new() { Name = "cancelButton", Text = "Cancel", DialogResult = DialogResult.Cancel };

        public double Spacing => (double)spacingBox.Value;
        public string Prefix => prefixBox.Text.Trim();

        public ItemsOptionsDialog(double spacing, string prefix)
        {
            Text = "Item options";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new System.Drawing.Size(270, 130);
            AcceptButton = okButton;
            CancelButton = cancelButton;

            var spacingLabel = new Label { Text = "Row spacing:", AutoSize = true, Location = new System.Drawing.Point(16, 19) };
            spacingBox.Location = new System.Drawing.Point(110, 16); spacingBox.Size = new System.Drawing.Size(120, 23);
            spacingBox.Value = (decimal)Math.Clamp(spacing, 1, 100);
            var prefixLabel = new Label { Text = "Prefix:", AutoSize = true, Location = new System.Drawing.Point(16, 51) };
            prefixBox.Location = new System.Drawing.Point(110, 48); prefixBox.Size = new System.Drawing.Size(120, 23);
            prefixBox.Text = prefix;
            okButton.Location = new System.Drawing.Point(114, 90); okButton.Size = new System.Drawing.Size(75, 25);
            cancelButton.Location = new System.Drawing.Point(195, 90); cancelButton.Size = new System.Drawing.Size(75, 25);
            Controls.AddRange(new Control[] { spacingLabel, spacingBox, prefixLabel, prefixBox, okButton, cancelButton });
            okButton.Click += (_, _) =>
            {
                // a message box over the dialog, which stays open: a test for a window no click opened (it is no step of a recording)
                if (Prefix.IndexOf(' ') >= 0)
                {
                    MessageBox.Show(this, "The prefix cannot contain spaces.", "Item options", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    prefixBox.Focus();
                    return;
                }
                DialogResult = DialogResult.OK;
                Close();
            };
        }
    }
}
