using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcPane = Autodesk.AutoCAD.Windows.Pane;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using WinForms = System.Windows.Forms;

namespace Retrace.Samples
{
    /// <summary>
    /// The ways an add-in puts an item into AutoCAD's status bar (the bar below the command line), as small generic samples for the status bar click steps.
    /// STATUSBARADD adds four items (once; STATUSBARREMOVE takes them out): a pane "Sample TB: edit" whose click opens a modal dialog (Cancel; OK also queues a CIRCLE 10,0 3), a pane "Sample run" whose
    /// click runs a command (a circle on the layer StatusRun), a pane "Sample menu" whose click shows a context menu (Draw circle, Draw large circle: each draws on the layer StatusMenu),
    /// and a tray item (tooltip "Sample tray", icon only) whose click draws a circle on the layer StatusTray. The first pane's text flips to "Sample TB: view" with STATUSBARTOGGLE
    /// (the text of an item may change with state). STATUSBARHIDE shows or hides the first pane. STATUSBARDUMP prints what the status bar holds (text, tooltip, visibility per pane and tray item) to the command line.
    /// </summary>
    public class StatusBarCommands
    {
        private static AcPane? edit, run, menu;
        private static Autodesk.AutoCAD.Windows.TrayItem? tray;
        private static bool view;

        [CommandMethod("STATUSBARADD")]
        public void Add()
        {
            var bar = AcApp.StatusBar;
            var ed = AcApp.DocumentManager.MdiActiveDocument?.Editor;
            if (bar == null) { ed?.WriteMessage("\nNo status bar."); return; }
            if (edit != null) { ed?.WriteMessage("\nThe sample items are already there."); return; }
            // Style Command: a pane without it never raises MouseDown
            edit = new AcPane { Style = Autodesk.AutoCAD.Windows.PaneStyles.Command, Text = "Sample TB: edit", ToolTipText = "Edit the sample settings", Visible = true };
            run = new AcPane { Style = Autodesk.AutoCAD.Windows.PaneStyles.Command, Text = "Sample run", ToolTipText = "Draw a circle with a command", Visible = true };
            menu = new AcPane { Style = Autodesk.AutoCAD.Windows.PaneStyles.Command, Text = "Sample menu", ToolTipText = "Choose what to draw", Visible = true };
            edit.MouseDown += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) Application_ShowSettings(); };
            run.MouseDown += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) AcApp.DocumentManager.MdiActiveDocument?.SendStringToExecute("_.CIRCLE 0,0 5 ", true, false, false); };
            menu.MouseDown += (_, e) => ShowMenu();
            bar.Panes.Add(edit); bar.Panes.Add(run); bar.Panes.Add(menu);
            tray = new Autodesk.AutoCAD.Windows.TrayItem { ToolTipText = "Sample tray", Icon = System.Drawing.SystemIcons.Information, Visible = true };
            tray.MouseDown += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) SamplePalette.Draw((_, _) => new Autodesk.AutoCAD.DatabaseServices.Circle(new Point3d(30, 0, 0), Vector3d.ZAxis, 3), "StatusTray"); };
            bar.TrayItems.Add(tray);
            bar.Update();
            ed?.WriteMessage("\nSample status bar items added.");
        }

        [CommandMethod("STATUSBARREMOVE")]
        public void Remove()
        {
            var bar = AcApp.StatusBar;
            if (bar == null) return;
            foreach (var p in new[] { edit, run, menu }) if (p != null) bar.Panes.Remove(p);
            if (tray != null) bar.TrayItems.Remove(tray);
            edit = run = menu = null; tray = null;
            bar.Update();
        }

        [CommandMethod("STATUSBARTOGGLE")]
        public void Toggle()
        {
            if (edit == null) return;
            view = !view;
            edit.Text = view ? "Sample TB: view" : "Sample TB: edit";
            AcApp.StatusBar?.Update();
        }

        // turns the first pane off and on again (an item turned off in the status bar's Customization menu is not shown either)
        [CommandMethod("STATUSBARHIDE")]
        public void Hide()
        {
            if (edit == null) return;
            edit.Visible = !edit.Visible;
            AcApp.StatusBar?.Update();
        }

        [CommandMethod("STATUSBARDUMP")]
        public void Dump()
        {
            var ed = AcApp.DocumentManager.MdiActiveDocument?.Editor;
            var bar = AcApp.StatusBar;
            if (bar == null) { ed?.WriteMessage("\nNo status bar."); return; }
            int i = 0;
            foreach (AcPane p in bar.Panes) ed?.WriteMessage($"\npane {i++}: text='{p.Text}' tip='{p.ToolTipText}' visible={p.Visible} style={p.Style} icon={(p.Icon != null)}");
            i = 0;
            foreach (Autodesk.AutoCAD.Windows.TrayItem t in bar.TrayItems) ed?.WriteMessage($"\ntray {i++}: tip='{t.ToolTipText}' visible={t.Visible}");
            ed?.WriteMessage("\nbar window: " + bar.Window);
        }

        private static void Application_ShowSettings()
        {
            using var f = new WinForms.Form { Text = "Sample TB settings", Width = 300, Height = 160, StartPosition = WinForms.FormStartPosition.CenterParent, FormBorderStyle = WinForms.FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
            var ok = new WinForms.Button { Text = "OK", DialogResult = WinForms.DialogResult.OK, Left = 100, Top = 80, Width = 80 };
            var cancel = new WinForms.Button { Text = "Cancel", DialogResult = WinForms.DialogResult.Cancel, Left = 190, Top = 80, Width = 80 };
            f.Controls.Add(new WinForms.Label { Text = "Sample setting:", Left = 14, Top = 20, Width = 100 });
            f.Controls.Add(new WinForms.TextBox { Name = "SampleValue", Text = "1", Left = 120, Top = 16, Width = 140 });
            f.Controls.Add(ok); f.Controls.Add(cancel);
            f.AcceptButton = ok; f.CancelButton = cancel;
            // OK starts a command (queued, it runs once the dialog has closed): a status bar click whose dialog starts a command
            if (AcApp.ShowModalDialog(f) == WinForms.DialogResult.OK) AcApp.DocumentManager.MdiActiveDocument?.SendStringToExecute("_.CIRCLE 10,0 3 ", true, false, false);
        }

        private static void ShowMenu()
        {
            var m = new WinForms.ContextMenuStrip();
            m.Items.Add("Draw circle", null, (_, _) => SamplePalette.Draw((_, _) => new Autodesk.AutoCAD.DatabaseServices.Circle(new Point3d(60, 0, 0), Vector3d.ZAxis, 3), "StatusMenu"));
            m.Items.Add("Draw large circle", null, (_, _) => SamplePalette.Draw((_, _) => new Autodesk.AutoCAD.DatabaseServices.Circle(new Point3d(80, 0, 0), Vector3d.ZAxis, 6), "StatusMenu"));
            m.Show(WinForms.Cursor.Position);
        }
    }
}
