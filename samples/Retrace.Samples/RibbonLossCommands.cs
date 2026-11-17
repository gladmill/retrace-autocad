using Autodesk.AutoCAD.Runtime;
using Autodesk.Windows;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Retrace.Samples
{
    /// <summary>
    /// Test-only commands that lose the Retrace ribbon tab in the ways the add-in must repair (see the README's "The Retrace tab is missing"):
    /// RIBBONLOSS removes the tab from the ribbon (Tabs.Remove) and prints, from the next Idle ticks, how many ms it took until a Retrace tab was back
    /// (the time of the add-in's repair; the printout itself is part of the test). RIBBONLOSSMANY [n] removes it again every time it is back, n times
    /// (default 8): shows the loop guard. RIBBONLOSSHIDE only sets IsVisible = false, like unticking Retrace in the ribbon's Show Tabs menu (the add-in
    /// leaves that alone; RETRACERIBBON shows the tab again). RIBBONLOSSDUP adds a second Retrace-id tab (RETRACERIBBON leaves one).
    /// </summary>
    public class RibbonLossCommands
    {
        const string TabId = "RETRACE_TAB";
        static long _removedAt;
        static int _left;
        static int _done;
        static bool _hooked;

        static RibbonControl? Rc => ComponentManager.Ribbon;

        static bool Remove()
        {
            var rc = Rc;
            var tab = rc?.Tabs.FirstOrDefault(t => t.Id == TabId);
            if (rc == null || tab == null) return false;
            _removedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            rc.Tabs.Remove(tab);
            return true;
        }

        static void Hook() { if (_hooked) return; _hooked = true; Application.Idle += OnIdle; }

        static void Unhook() { if (!_hooked) return; _hooked = false; Application.Idle -= OnIdle; }

        static void OnIdle(object? s, EventArgs e)
        {
            try
            {
                var rc = Rc;
                if (rc == null || _removedAt == 0 || !rc.Tabs.Any(t => t.Id == TabId)) return;
                var ms = (System.Diagnostics.Stopwatch.GetTimestamp() - _removedAt) * 1000 / System.Diagnostics.Stopwatch.Frequency;
                _removedAt = 0;
                _done++;
                var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
                ed?.WriteMessage($"\nRIBBONLOSS: the Retrace tab was back {ms} ms after the removal (removal {_done}).");
                System.IO.File.AppendAllText(@"C:\temp\ribbonloss.txt", $"{DateTime.Now:HH:mm:ss.fff} back after {ms} ms (removal {_done})\n");
                if (_left > 0) { _left--; Remove(); }
                else Unhook();
            }
            catch (System.Exception) { }
        }

        [CommandMethod("RIBBONLOSS")]
        public void Loss()
        {
            _left = 0; _done = 0;
            var ed = Application.DocumentManager.MdiActiveDocument.Editor;
            if (!Remove()) { ed.WriteMessage("\nRIBBONLOSS: no Retrace tab in the ribbon."); return; }
            Hook();
            ed.WriteMessage("\nRIBBONLOSS: tab removed.");
        }

        [CommandMethod("RIBBONLOSSMANY")]
        public void Many()
        {
            var ed = Application.DocumentManager.MdiActiveDocument.Editor;
            var o = new Autodesk.AutoCAD.EditorInput.PromptIntegerOptions("\nRemovals <8>: ") { AllowNone = true, DefaultValue = 8, LowerLimit = 1, UpperLimit = 50 };
            var r = ed.GetInteger(o);
            var n = r.Status == Autodesk.AutoCAD.EditorInput.PromptStatus.OK ? r.Value : 8;
            _done = 0; _left = n - 1;
            if (!Remove()) { ed.WriteMessage("\nRIBBONLOSSMANY: no Retrace tab in the ribbon."); return; }
            Hook();
            ed.WriteMessage($"\nRIBBONLOSSMANY: {n} removals, each time the tab is back.");
        }

        /// <summary>Prints what the ribbon holds about the Retrace tab: tab count, visible, active, index, ribbon open.</summary>
        [CommandMethod("RIBBONLOSSSTATE")]
        public void State()
        {
            var rc = Rc;
            var ed = Application.DocumentManager.MdiActiveDocument.Editor;
            if (rc == null) { ed.WriteMessage("\nRIBBONSTATE: no ribbon object."); return; }
            var mine = rc.Tabs.Where(t => t.Id == TabId).ToList();
            var first = mine.FirstOrDefault();
            ed.WriteMessage($"\nRIBBONSTATE: ribbonVisible={rc.IsVisible} tabs={rc.Tabs.Count} retraceTabs={mine.Count} visible={first?.IsVisible} active={first?.IsActive} index={(first == null ? -1 : rc.Tabs.IndexOf(first))} activeTab={rc.ActiveTab?.Id} sameRibbon={ReferenceEquals(rc, ComponentManager.Ribbon)}");
        }

        [CommandMethod("RIBBONLOSSHIDE")]
        public void Hide()
        {
            var tab = Rc?.Tabs.FirstOrDefault(t => t.Id == TabId);
            if (tab != null) tab.IsVisible = false;
            Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage(tab == null ? "\nRIBBONLOSSHIDE: no Retrace tab." : "\nRIBBONLOSSHIDE: tab hidden.");
        }

        [CommandMethod("RIBBONLOSSDUP")]
        public void Dup()
        {
            var rc = Rc;
            if (rc == null) return;
            var t = new RibbonTab { Id = TabId, Title = "Retrace copy", Name = "Retrace copy" };
            rc.Tabs.Add(t);
            Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage("\nRIBBONLOSSDUP: second tab with the Retrace id added.");
        }
    }
}
