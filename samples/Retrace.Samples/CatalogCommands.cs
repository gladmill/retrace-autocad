using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace Retrace.Samples
{
    /// <summary>
    /// DRAWCATALOG: a list view as a dialog field in a frame-like modal window of a class that is not #32770. First the window (<see cref="CatalogFrame"/>, a plain Win32 window made with CreateWindowEx and run with its own message loop: window class <c>AfxFrameOrView140u</c>, a child view window with a tab control, an icon-mode list view with
    /// view-mode buttons, Add) whose Add closes it; at once the second dialog opens, a real <c>#32770</c> property sheet "Order details" (<see cref="PropSheet"/>: tabs General, Size, Options with
    /// Name, Note, Width, Height, Mark the center, Radius). Two sequential modals, not nested. OK draws a TEXT with the chosen products and the order's name and size.
    /// </summary>
    public class CatalogCommands
    {
        [CommandMethod("DRAWCATALOG")]
        public void DrawCatalog()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed = doc.Editor;

            var frame = new CatalogFrame();
            if (!frame.Show(Application.MainWindow.Handle)) return;
            var products = string.Join(", ", frame.Chosen);

            // Add closed the frame: the order dialog opens right away
            var sheet = new PropSheet { PlateName = products };
            if (!sheet.Show(Application.MainWindow.Handle, "Order details")) return;
            var line = $"{products} / {sheet.PlateName} / {sheet.Width:0.###} x {sheet.Height:0.###}" + (frame.Note.Length > 0 ? " / " + frame.Note : "") + (frame.Marked.Length > 0 ? " / marked " + frame.Marked : "");

            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                var text = new DBText { TextString = line, Position = new Point3d(0, 0, 0), Height = 2.5 };
                modelSpace.AppendEntity(text);
                tr.AddNewlyCreatedDBObject(text, true);
                tr.Commit();
            }
            ed.WriteMessage("\n" + line);
        }
    }
}
