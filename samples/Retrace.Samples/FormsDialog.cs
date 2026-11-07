using System.Drawing;
using System.Windows.Forms;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Retrace.Samples
{
    /// <summary>
    /// The main form of DRAWFORMS, a test for trees and sub forms opened from a main form:
    /// <list type="bullet">
    /// <item><c>CheckTree</c> (label "Layers"): a TreeView with check boxes and nodes that are filled when they are opened (<see cref="PartCatalog"/>). The set of ticked items is kept by
    /// <c>AfterCheck</c> (not read from the nodes at the end), so an item ticked without telling the form is not in the summary.</item>
    /// <item><c>ItemTree</c> (label "Items"): double-click an item and a sub form opens (<see cref="PartForm"/>: a quantity, and a Finish... button that opens <see cref="FinishForm"/>).</item>
    /// <item><b>Notes...</b> opens <see cref="NotesForm"/> when the mouse button goes DOWN on it (MouseDown, there is no Click handler).</item>
    /// </list>
    /// OK closes the form; <see cref="Summary"/> holds every choice.
    /// </summary>
    public class FormsDialog : Form
    {
        private readonly TreeView checkTree = new()
        {
            Name = "CheckTree", AccessibleName = "Layers", CheckBoxes = true, HideSelection = false, Location = new Point(16, 32), Size = new Size(200, 180), TabIndex = 1,
        };
        private readonly TreeView itemTree = new()
        {
            Name = "ItemTree", AccessibleName = "Items", HideSelection = false, Location = new Point(232, 32), Size = new Size(200, 180), TabIndex = 2,
        };
        private readonly Button notesButton = new() { Name = "notesButton", Text = "Notes...", Location = new Point(16, 224), Size = new Size(100, 27), TabIndex = 3 };
        private readonly Label status = new() { Name = "StatusLabel", AutoSize = true, Location = new Point(130, 230), Text = "Notes: (none)" };
        private readonly Button ok = new() { Name = "okButton", Text = "OK", Location = new Point(276, 268), Size = new Size(75, 27), TabIndex = 4 };
        private readonly Button cancel = new() { Name = "cancelButton", Text = "Cancel", Location = new Point(357, 268), Size = new Size(75, 27), TabIndex = 5, DialogResult = DialogResult.Cancel };

        private readonly SortedSet<string> ticked = new(StringComparer.Ordinal);
        private readonly SortedDictionary<string, string> parts = new(StringComparer.Ordinal);
        private string notes = "";

        public FormsDialog()
        {
            Text = "Assembly";
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(448, 310);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(new Label { Name = "layersLabel", AutoSize = true, Location = new Point(16, 12), Text = "&Layers:" });
            Controls.Add(new Label { Name = "itemsLabel", AutoSize = true, Location = new Point(232, 12), Text = "&Items:" });
            Controls.AddRange(new Control[] { checkTree, itemTree, notesButton, status, ok, cancel });

            PartCatalog.Fill(checkTree);
            checkTree.AfterCheck += (_, e) =>
            {
                if (e.Node == null) return;
                if (e.Node.Checked) ticked.Add(e.Node.FullPath); else ticked.Remove(e.Node.FullPath);
            };

            // Hammer starts ticked (after the handler is in: it is in the set), so that a recording can untick an item as well as tick one
            checkTree.Nodes[1].Nodes[0].Checked = true;

            var housing = itemTree.Nodes.Add("Housing");
            housing.Nodes.Add("Lid");
            housing.Nodes.Add("Base");
            itemTree.Nodes.Add("Bracket");
            itemTree.Nodes.Add("Plate");
            itemTree.NodeMouseDoubleClick += (_, e) => OpenPart(e.Node?.FullPath ?? "");

            // acts on the mouse button going down: there is no Click handler
            notesButton.MouseDown += (_, _) => OpenNotes();
            ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        }

        private void OpenPart(string name)
        {
            if (name.Length == 0) return;
            parts.TryGetValue(name, out var before);
            using var form = new PartForm(name, before ?? "");
            if (Application.ShowModalDialog(this, form, false) == DialogResult.OK) parts[name] = form.Result;
        }

        private void OpenNotes()
        {
            using var form = new NotesForm(notes);
            if (Application.ShowModalDialog(this, form, false) != DialogResult.OK) return;
            notes = form.Notes;
            status.Text = "Notes: " + (notes.Length == 0 ? "(none)" : notes);
        }

        /// <summary>Every choice in one line: the ticked items, the result of each sub form, the notes.</summary>
        public string Summary() =>
            "Checked: " + (ticked.Count == 0 ? "-" : string.Join(", ", ticked)) + " | Parts: " + (parts.Count == 0 ? "-" : string.Join(", ", parts.Select(p => p.Key + "=" + p.Value)))
            + " | Notes: " + (notes.Length == 0 ? "-" : notes);
    }

    /// <summary>The sub form of a double-click on an item: a quantity, a Finish... button that opens the sub-sub form, OK / Cancel. Its result is "qty/finish".</summary>
    public class PartForm : Form
    {
        private readonly TextBox qty = new() { Name = "qtyBox", AccessibleName = "Quantity", Text = "1", Location = new Point(100, 18), Size = new Size(80, 23), TabIndex = 1 };
        // Weight reformats to three decimals when the focus leaves it (a Leave handler): as the mouse goes down on a button the field changes under the click, which the recording must hold as the click saw it
        private readonly TextBox weight = new() { Name = "weightBox", AccessibleName = "Weight", Location = new Point(232, 18), Size = new Size(56, 23), TabIndex = 5 };
        private readonly Button finishButton = new() { Name = "finishButton", Text = "Finish...", Location = new Point(16, 56), Size = new Size(100, 27), TabIndex = 2 };
        private readonly Label finishLabel = new() { Name = "FinishLabel", AutoSize = true, Location = new Point(130, 62), Text = "Finish: (none)" };
        private readonly Button ok = new() { Name = "okButton", Text = "OK", Location = new Point(132, 104), Size = new Size(75, 27), TabIndex = 3 };
        private readonly Button cancel = new() { Name = "cancelButton", Text = "Cancel", Location = new Point(213, 104), Size = new Size(75, 27), TabIndex = 4, DialogResult = DialogResult.Cancel };
        private string finish = "";

        public PartForm(string part, string before)
        {
            Text = "Part " + part.Replace('\\', '>');
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(304, 144);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(new Label { Name = "qtyLabel", AutoSize = true, Location = new Point(16, 22), Text = "&Quantity:" });
            Controls.Add(new Label { Name = "weightLabel", AutoSize = true, Location = new Point(190, 22), Text = "&Weight:" });
            Controls.AddRange(new Control[] { qty, weight, finishButton, finishLabel, ok, cancel });
            weight.Leave += (_, _) => { if (double.TryParse(weight.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w)) weight.Text = w.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture); };
            if (before.Contains('/')) { qty.Text = before[..before.IndexOf('/')]; finish = before[(before.IndexOf('/') + 1)..]; ShowFinish(); }
            finishButton.Click += (_, _) =>
            {
                using var form = new FinishForm(finish);
                if (Autodesk.AutoCAD.ApplicationServices.Application.ShowModalDialog(this, form, false) != DialogResult.OK) return;
                finish = form.Coating;
                ShowFinish();
            };
            ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        }

        private void ShowFinish() => finishLabel.Text = "Finish: " + (finish.Length == 0 ? "(none)" : finish);

        public string Result => qty.Text.Trim() + "/" + (finish.Length == 0 ? "-" : finish) + (weight.Text.Trim().Length == 0 ? "" : "/w" + weight.Text.Trim());
    }

    /// <summary>The sub-sub form: a coating, OK / Cancel.</summary>
    public class FinishForm : Form
    {
        private readonly TextBox coating = new() { Name = "coatingBox", AccessibleName = "Coating", Location = new Point(100, 18), Size = new Size(160, 23), TabIndex = 1 };
        private readonly Button ok = new() { Name = "okButton", Text = "OK", Location = new Point(104, 60), Size = new Size(75, 27), TabIndex = 2 };
        private readonly Button cancel = new() { Name = "cancelButton", Text = "Cancel", Location = new Point(185, 60), Size = new Size(75, 27), TabIndex = 3, DialogResult = DialogResult.Cancel };

        public FinishForm(string before)
        {
            Text = "Finish";
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(280, 100);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(new Label { Name = "coatingLabel", AutoSize = true, Location = new Point(16, 22), Text = "&Coating:" });
            Controls.AddRange(new Control[] { coating, ok, cancel });
            coating.Text = before;
            ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        }

        public string Coating => coating.Text.Trim();
    }

    /// <summary>The form of the Notes... button: one text.</summary>
    public class NotesForm : Form
    {
        private readonly TextBox text = new() { Name = "notesBox", AccessibleName = "Notes", Location = new Point(16, 36), Size = new Size(248, 23), TabIndex = 1 };
        private readonly Button ok = new() { Name = "okButton", Text = "OK", Location = new Point(108, 76), Size = new Size(75, 27), TabIndex = 2 };
        private readonly Button cancel = new() { Name = "cancelButton", Text = "Cancel", Location = new Point(189, 76), Size = new Size(75, 27), TabIndex = 3, DialogResult = DialogResult.Cancel };

        public NotesForm(string before)
        {
            Text = "Notes";
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(280, 116);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(new Label { Name = "notesLabel", AutoSize = true, Location = new Point(16, 14), Text = "&Notes:" });
            Controls.AddRange(new Control[] { text, ok, cancel });
            text.Text = before;
            ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        }

        public string Notes => text.Text.Trim();
    }
}
