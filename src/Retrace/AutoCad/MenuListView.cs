using System.Runtime.InteropServices;
using Retrace.Core;
using static Retrace.AutoCad.Win32;

namespace Retrace.AutoCad;

/// <summary>
/// A list view (SysListView32, a WinForms ListView) as the target of a context menu. A list view is not a dialog field (its value is not recorded), so a menu choice would have no control and no item:
/// the recorder keeps the control as `name:Name` (WinForms) or `id:N`, the kind `listview`, the item the menu opened on (the clicked one, or the focused selected one for the Menu key and Shift+F10: its
/// first-column text, its row and which row with that text it is), the texts of all selected items, `noItem` when a hit test found none, `onHeader` for the column header. Replay finds the control the same way,
/// checks that every recorded item is there before it touches the selection, restores selection and focus, and right-clicks the item's label (empty space for no item, the header for the header).
/// UI thread only (the LVITEM and rectangle buffers are ours: only a window of this thread is asked).
/// </summary>
static class MenuListView
{
    public const string Kind = "listview";
    const uint LVM_GETITEMCOUNT = 0x1004, LVM_GETNEXTITEM = 0x100C, LVM_GETITEMRECT = 0x100E, LVM_HITTEST = 0x1012, LVM_ENSUREVISIBLE = 0x1013, LVM_SETITEMSTATE = 0x102B, LVM_GETITEMSTATE = 0x102C, LVM_GETITEMTEXTW = 0x1073;
    const int LVNI_FOCUSED = 1, LVNI_SELECTED = 2, LVIS_SELECTED = 2, LVIS_FOCUSED = 1, LVIF_STATE = 8, LVIR_LABEL = 2;
    const uint SMTO_ABORTIFHUNG = 2;
    const int MaxItems = 5000;
    /// <summary>More selected items than this are not recorded as a set (the item still is); replay hands such a menu to a person.</summary>
    public const int MaxSelection = 100;

    public static bool Is(IntPtr h) => DialogReader.Segment(DialogReader.ClassOf(h)) == "syslistview32";

    /// <summary>The list view that owns <paramref name="h"/> when <paramref name="h"/> is its header (SysHeader32), else zero.</summary>
    public static IntPtr OwnerOfHeader(IntPtr h) => DialogReader.Segment(DialogReader.ClassOf(h)) == "sysheader32" && DialogReader.GetParent(h) is var p && p != IntPtr.Zero && Is(p) ? p : IntPtr.Zero;

    static IntPtr Ask(IntPtr h, uint msg, IntPtr w, IntPtr l) { Native.SendNum(h, msg, w, l, SMTO_ABORTIFHUNG, 1000, out var r); return r; }

    /// <summary>One list view of a window: its window (zero when WinForms has not created it yet) and its place, the Nth list view of the window from 1.</summary>
    readonly record struct Entry(IntPtr H, int Ordinal);

    static object? GetProp(object o, string name) { try { return o.GetType().GetProperty(name)?.GetValue(o); } catch (Exception) { return null; } }
    static bool IsListViewControl(object c) { for (var t = c.GetType(); t != null; t = t.BaseType) if (t.FullName == "System.Windows.Forms.ListView") return true; return false; }

    static void CollectWinForms(object control, List<IntPtr> into)
    {
        if (GetProp(control, "Controls") is not System.Collections.IEnumerable children) return;
        foreach (var child in children)
        {
            if (child == null) continue;
            if (IsListViewControl(child)) into.Add(GetProp(child, "IsHandleCreated") is true && GetProp(child, "Handle") is IntPtr h ? h : IntPtr.Zero);
            CollectWinForms(child, into);
        }
    }

    /// <summary>The list views of a window in a fixed order, in one pass. A WinForms form is walked through its control tree (<c>Control.Controls</c>, created or not), so a list view on a tab page that was never shown
    /// still counts and the ordinals do not depend on which page is showing; any other window (a Win32 dialog, a palette page that is no WinForms control) through its child windows, which counts only list views that exist.</summary>
    static List<Entry> Enumerate(IntPtr container)
    {
        var handles = new List<IntPtr>();
        if (WinFormsControls.Control(container) is { } form) CollectWinForms(form, handles);
        else EnumChildWindows(container, (h, _) => { if (Is(h)) handles.Add(h); return true; }, IntPtr.Zero);
        return handles.Select((h, i) => new Entry(h, i + 1)).ToList();
    }

