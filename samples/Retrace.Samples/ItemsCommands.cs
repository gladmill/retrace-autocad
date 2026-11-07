using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace Retrace.Samples
{
    /// <summary>DRAWITEMS: a dialog with buttons in it (Add, Options... which opens a nested dialog) and a label that counts, then one TEXT per item.</summary>
    public class ItemsCommands
    {
        [CommandMethod("DRAWITEMS")]
        public void DrawItems()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed = doc.Editor;

            using var dialog = new ItemsDialog();
            if (Application.ShowModalDialog(dialog) != System.Windows.Forms.DialogResult.OK) return;
            if (dialog.Items.Count == 0) { ed.WriteMessage("\nNo items. Nothing drawn."); return; }

            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                for (int i = 0; i < dialog.Items.Count; i++)
                {
                    var text = new DBText
                    {
                        TextString = dialog.Prefix + dialog.Items[i],
                        Position = new Point3d(0, -i * dialog.Spacing, 0),
                        Height = 2.5,
                    };
                    modelSpace.AppendEntity(text);
                    tr.AddNewlyCreatedDBObject(text, true);
                }
                tr.Commit();
            }
            ed.WriteMessage("\n" + dialog.Items.Count + " text(s) drawn, spacing " + dialog.Spacing + ".");
        }
    }
}
