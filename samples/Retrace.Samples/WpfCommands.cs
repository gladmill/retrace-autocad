using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace Retrace.Samples
{
    /// <summary>DRAWWPF: a WPF dialog with buttons (Add, Details... which opens a WPF sub window whose More... opens a WinForms sub-sub dialog); DRAWMIX: a WinForms dialog whose Details... opens that WPF sub window.</summary>
    public class WpfCommands
    {
        [CommandMethod("DRAWWPF")]
        public void DrawWpf()
        {
            var window = new WpfItemsWindow();
            if (Application.ShowModalWindow(window) != true) return;
            var lines = window.Items.ToList();
            lines.Add("note " + (window.Note.Length == 0 ? "none" : window.Note) + " depth " + window.Depth);
            Draw(lines);
            Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage("\n" + lines.Count + " text(s) drawn.");
        }

        [CommandMethod("DRAWMIX")]
        public void DrawMix()
        {
            using var dialog = new WpfMixDialog();
            if (Application.ShowModalDialog(dialog) != System.Windows.Forms.DialogResult.OK) return;
            var lines = new List<string> { dialog.PlateTitle, "note " + (dialog.Note.Length == 0 ? "none" : dialog.Note) + " depth " + dialog.Depth };
            Draw(lines);
            Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage("\n" + lines.Count + " text(s) drawn.");
        }

        private static void Draw(List<string> lines)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                for (int i = 0; i < lines.Count; i++)
                {
                    var text = new DBText { TextString = lines[i], Position = new Point3d(0, -i * 5, 0), Height = 2.5 };
                    modelSpace.AppendEntity(text);
                    tr.AddNewlyCreatedDBObject(text, true);
                }
                tr.Commit();
            }
        }
    }
}
