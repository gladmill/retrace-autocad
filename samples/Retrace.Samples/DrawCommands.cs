using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace Retrace.Samples
{
    /// <summary>Sample commands with plain command-line prompts. Esc at any prompt cancels without drawing.</summary>
    public class DrawCommands
    {
        private static double lastRadius = 5.0;
        private static double lastHeight = 2.5;
        private static string lastText = "Label";

        [CommandMethod("DRAWLINE")]
        public void DrawLine()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed = doc.Editor;

            var startResult = ed.GetPoint("\nStart point: ");
            if (startResult.Status != PromptStatus.OK) return;

            var endOptions = new PromptPointOptions("\nEnd point: ")
            {
                BasePoint = startResult.Value,
                UseBasePoint = true,
            };
            var endResult = ed.GetPoint(endOptions);
            if (endResult.Status != PromptStatus.OK) return;

            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var line = new Line(startResult.Value, endResult.Value);
                AddToModelSpace(doc.Database, tr, line);
                tr.Commit();
            }
            ed.WriteMessage("\nLine drawn.");
        }

        [CommandMethod("DRAWCIRCLE")]
        public void DrawCircle()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed = doc.Editor;

            var centerResult = ed.GetPoint("\nCenter point: ");
            if (centerResult.Status != PromptStatus.OK) return;

            var radiusOptions = new PromptDistanceOptions("\nRadius <" + lastRadius + ">: ")
            {
                BasePoint = centerResult.Value,
                UseBasePoint = true,
                AllowNone = true,
                AllowZero = false,
                AllowNegative = false,
            };
            var radiusResult = ed.GetDistance(radiusOptions);
            double radius;
            if (radiusResult.Status == PromptStatus.None) radius = lastRadius;
            else if (radiusResult.Status == PromptStatus.OK) radius = radiusResult.Value;
            else return;

            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var circle = new Circle(centerResult.Value, Vector3d.ZAxis, radius);
                AddToModelSpace(doc.Database, tr, circle);
                tr.Commit();
            }
            lastRadius = radius;
            ed.WriteMessage("\nCircle drawn, radius " + radius + ".");
        }

        [CommandMethod("DRAWRECT")]
        public void DrawRect()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed = doc.Editor;

            var firstResult = ed.GetPoint("\nFirst corner: ");
            if (firstResult.Status != PromptStatus.OK) return;

            var cornerOptions = new PromptCornerOptions("\nOpposite corner: ", firstResult.Value);
            var cornerResult = ed.GetCorner(cornerOptions);
            if (cornerResult.Status != PromptStatus.OK) return;

            Point3d a = firstResult.Value;
            Point3d b = cornerResult.Value;
            if (Math.Abs(a.X - b.X) < Tolerance.Global.EqualPoint || Math.Abs(a.Y - b.Y) < Tolerance.Global.EqualPoint)
            {
                ed.WriteMessage("\nThe two corners must differ in X and Y. Nothing drawn.");
                return;
            }

            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var rect = new Polyline();
                rect.AddVertexAt(0, new Point2d(a.X, a.Y), 0, 0, 0);
                rect.AddVertexAt(1, new Point2d(b.X, a.Y), 0, 0, 0);
                rect.AddVertexAt(2, new Point2d(b.X, b.Y), 0, 0, 0);
                rect.AddVertexAt(3, new Point2d(a.X, b.Y), 0, 0, 0);
                rect.Closed = true;
                AddToModelSpace(doc.Database, tr, rect);
                tr.Commit();
            }
            ed.WriteMessage("\nRectangle drawn.");
        }

        [CommandMethod("DRAWLABEL")]
        public void DrawLabel()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed = doc.Editor;

            var textOptions = new PromptStringOptions("\nLabel text <" + lastText + ">: ")
            {
                AllowSpaces = true,
            };
            var textResult = ed.GetString(textOptions);
            if (textResult.Status != PromptStatus.OK) return;
            string text = string.IsNullOrWhiteSpace(textResult.StringResult) ? lastText : textResult.StringResult;

            var pointResult = ed.GetPoint("\nInsertion point: ");
            if (pointResult.Status != PromptStatus.OK) return;

            var heightOptions = new PromptDoubleOptions("\nText height <" + lastHeight + ">: ")
            {
                AllowNone = true,
                AllowZero = false,
                AllowNegative = false,
            };
            var heightResult = ed.GetDouble(heightOptions);
            double height;
            if (heightResult.Status == PromptStatus.None) height = lastHeight;
            else if (heightResult.Status == PromptStatus.OK) height = heightResult.Value;
            else return;

            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var label = new DBText
                {
                    TextString = text,
                    Position = pointResult.Value,
                    Height = height,
                };
                AddToModelSpace(doc.Database, tr, label);
                tr.Commit();
            }
            lastText = text;
            lastHeight = height;
            ed.WriteMessage("\nLabel added: " + text);
        }

        [CommandMethod("DRAWGRID")]
        public void DrawGrid()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed = doc.Editor;

            using var dialog = new GridDialog();
            var answer = Application.ShowModalDialog(dialog);
            if (answer != System.Windows.Forms.DialogResult.OK) return;

            int rows = dialog.Rows;
            int columns = dialog.Columns;
            double spacing = dialog.Spacing;
            string layerName = dialog.LayerName;

            var originResult = ed.GetPoint("\nLower-left point of the grid: ");
            if (originResult.Status != PromptStatus.OK) return;
            Point3d origin = originResult.Value;

            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var layers = (LayerTable)tr.GetObject(doc.Database.LayerTableId, OpenMode.ForRead);
                ObjectId layerId;
                if (layers.Has(layerName))
                {
                    layerId = layers[layerName];
                }
                else
                {
                    layers.UpgradeOpen();
                    var record = new LayerTableRecord { Name = layerName };
                    layerId = layers.Add(record);
                    tr.AddNewlyCreatedDBObject(record, true);
                }

                for (int r = 0; r < rows; r++)
                {
                    for (int c = 0; c < columns; c++)
                    {
                        var position = new Point3d(origin.X + c * spacing, origin.Y + r * spacing, origin.Z);
                        var point = new DBPoint(position) { LayerId = layerId };
                        AddToModelSpace(doc.Database, tr, point);
                    }
                }
                tr.Commit();
            }
            ed.WriteMessage("\nGrid drawn: " + rows + " x " + columns + " points on layer " + layerName + ".");
        }

        private static void AddToModelSpace(Database db, Transaction tr, Entity entity)
        {
            var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
            modelSpace.AppendEntity(entity);
            tr.AddNewlyCreatedDBObject(entity, true);
        }
    }
}
