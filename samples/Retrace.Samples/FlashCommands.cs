using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

namespace Retrace.Samples
{
    /// <summary>
    /// Dialogs that show up by themselves, and one that paints late (all use <see cref="InsertDialog"/>: a tree, image buttons, Add / Quick add):
    /// DRAWFLASH opens the dialog (a person picks a part and closes it), asks for an insertion point, then shows the same dialog again for about 150 ms and closes it itself (an add-in that reopens its dialog
    /// to refresh something), and draws the part's name at the point. DRAWFLASHLONG is the same with 450 ms. DRAWLATEDIALOG shows the dialog with an empty body and puts its controls in about 1.2 s later (the dialog is up at once, its body paints late)
    /// (a person picks a part, closes it, gives a point). DRAWFLASHQUICK is the plain dialog and a point (to answer the dialog by key within a fraction of a second).
    /// </summary>
    public class FlashCommands
    {
        [CommandMethod("DRAWFLASH")]
        public void DrawFlash() => Run(150, 0);

        [CommandMethod("DRAWFLASHLONG")]
        public void DrawFlashLong() => Run(450, 0);

        [CommandMethod("DRAWLATEDIALOG")]
        public void DrawLateDialog() => Run(0, 1200);

        /// <summary>A point, then the dialog flashes by itself (150 ms); no dialog a person answers: a test of it has no dialog step, so an unattended replay meets the flash unrecorded.</summary>
        [CommandMethod("DRAWFLASHONLY")]
        public void DrawFlashOnly() => FlashOnly(150);

        /// <summary>The same with a flash of 800 ms (a slower machine).</summary>
        [CommandMethod("DRAWFLASHONLYLONG")]
        public void DrawFlashOnlyLong() => FlashOnly(800);

        private static void FlashOnly(int ms)
        {
            var ed = Application.DocumentManager.MdiActiveDocument.Editor;
            if (ed.GetPoint(new PromptPointOptions("\nInsertion point: ")).Status != PromptStatus.OK) return;
            InsertDialog.AutoCloseMs = ms;
            try { using var again = new InsertDialog("Choose part"); Application.ShowModalDialog(again); }
            finally { InsertDialog.AutoCloseMs = 0; }
            ed.WriteMessage("\nDone.");
        }

        [CommandMethod("DRAWFLASHQUICK")]
        public void DrawFlashQuick() => Run(0, 0);

        private static void Run(int flashMs, int lateMs)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed = doc.Editor;
            string name;
            InsertDialog.LateMs = lateMs;
            try
            {
                using var dialog = new InsertDialog("Choose part");
                if (Application.ShowModalDialog(dialog) != System.Windows.Forms.DialogResult.OK) return;
                name = dialog.PartName;
            }
            finally { InsertDialog.LateMs = 0; }
            var pt = ed.GetPoint(new PromptPointOptions("\nInsertion point: "));
            if (pt.Status != PromptStatus.OK) return;
            if (flashMs > 0)
            {
                InsertDialog.AutoCloseMs = flashMs;
                try { using var again = new InsertDialog("Choose part"); Application.ShowModalDialog(again); }
                finally { InsertDialog.AutoCloseMs = 0; }
            }
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                var text = new DBText { TextString = name, Position = pt.Value, Height = 2.5 };
                ms.AppendEntity(text);
                tr.AddNewlyCreatedDBObject(text, true);
                tr.Commit();
            }
            ed.WriteMessage("\nPart placed: " + name);
        }
    }
}
