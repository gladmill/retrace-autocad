using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using System.Drawing;
using System.Windows.Forms;

namespace Retrace.Samples
{
    /// <summary>
    /// Modal states a replay has to tell apart:
    /// DRAWHIDDENMODAL opens a modal form that is never visible (fully transparent, no frame, no size) with <c>Application.ShowModalDialog</c>, closes it by itself after about 2 s, and then runs the
    /// DRAWDRAGPLACE jig (insertion point, rotation): AutoCAD is in a modal state with no dialog window of its own, the shape of an add-in's hidden interaction. A recording holds a <c>modal_enter</c>
    /// with the main window in front and no dialog; the person's point and angle come after it.
    /// DRAWSLOWDIALOG opens the same hidden form, and about 0.8 s later a real dialog (a name and OK) on top of it: a dialog that shows late, which must still be recorded and filled in as a dialog.
    /// </summary>
    public class HiddenModalCommands
    {
        [CommandMethod("DRAWHIDDENMODAL")]
        public void DrawHiddenModal()
        {
            using (var hidden = new HiddenForm(2000, false)) Application.ShowModalDialog(hidden);
            new DragCommands().DrawDragPlace();
        }

        [CommandMethod("DRAWSLOWDIALOG")]
        public void DrawSlowDialog()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            string? name = null;
            using (var hidden = new HiddenForm(800, true)) { Application.ShowModalDialog(hidden); name = hidden.Name2; }
            if (string.IsNullOrEmpty(name)) { doc.Editor.WriteMessage("\nNo name. Nothing drawn."); return; }
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                var text = new DBText { TextString = name, Position = new Point3d(0, -60, 0), Height = 2.5 };
                ms.AppendEntity(text);
                tr.AddNewlyCreatedDBObject(text, true);
                tr.Commit();
            }
            doc.Editor.WriteMessage("\nText drawn: " + name);
        }

        /// <summary>A form that is never seen: opacity 0, no frame, no taskbar button, 1 x 1. It closes by itself after <c>ms</c>, or (<c>thenDialog</c>) opens a real dialog over itself at that time and closes with it.</summary>
        sealed class HiddenForm : Form
        {
            readonly System.Windows.Forms.Timer _timer = new();
            readonly bool _thenDialog;
            public string? Name2;

            public HiddenForm(int ms, bool thenDialog)
            {
                _thenDialog = thenDialog;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                Location = new Point(-32000, -32000);
                Size = new Size(1, 1);
                Opacity = 0;
                _timer.Interval = ms;
                _timer.Tick += (_, _) =>
                {
                    _timer.Stop();
                    if (_thenDialog)
                    {
                        using var d = new NameDialog();
                        if (d.ShowDialog(this) == DialogResult.OK) Name2 = d.NameText;
                    }
                    Close();
                };
            }

            protected override void OnShown(EventArgs e) { base.OnShown(e); _timer.Start(); }
            protected override void Dispose(bool disposing) { if (disposing) _timer.Dispose(); base.Dispose(disposing); }
        }

        /// <summary>A real dialog: a name box, OK and Cancel.</summary>
        sealed class NameDialog : Form
        {
            readonly TextBox _name = new() { Name = "txtName", Text = "item", Left = 20, Top = 40, Width = 220 };
            public string NameText => _name.Text;

            public NameDialog()
            {
                Text = "Slow name";
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
                StartPosition = FormStartPosition.CenterScreen;
                ClientSize = new Size(260, 120);
                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 80, Top = 80 };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 165, Top = 80 };
                Controls.Add(new Label { Text = "Name", Left = 20, Top = 20, AutoSize = true });
                Controls.Add(_name); Controls.Add(ok); Controls.Add(cancel);
                AcceptButton = ok; CancelButton = cancel;
            }
        }
    }
}
