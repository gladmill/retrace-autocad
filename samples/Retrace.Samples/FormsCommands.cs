using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace Retrace.Samples
{
    /// <summary>DRAWFORMS: a main form with a tree of check boxes, a tree whose items open a sub form on a double-click (which has a button that opens a sub-sub form), and a button that acts on MouseDown.
    /// OK draws one TEXT that holds every choice, so a replay that skipped one makes a checkpoint fail.</summary>
    public class FormsCommands
    {
        [CommandMethod("DRAWFORMS")]
        public void DrawForms()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed = doc.Editor;

            using var dialog = new FormsDialog();
            if (Application.ShowModalDialog(dialog) != System.Windows.Forms.DialogResult.OK) return;
            var summary = dialog.Summary();

            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                var text = new DBText { TextString = summary, Position = new Point3d(0, 0, 0), Height = 2.5 };
                modelSpace.AppendEntity(text);
                tr.AddNewlyCreatedDBObject(text, true);
                tr.Commit();
            }
            ed.WriteMessage("\nDrawn: " + summary);
        }
    }
}
