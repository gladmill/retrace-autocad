using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using WinForms = System.Windows.Forms;

namespace Retrace.Samples
{
    /// <summary>
    /// A command that returns at once and shows its palette set LATE, as some add-ins do (they show or expand it from an Idle handler or a timer): DRAWLATE300, DRAWLATE1000 and DRAWLATE2000 make the set visible that many ms after the command
    /// ended, floating at the left of the window; DRAWLATEDOCK300 / DRAWLATEDOCK1000 / DRAWLATEDOCK2000 dock it at the left. The set (Retrace Late Set, one page "Late") is made once, hidden, and shown by the timer; DRAWLATECLOSE hides it again.
    /// A ribbon or Quick Access Toolbar button that runs one of these has no palette on screen when the command ends: the end picture of Retrace's recording is taken too early for it.
    /// </summary>
    public class LatePaletteCommands
    {
        private static PaletteSet? set;
        private static WinForms.Timer? timer;

        [CommandMethod("DRAWLATE300")] public void Late300() { Run(300, false); }
        [CommandMethod("DRAWLATE1000")] public void Late1000() { Run(1000, false); }
        [CommandMethod("DRAWLATE1300")] public void Late1300() { Run(1300, false); }   // just after the end picture (taken about 1.1 s after the click): the retake runs while the first picture is still being written
        [CommandMethod("DRAWLATE2000")] public void Late2000() { Run(2000, false); }
        [CommandMethod("DRAWLATE4000")] public void Late4000() { Run(4000, false); }   // after the 3 s limit of the recorder: nothing is caught
        [CommandMethod("DRAWLATEDOCK300")] public void LateDock300() { Run(300, true); }
        [CommandMethod("DRAWLATEDOCK1000")] public void LateDock1000() { Run(1000, true); }
        [CommandMethod("DRAWLATEDOCK2000")] public void LateDock2000() { Run(2000, true); }
        [CommandMethod("DRAWLATENOTHING")] public void Nothing() { }   // a command that opens nothing (for hovering an edge tab right after it)
        [CommandMethod("DRAWLATECLOSE")] public void Close() { timer?.Stop(); if (set != null) set.Visible = false; }

        // The set anchored at the left edge with Auto-hide on (retracted: only its edge tab shows). DRAWLATEANCHOR makes it (type it; no recording needed). DRAWLATEEXPAND1500 / 2500 then expand it from a timer that many ms
        // after the command ended, with the mouse left where the click was: the case of an add-in whose set is anchored before the click and shows itself after it.
        [CommandMethod("DRAWLATEANCHOR")]
        public void Anchor()
        {
            Make();
            set!.Visible = true;
            set.AutoRollUp = true;   // only valid once the set shows, and before it is docked (docking a fresh set first made it throw "operation not valid")
            if (set.Dock == DockSides.None) set.Dock = DockSides.Left;
        }
        [CommandMethod("DRAWLATEEXPAND1500")] public void Expand1500() { Expand(1500); }
        [CommandMethod("DRAWLATEEXPAND2500")] public void Expand2500() { Expand(2500); }

        // A floating set that is on the screen but far outside it (like a root window at -3815) until a timer brings it back 2 s after the command ended.
        [CommandMethod("DRAWLATEOFFSCREEN2000")]
        public void Offscreen2000()
        {
            timer?.Stop();
            Make();
            set!.Visible = true;
            set.Location = new System.Drawing.Point(-3815, 182);
            var t = new WinForms.Timer { Interval = 2000 };
            t.Tick += (_, _) => { t.Stop(); if (set != null) set.Location = new System.Drawing.Point(60, 220); };
            timer = t; t.Start();
        }

        private static void Make()
        {
            if (set != null) return;
            var s = new PaletteSet("Retrace Late Set", new System.Guid("2f6c9d14-7a30-4b58-8e1c-3d5a70b9c642"));
            s.Style = PaletteSetStyles.ShowCloseButton | PaletteSetStyles.ShowAutoHideButton;
            s.Size = new System.Drawing.Size(360, 400);
            s.Add("Late", new CanvasControl("PaneLate"));
            set = s;
        }

        private static void Expand(int delayMs)
        {
            timer?.Stop();
            var t = new WinForms.Timer { Interval = delayMs };
            t.Tick += (_, _) => { t.Stop(); if (set == null) return; set.Visible = true; set.AutoRollUp = false; };
            timer = t; t.Start();
        }

        private static void Run(int delayMs, bool dock)
        {
            timer?.Stop();
            if (set != null) set.Visible = false;
            Make();
            var t = new WinForms.Timer { Interval = delayMs };
            t.Tick += (_, _) =>
            {
                t.Stop();
                if (set == null) return;
                set.Visible = true;
                if (dock) { if (set.Dock == DockSides.None) set.Dock = DockSides.Left; }
                else set.Location = new System.Drawing.Point(60, 220);
            };
            timer = t;
            t.Start();   // the command returns now; the set shows delayMs after it ended
        }
    }
}
