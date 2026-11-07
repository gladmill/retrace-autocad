using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace Retrace.Samples
{
    /// <summary>DRAWMENU: a dialog with right-click context menus (a tree with a ContextMenuStrip and a sub form, a list with a Win32 popup menu), then TEXT lines drawn from what the menus did.</summary>
    public class MenuCommands
    {
        [CommandMethod("DRAWMENU")]
        public void DrawMenu()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed = doc.Editor;

            using var dialog = new MenuDialog();
            if (Application.ShowModalDialog(dialog) != System.Windows.Forms.DialogResult.OK) return;
            var lines = new[] { "part: " + dialog.Selected, "note: " + dialog.Note, "tools: " + dialog.ToolsCount, "files: " + dialog.FileList };

            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                for (int i = 0; i < lines.Length; i++)
                {
                    var text = new DBText { TextString = lines[i], Position = new Point3d(0, -i * 5, 0), Height = 2.5 };
                    modelSpace.AppendEntity(text);
                    tr.AddNewlyCreatedDBObject(text, true);
                }
                tr.Commit();
            }
            ed.WriteMessage("\n" + string.Join("; ", lines));
        }
    }
}