    /// <summary>The label of a list view the way a dialog field gets one: the static text next to it ("Tools:"), "" when there is none.</summary>
    static string LabelNextTo(IntPtr list, IntPtr container)
    {
        try
        {
            var statics = new List<(Box Rect, string Text)>();
            EnumChildWindows(container, (h, _) =>
            {
                if (DialogReader.Segment(DialogReader.ClassOf(h)) == "static" && Native.IsVisible(h) && GetWindowRect(h, out var r))
                {
                    var t = DialogReader.Text(h);
                    if (t.Length > 0) statics.Add((new Box(r.Left, r.Top, r.Right, r.Bottom), t));
                }
                return true;
            }, IntPtr.Zero);
            return GetWindowRect(list, out var lr) ? DialogFieldsCore.FindLabel(new Box(lr.Left, lr.Top, lr.Right, lr.Bottom), statics) ?? "" : "";
        }
        catch (Exception) { return ""; }
    }

    /// <summary>How a control is keyed: `name:Name` for a WinForms control that has one; a WinForms control without a name (its dialog id changes every run) by its place, `ord:N` (see <see cref="Enumerate"/>); any other (a
    /// Win32 dialog's own control) `id:N`. With a label: the accessible name or name, else the static text next to it; "" when there is none.</summary>
    static (string Key, string Label) KeyFor(IntPtr list, int ordinal, IntPtr container)
    {
        if (WinFormsControls.NamesOf(list) is var (name, accessible))
        {
            var own = DialogFieldsCore.CleanLabel(accessible.Length > 0 ? accessible : name);
            return (name.Length > 0 ? "name:" + name : ordinal > 0 ? "ord:" + ordinal : "id:" + DialogReader.GetDlgCtrlID(list), own.Length > 0 ? own : DialogFieldsCore.CleanLabel(LabelNextTo(list, container)));
        }
        return ("id:" + DialogReader.GetDlgCtrlID(list), DialogFieldsCore.CleanLabel(LabelNextTo(list, container)));
    }

    public static (string Key, string Label) KeyOf(IntPtr list, IntPtr container)
    {
        var entry = Enumerate(container).FirstOrDefault(e => e.H == list);
        return KeyFor(list, entry.Ordinal, container);
    }

    /// <summary>Reads the first-column texts of a list view through one reused buffer.</summary>
    sealed class Texts : IDisposable
    {
        const int Size = 96, Chars = 260;
        readonly IntPtr _list, _item = Marshal.AllocHGlobal(Size), _text = Marshal.AllocHGlobal(Chars * 2);
        public Texts(IntPtr list) { _list = list; }
        public string Of(int index)
        {
            for (int i = 0; i < Size; i += 8) Marshal.WriteInt64(_item, i, 0);
            Marshal.WriteInt16(_text, 0, 0);
            Marshal.WriteIntPtr(_item, 24, _text);          // pszText (LVITEMW, 64-bit layout)
            Marshal.WriteInt32(_item, 32, Chars);           // cchTextMax
            var n = Ask(_list, LVM_GETITEMTEXTW, (IntPtr)index, _item).ToInt32();   // iSubItem 0
            return n > 0 ? Marshal.PtrToStringUni(_text, Math.Min(n, Chars - 1)) ?? "" : "";
        }
        public List<string> All(int count) { var l = new List<string>(count); for (int i = 0; i < count; i++) l.Add(Of(i)); return l; }
        public void Dispose() { Marshal.FreeHGlobal(_item); Marshal.FreeHGlobal(_text); }
    }

