using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace Retrace.Samples
{
    /// <summary>A command for checking the zoom to the last created object on objects whose GeometricExtents is point-like or fails: it adds one such entity at the given point and writes what
    /// GeometricExtents, Bounds and a temporary Explode say about it to the command line. Nothing here is a stand-in for a third-party custom entity (none can be made from .NET).</summary>
    public class OddExtentsSample
    {
        [CommandMethod("DRAWODDEXT")]
        public void DrawOddExtents()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed = doc.Editor;
            var kindOptions = new PromptKeywordOptions("\nKind [ZeroLine/EmptyRef/EmptyText/Xline]: ", "ZeroLine EmptyRef EmptyText Xline") { AllowNone = false };
            var kind = ed.GetKeywords(kindOptions);
            if (kind.Status != PromptStatus.OK) return;
            var pt = ed.GetPoint("\nPoint: ");
            if (pt.Status != PromptStatus.OK) return;

            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                Entity ent = kind.StringResult switch
                {
                    "ZeroLine" => new Line(pt.Value, pt.Value),
                    "EmptyRef" => new BlockReference(pt.Value, EmptyBlock(doc.Database, tr)),
                    "EmptyText" => new MText { Location = pt.Value, Contents = "" },
                    _ => new Xline { BasePoint = pt.Value, UnitDir = Vector3d.XAxis },
                };
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                ms.AppendEntity(ent);
                tr.AddNewlyCreatedDBObject(ent, true);
                ed.WriteMessage("\n" + kind.StringResult + " " + ent.GetRXClass().Name + ": GeometricExtents " + Try(() => Fmt(ent.GeometricExtents))
                    + "; Bounds " + Try(() => ent.Bounds is { } b ? Fmt(b) : "none") + "; Explode " + Try(() => { using var c = new DBObjectCollection(); ent.Explode(c); var n = c.Count; foreach (DBObject o in c) o.Dispose(); return n + " pieces"; }));
                tr.Commit();
            }
        }

        static ObjectId EmptyBlock(Database db, Transaction tr)
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            if (bt.Has("ODDEMPTY")) return bt["ODDEMPTY"];
            bt.UpgradeOpen();
            var btr = new BlockTableRecord { Name = "ODDEMPTY" };
            var id = bt.Add(btr);
            tr.AddNewlyCreatedDBObject(btr, true);
            return id;
        }

        static string Fmt(Extents3d e) => e.MinPoint.X.ToString("0.###") + "," + e.MinPoint.Y.ToString("0.###") + " to " + e.MaxPoint.X.ToString("0.###") + "," + e.MaxPoint.Y.ToString("0.###");

        static string Try(Func<string> f)
        {
            try { return f(); } catch (System.Exception ex) { return "threw " + ex.GetType().Name + " (" + ex.Message + ")"; }
        }
    }
}
