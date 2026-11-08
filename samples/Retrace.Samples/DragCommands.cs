using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace Retrace.Samples
{
    /// <summary>
    /// DRAWDRAGPLACE: places a small rectangle (a polyline, 40 x 20) with a jig (<c>Editor.Drag</c>): an insertion point with a keyword
    /// ("Flip" mirrors the shape) and then a rotation. While the mouse moves the shape follows it; the recorder sees a point result for every sample.
    /// Esc cancels without drawing. DRAWDRAGPLACEGP asks for the same two answers with plain GetPoint and GetAngle (a rubber band, no jig): the control case.
    /// </summary>
    public class DragCommands
    {
        const string PointPrompt = "\nSpecify insertion point or [Flip]: ";
        const string AnglePrompt = "\nSpecify rotation angle: ";

        [CommandMethod("DRAWDRAGPLACE")]
        public void DrawDragPlace()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed = doc.Editor;
            var shape = Shape(Point3d.Origin, 0, false);
            var jig = new PlaceJig(shape);
            while (true)
            {
                jig.Phase = 0;
                var r = ed.Drag(jig);
                if (r.Status == PromptStatus.Keyword) { jig.Flip = !jig.Flip; continue; }
                if (r.Status != PromptStatus.OK) { shape.Dispose(); return; }
                break;
            }
            jig.Phase = 1;
            var r2 = ed.Drag(jig);
            if (r2.Status != PromptStatus.OK) { shape.Dispose(); return; }
            Add(doc, shape);
            ed.WriteMessage("\nShape placed at " + jig.Point.X.ToString("0.###") + "," + jig.Point.Y.ToString("0.###") + " rotated " + jig.Angle.ToString("0.####") + " rad.");
        }

        [CommandMethod("DRAWDRAGPLACEGP")]
        public void DrawDragPlaceGetPoint()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed = doc.Editor;
            var o = new PromptPointOptions("\nSpecify insertion point: ");
            o.Keywords.Add("Flip");
            bool flip = false;
            PromptPointResult p;
            while (true)
            {
                p = ed.GetPoint(o);
                if (p.Status == PromptStatus.Keyword) { flip = !flip; continue; }
                break;
            }
            if (p.Status != PromptStatus.OK) return;
            var a = ed.GetAngle(new PromptAngleOptions(AnglePrompt) { BasePoint = p.Value, UseBasePoint = true });
            if (a.Status != PromptStatus.OK) return;
            Add(doc, Shape(p.Value, a.Value, flip));
            ed.WriteMessage("\nShape placed at " + p.Value.X.ToString("0.###") + "," + p.Value.Y.ToString("0.###") + " rotated " + a.Value.ToString("0.####") + " rad.");
        }

        static Polyline Shape(Point3d at, double angle, bool flip)
        {
            var pl = new Polyline();
            for (int i = 0; i < 4; i++) pl.AddVertexAt(i, Point2d.Origin, 0, 0, 0);
            pl.Closed = true;
            Place(pl, at, angle, flip);
            return pl;
        }

        static void Place(Polyline pl, Point3d at, double angle, bool flip)
        {
            var corners = new[] { new Vector2d(0, 0), new Vector2d(40, 0), new Vector2d(40, 20), new Vector2d(0, 20) };
            for (int i = 0; i < 4; i++)
            {
                var c = corners[i];
                if (flip) c = new Vector2d(c.X, -c.Y);
                pl.SetPointAt(i, new Point2d(at.X, at.Y) + c.RotateBy(angle));
            }
        }

        static void Add(Autodesk.AutoCAD.ApplicationServices.Document doc, Polyline pl)
        {
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                ms.AppendEntity(pl);
                tr.AddNewlyCreatedDBObject(pl, true);
                tr.Commit();
            }
        }

        sealed class PlaceJig : EntityJig
        {
            public int Phase;
            public bool Flip;
            public Point3d Point = Point3d.Origin;
            public double Angle;

            public PlaceJig(Polyline pl) : base(pl) { }

            protected override SamplerStatus Sampler(JigPrompts prompts)
            {
                if (Phase == 0)
                {
                    var o = new JigPromptPointOptions();
                    o.SetMessageAndKeywords(PointPrompt, "Flip");
                    o.UserInputControls = UserInputControls.Accept3dCoordinates;
                    var r = prompts.AcquirePoint(o);
                    if (r.Status == PromptStatus.Keyword) return SamplerStatus.OK;
                    if (r.Status != PromptStatus.OK) return SamplerStatus.Cancel;
                    if (r.Value.IsEqualTo(Point)) return SamplerStatus.NoChange;
                    Point = r.Value;
                    return SamplerStatus.OK;
                }
                var ao = new JigPromptAngleOptions(AnglePrompt) { BasePoint = Point, UseBasePoint = true };
                var ar = prompts.AcquireAngle(ao);
                if (ar.Status != PromptStatus.OK) return SamplerStatus.Cancel;
                if (Math.Abs(ar.Value - Angle) < 1e-9) return SamplerStatus.NoChange;
                Angle = ar.Value;
                return SamplerStatus.OK;
            }

            protected override bool Update()
            {
                Place((Polyline)Entity, Point, Angle, Flip);
                return true;
            }
        }
    }
}
