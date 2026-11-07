using System.Runtime.InteropServices;

namespace Retrace.AutoCad;

// The tab context of a scan: which tab controls a dialog (or palette page) has, which tab each shows, and which page each control sits in. The paths and the
// plan are Core (TabPath, TabPlan); this reads the windows.
static partial class DialogReader
{
    /// <summary>The tab controls one <see cref="Scan"/> found (visible ones of this thread) and where the controls sit: a control is in the page of a tab control when it is a child window of it
    /// (a WinForms TabPage, the page of a native control), else when the window that holds it next to the tab control (a property sheet's page, or the control itself in a plain dialog that
    /// shows and hides its controls on TCN_SELCHANGE) lies in the control's display rectangle (TCM_ADJUSTRECT).</summary>
    sealed class TabScan
    {
        public readonly List<IntPtr> Handles = new();
        public List<TabInfo> Infos = new();
        readonly List<(int L, int T, int R, int B)?> _display = new();
        readonly List<IntPtr> _siblingsParent = new();

        // tab texts per tab control, kept while its item count and selection stay as they were, it is the same window (class and parent) and they are under 5 s old (a scan runs every ~150 ms)
        static readonly Dictionary<IntPtr, (int Count, int Selected, IntPtr Parent, long At, List<string> Texts)> TextCache = new();
        readonly Dictionary<IntPtr, List<int>> _containing = new();   // per scan: where each window sits, worked out once

        static List<string> TextsOf(IntPtr tab, int selected)
        {
            int count = TabControl32.Count(tab);
            var parent = GetParent(tab);
            long now = Environment.TickCount64;
            if (TextCache.TryGetValue(tab, out var c) && c.Count == count && c.Selected == selected && c.Parent == parent && now - c.At < 5000) return c.Texts;
            if (TextCache.Count > 200) TextCache.Clear();
            var texts = TabControl32.Texts(tab);
            TextCache[tab] = (count, selected, parent, now, texts);
            return texts;
        }

        static readonly HashSet<IntPtr> Logged = new();   // tab controls already written to the log (once each: how a replay would switch them)

        /// <summary>Null when the dialog has no tab control of this thread.</summary>
        public static TabScan? Read(IReadOnlyList<Ctl> ctls)
        {
            var scan = new TabScan();
            var items = new List<(List<string> Items, int Selected)>();
            foreach (var c in ctls)
            {
                if (c.Seg != "systabcontrol32" || !Native.OwnedByThisThread(c.H)) continue;
                scan.Handles.Add(c.H);
                int selected = TabControl32.Selected(c.H);
                items.Add((TextsOf(c.H, selected), selected));
                if (Logged.Count > 500) Logged.Clear();
                if (Logged.Add(c.H)) Log.Info($"tab control {ClassOf(c.H)} ({items[^1].Items.Count} tabs: {string.Join(" | ", items[^1].Items)}): switched as a {TabControl32.KindOf(c.H)}");
                scan._display.Add(TabControl32.DisplayRect(c.H));
                scan._siblingsParent.Add(GetParent(c.H));
            }
            if (scan.Handles.Count == 0) return null;
            // which tab controls sit in the pages of which: a tab control is a control like any other
            for (int i = 0; i < scan.Handles.Count; i++)
                scan.Infos.Add(new TabInfo(i, items[i].Items, items[i].Selected, scan.Containing(scan.Handles[i])));
            return scan;
        }

        /// <summary>The ids of the tab controls whose page holds the window <paramref name="x"/>.</summary>
        public List<int> Containing(IntPtr x)
        {
            if (_containing.TryGetValue(x, out var known)) return known;
            var ids = new List<int>();
            for (int i = 0; i < Handles.Count; i++) if (Contains(i, x)) ids.Add(i);
            return _containing[x] = ids;
        }

        bool Contains(int i, IntPtr x)
        {
            var tab = Handles[i];
            if (x == tab) return false;
            if (Native.IsInside(tab, x)) return true;
            if (_display[i] is not { } disp || _siblingsParent[i] == IntPtr.Zero) return false;
            // the window that holds x next to the tab control (x itself, or the property sheet page it is in)
            var a = Native.AncestorUnder(x, _siblingsParent[i]);
            if (a == IntPtr.Zero || a == tab) return false;
            GetWindowRect(a, out var r);
            int cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
            return cx >= disp.L && cx < disp.R && cy >= disp.T && cy < disp.B;
        }

        /// <summary>The tab path of the window <paramref name="x"/>: the selected tab of each tab control whose page holds it, outermost first; "" when it sits in none.</summary>
        public string PathOf(IntPtr x) => TabPlan.PathOf(Containing(x), Infos);
    }
}