    static int Count(IntPtr list) => Math.Min(Ask(list, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32(), MaxItems);
    static bool IsSelected(IntPtr list, int index) => (Ask(list, LVM_GETITEMSTATE, (IntPtr)index, (IntPtr)LVIS_SELECTED).ToInt64() & LVIS_SELECTED) != 0;

    static int HitTest(IntPtr list, int x, int y)
    {
        var info = Marshal.AllocHGlobal(24);
        try
        {
            for (int i = 0; i < 24; i += 8) Marshal.WriteInt64(info, i, 0);
            Marshal.WriteInt32(info, 0, x); Marshal.WriteInt32(info, 4, y);
            return Ask(list, LVM_HITTEST, IntPtr.Zero, info).ToInt32();
        }
        finally { Marshal.FreeHGlobal(info); }
    }

    /// <summary>What a menu opened on. <c>Known</c> false: the list could not be read (nothing is claimed, in particular not "no item"). <c>NoItem</c>: a hit test (or, for a key, no focused selected item and
    /// no selection) found no item; the selection is then empty (a click on empty space clears it). <c>Truncated</c>: the item is among more than <see cref="MaxSelection"/> selected ones: <c>Selection</c> is then empty.</summary>
    public sealed record Opened(bool Known, bool NoItem, string Item, int Index, int Occurrence, List<string> Selection, List<int> SelectionOccurrences, bool Truncated, List<int>? SelectionRows = null);
    static readonly Opened Unknown = new(false, false, "", -1, 0, new List<string>(), new List<int>(), false);

    /// <summary>Reads what the right-click at (x, y) of the list view (client coordinates) or the key (no point) opens its menu on. Called before the program reacts to the click.</summary>
    public static Opened Read(IntPtr list, int? x, int? y)
    {
        try
        {
            if (!Native.OwnedByThisThread(list)) return Unknown;
            using var reader = new Texts(list);
            var texts = reader.All(Count(list));
            var selected = new List<int>();
            for (int i = (int)Ask(list, LVM_GETNEXTITEM, (IntPtr)(-1), (IntPtr)LVNI_SELECTED); i >= 0 && selected.Count <= MaxSelection; i = (int)Ask(list, LVM_GETNEXTITEM, (IntPtr)i, (IntPtr)LVNI_SELECTED))
                selected.Add(i);
            int index;
            if (x == null || y == null)
            {
                index = Ask(list, LVM_GETNEXTITEM, (IntPtr)(-1), (IntPtr)LVNI_FOCUSED).ToInt32();
                if (index < 0 || !IsSelected(list, index)) index = selected.Count > 0 ? selected[0] : -1;   // a focus rectangle on an unselected item is no target of the key
            }
            else index = HitTest(list, x.Value, y.Value);
            if (index < 0) return new Opened(true, true, "", -1, 0, new List<string>(), new List<int>(), false);
            if (index >= texts.Count || selected.Any(r => r >= texts.Count)) { Log.Info($"menu on a list view: a row past the first {MaxItems} is involved, no item is recorded"); return Unknown; }
            bool itemSelected = IsSelected(list, index);
            bool truncated = selected.Count > MaxSelection && itemSelected;
            // a click on an unselected row selects it alone (this was read before the list view reacted)
            var rows = truncated ? new List<int>() : itemSelected ? selected : new List<int> { index };
            return new Opened(true, false, texts[index], index, ListViewRows.OccurrenceOf(texts, index), rows.Select(r => texts[r]).ToList(), rows.Select(r => ListViewRows.OccurrenceOf(texts, r)).ToList(), truncated, rows);
        }
        catch (Exception ex) { Log.Error("menu target (list view)", ex); return Unknown; }
    }

    /// <summary>The list view of <paramref name="container"/> that a recorded key names (`name:X`, `id:N`), or zero. <paramref name="hidden"/>: the container is an auto-hidden palette, so a control counts when
    /// it and every window up to the container have their own WS_VISIBLE (as a scan of a hidden window does).</summary>
    public static IntPtr Find(IntPtr container, string key, bool hidden)
    {
        foreach (var e in Enumerate(container))   // one pass: the ordinals come with it
        {
            if (e.H == IntPtr.Zero || !(hidden ? DialogReader.OwnVisibleUpTo(e.H, container) : Native.IsVisible(e.H))) continue;
            if (KeyFor(e.H, e.Ordinal, container).Key.Equals(key, StringComparison.OrdinalIgnoreCase)) return e.H;
        }
        return IntPtr.Zero;
    }

    /// <summary>The column header (SysHeader32) of a list view, or zero (a list view with no header). Visible by the same rule as the list view (<paramref name="hidden"/>: an auto-hidden palette).</summary>
    public static IntPtr HeaderOf(IntPtr list, IntPtr container, bool hidden)
    {
        var found = IntPtr.Zero;
        EnumChildWindows(list, (h, _) =>
        {
            if (DialogReader.Segment(DialogReader.ClassOf(h)) == "sysheader32" && (hidden ? DialogReader.OwnVisibleUpTo(h, container) : Native.IsVisible(h))) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    static void SetState(IntPtr list, int index, int state, int mask)
    {
        var s = Marshal.AllocHGlobal(96);
        try
        {
            for (int i = 0; i < 96; i += 8) Marshal.WriteInt64(s, i, 0);
            Marshal.WriteInt32(s, 0, LVIF_STATE); Marshal.WriteInt32(s, 12, state); Marshal.WriteInt32(s, 16, mask);
            Ask(list, LVM_SETITEMSTATE, (IntPtr)index, s);
        }
        finally { Marshal.FreeHGlobal(s); }
    }

    /// <summary>The middle of the label (column 0) of item <paramref name="index"/>, scrolled into view, in screen coordinates.</summary>
    static (int X, int Y) LabelMiddle(IntPtr list, int index)
    {
        var rect = Marshal.AllocHGlobal(16);
        try
        {
            Ask(list, LVM_ENSUREVISIBLE, (IntPtr)index, IntPtr.Zero);
            Marshal.WriteInt32(rect, 0, LVIR_LABEL);
            Ask(list, LVM_GETITEMRECT, (IntPtr)index, rect);
            var p = new POINT { X = (Marshal.ReadInt32(rect, 0) + Marshal.ReadInt32(rect, 8)) / 2, Y = (Marshal.ReadInt32(rect, 4) + Marshal.ReadInt32(rect, 12)) / 2 };
            ClientToScreen(list, ref p);
            return (p.X, p.Y);
        }
        finally { Marshal.FreeHGlobal(rect); }
    }

    /// <summary>The row texts of a list, read once and kept across the ticks of one replay step; read again when the number of rows changed.</summary>
    public sealed class TextCache
    {
        internal int Count = -1;
        internal List<string> Texts = new();
    }

    /// <summary>Where and how replay opens the menu. <c>PostTo</c>: the window the right-click or WM_CONTEXTMENU is posted to (the list, or its header for a header menu). <c>Keyboard</c>: there is no empty point to
    /// click, so WM_CONTEXTMENU is posted in its keyboard form (lParam -1) with the selection cleared.</summary>
    public sealed record Target(int X, int Y, IntPtr PostTo, bool Keyboard);

    /// <summary>Gets the list ready for the recorded menu and says where to open it (screen coordinates). A header menu needs no row and leaves the selection alone; no item clears the selection (after an empty
    /// point is found); an item: everything recorded is looked up first, and only when every row is there is the selection changed, so a missing row leaves the person's selection as it was and
    /// <paramref name="problem"/> names it. Row texts are read only for an item, once per step (<paramref name="cache"/>).</summary>
    public static Target? Prepare(IntPtr list, MenuChoice menu, TextCache cache, IntPtr container, bool hidden, out string? problem)
    {
        problem = null;
        try
        {
            if (!Native.OwnedByThisThread(list)) { problem = "the list belongs to another thread"; return null; }
            var header = HeaderOf(list, container, hidden);
            if (menu.OnHeader)
            {
                if (header == IntPtr.Zero) { problem = "the list has no column header"; return null; }
                if (!GetWindowRect(header, out var r)) { problem = "the column header could not be read"; return null; }
                return new Target((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2, header, false);
            }
            if (menu.NoItem)
            {
                var spot = EmptySpot(list, header);
                SetState(list, -1, 0, LVIS_SELECTED | LVIS_FOCUSED);
                if (spot != null) return new Target(spot.Value.X, spot.Value.Y, list, false);
                // the rows fill the list: the keyboard form of the menu, with nothing selected
                GetClientRect(list, out var c);
                var mid = new POINT { X = c.Width / 2, Y = c.Height / 2 };
                ClientToScreen(list, ref mid);
                return new Target(mid.X, mid.Y, list, true);
            }
            // the row texts are read once per step; a row that is not found is looked for once more in a fresh read before it is reported missing (an item renamed in place: "Wrench" -> "Wrench*")
            (int At, List<int> Rows)? plan = null;
            for (int pass = 0; pass < 2 && plan == null; pass++)
            {
                int count = Count(list);
                if (cache.Count != count || pass == 1) { using var reader = new Texts(list); cache.Texts = reader.All(count); cache.Count = count; }
                plan = ListViewRows.Plan(cache.Texts, menu, out problem);
            }
            if (plan is not var (at, rows)) return null;
            SetState(list, -1, 0, LVIS_SELECTED | LVIS_FOCUSED);   // every row checked: now the selection and focus change
            foreach (var i in rows) SetState(list, i, LVIS_SELECTED, LVIS_SELECTED);
            SetState(list, at, LVIS_FOCUSED | LVIS_SELECTED, LVIS_FOCUSED | LVIS_SELECTED);
            var p = LabelMiddle(list, at);
            return new Target(p.X, p.Y, list, false);
        }
        catch (Exception ex) { Log.Error("menu select (list view)", ex); problem = "the list view could not be read: " + ex.Message; return null; }
    }

    /// <summary>A point of the list view's client area where no item is and the header is not, in screen coordinates; null when the rows fill the area.</summary>
    static (int X, int Y)? EmptySpot(IntPtr list, IntPtr header)
    {
        if (!GetClientRect(list, out var c) || c.Width < 8 || c.Height < 8) return null;
        int headerBottom = int.MinValue, headerTop = int.MaxValue;
        if (header != IntPtr.Zero && GetWindowRect(header, out var hr))
        {
            var top = new POINT { X = hr.Left, Y = hr.Top }; var bottom = new POINT { X = hr.Right, Y = hr.Bottom };
            ScreenToClient(list, ref top); ScreenToClient(list, ref bottom);
            headerTop = top.Y; headerBottom = bottom.Y;
        }
        for (int y = c.Bottom - 3; y > c.Top + 2; y -= 4)
            if ((y < headerTop - 2 || y > headerBottom + 2) && HitTest(list, c.Right - 4, y) < 0)
            {
                var p = new POINT { X = c.Right - 4, Y = y };
                ClientToScreen(list, ref p);
                return (p.X, p.Y);
            }
        return null;
    }
}
