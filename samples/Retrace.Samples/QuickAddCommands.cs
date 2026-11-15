using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace Retrace.Samples
{
    /// <summary>DRAWQUICKADD: a modal dialog with a tree on the left, a large-icon list in the middle that is EMPTY until a tree item is selected, and push buttons on the right
    /// (<b>Place</b> closes the dialog, <b>Close</b> too); then an insertion point and a rotation are asked for. A test for the pictures of a recording's test document: one after the tree
    /// click (the list filled), one after the list click, one at the button. The tree and the list carry no label, so they are "#id" in a recording (steps.md says "the tree" / "the list").</summary>
    public class QuickAddCommands
    {
        [CommandMethod("DRAWQUICKADD")]
        public void DrawQuickAdd()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed = doc.Editor;

            string chosen;
            using (var dialog = new QuickAddDialog())
            {
                Application.ShowModalDialog(dialog);
                if (!dialog.Placed || dialog.Chosen.Length == 0) { ed.WriteMessage("\nNothing chosen."); return; }
                chosen = dialog.Chosen;
            }

            var pt = ed.GetPoint("\nSpecify insertion point: ");
            if (pt.Status != PromptStatus.OK) return;
            var ang = ed.GetAngle(new PromptAngleOptions("\nSpecify rotation angle <0>: ") { AllowNone = true, DefaultValue = 0, UseDefaultValue = true });
            if (ang.Status != PromptStatus.OK) return;

            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                var text = new DBText { TextString = chosen, Position = pt.Value, Height = 2.5, Rotation = ang.Value };
                modelSpace.AppendEntity(text);
                tr.AddNewlyCreatedDBObject(text, true);
                tr.Commit();
            }
            ed.WriteMessage("\n" + chosen + " placed.");
        }
    }
}
