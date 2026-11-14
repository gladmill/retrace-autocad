using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace Retrace.Samples
{
    /// <summary>DRAWINSERT: a dialog (tree, image buttons, Add / Quick add) that closes with a button, then the prompts "Insertion point:" and "Rotation angle &lt;0&gt;:", then a TEXT with the part's name there.</summary>
    public class InsertCommands
    {
        [CommandMethod("DRAWINSERT")]
        public void DrawInsert() => Run(low: false);

        /// <summary>The same, with the dialog opened partly below the screen.</summary>
        [CommandMethod("DRAWINSERTLOW")]
        public void DrawInsertLow() => Run(low: true);

        /// <summary>Two dialogs in one command: a parts dialog, the prompt "Insertion point:", a second dialog ("Insert part, finish"), then "Rotation angle:". The prompts between and after the dialogs are what a Tdoc step order is checked with.</summary>
        [CommandMethod("DRAWINSERTTWO")]
        public void DrawInsertTwo()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed = doc.Editor;
            string name;
            using (var first = new InsertDialog())
            {
                if (Application.ShowModalDialog(first) != System.Windows.Forms.DialogResult.OK) return;
                name = first.PartName;
            }
            var pt = ed.GetPoint(new PromptPointOptions("\nInsertion point: "));
            if (pt.Status != PromptStatus.OK) return;
            using (var second = new InsertDialog("Insert part, finish"))
            {
                if (Application.ShowModalDialog(second) != System.Windows.Forms.DialogResult.OK) return;
                name += "/" + second.PartName;
            }
            var ang = ed.GetAngle(new PromptAngleOptions("\nRotation angle: ") { AllowNone = true, DefaultValue = 0, UseDefaultValue = true });
            if (ang.Status != PromptStatus.OK && ang.Status != PromptStatus.None) return;
            Draw(doc, name, pt.Value, ang.Status == PromptStatus.OK ? ang.Value : 0);
        }

        private static void Run(bool low)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed = doc.Editor;
            string name;
            InsertDialog.Low = low;
            try
            {
                using var dialog = new InsertDialog();
                if (Application.ShowModalDialog(dialog) != System.Windows.Forms.DialogResult.OK) return;
                name = dialog.PartName;
            }
            finally { InsertDialog.Low = false; }

            var pt = ed.GetPoint(new PromptPointOptions("\nInsertion point: "));
            if (pt.Status != PromptStatus.OK) return;
            var ang = ed.GetAngle(new PromptAngleOptions("\nRotation angle: ") { AllowNone = true, DefaultValue = 0, UseDefaultValue = true });
            if (ang.Status != PromptStatus.OK && ang.Status != PromptStatus.None) return;
            Draw(doc, name, pt.Value, ang.Status == PromptStatus.OK ? ang.Value : 0);
        }

        private static void Draw(Autodesk.AutoCAD.ApplicationServices.Document doc, string name, Point3d at, double rot)
        {
            var ed = doc.Editor;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                var text = new DBText { TextString = name, Position = at, Height = 2.5, Rotation = rot };
                ms.AppendEntity(text);
                tr.AddNewlyCreatedDBObject(text, true);
                tr.Commit();
            }
            ed.WriteMessage("\nPart inserted: " + name);
        }
    }
}
