using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace Retrace.Samples
{
    /// <summary>DRAWTABS: a dialog with tabs (a field or two on each page), then a plate drawn from every value of every page.</summary>
    public class TabsCommands
    {
        [CommandMethod("DRAWTABS")]
        public void DrawTabs()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed = doc.Editor;

            using var dialog = new TabsDialog();
            if (Application.ShowModalDialog(dialog) != System.Windows.Forms.DialogResult.OK) return;
            double w = dialog.PlateWidth, h = dialog.PlateHeight;

            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                void Add(Entity entity) { modelSpace.AppendEntity(entity); tr.AddNewlyCreatedDBObject(entity, true); }

                // Size page: the outline is width x height. General page: the name below it. Style page: a circle at the center, its radius from the combo box
                var outline = new Polyline();
                outline.AddVertexAt(0, new Point2d(0, 0), 0, 0, 0);
                outline.AddVertexAt(1, new Point2d(w, 0), 0, 0, 0);
                outline.AddVertexAt(2, new Point2d(w, h), 0, 0, 0);
                outline.AddVertexAt(3, new Point2d(0, h), 0, 0, 0);
                outline.Closed = true;
                Add(outline);
                Add(new DBText { TextString = dialog.PlateName, Position = new Point3d(0, -4, 0), Height = 2.5 });
                if (dialog.MarkCenter) Add(new Circle(new Point3d(w / 2, h / 2, 0), Vector3d.ZAxis, dialog.MarkRadius));
                tr.Commit();
            }
            ed.WriteMessage("\nPlate " + dialog.PlateName + " drawn: " + w + " x " + h + (dialog.MarkCenter ? ", center marked." : "."));
        }
    }
}
