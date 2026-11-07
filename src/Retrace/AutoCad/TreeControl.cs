using System.Runtime.InteropServices;

namespace Retrace.AutoCad;

/// <summary>
/// A tree view (<c>SysTreeView32</c>, also what a WinForms TreeView is), read and driven with TVM messages. The one place for the TVM constants,
/// the TVITEM interop, an item's text, a path walk and the selection: the field kind <c>tree</c> of a dialog or palette (its value is the path of
/// the selected item, <see cref="TreePath"/>) and <see cref="NativePalette"/>'s tree clicks use it. UI thread only.
/// The messages that carry an item (TVITEM, RECT) pass pointers to memory of this call, which is only safe when the tree answers inside the
/// call: the window must belong to the calling thread (<see cref="OwnedByThisThread"/>; a message to another thread's window is queued and a
/// timeout would free the buffer while the tree may still use it). Nothing here throws.
/// </summary>
static class TreeControl
{
    internal const uint TV_FIRST = 0x1100, TVM_EXPAND = TV_FIRST + 2, TVM_GETITEMRECT = TV_FIRST + 4, TVM_GETNEXTITEM = TV_FIRST + 10,
        TVM_SELECTITEM = TV_FIRST + 11, TVM_ENSUREVISIBLE = TV_FIRST + 20, TVM_GETITEMW = TV_FIRST + 62, TVM_SETITEMW = TV_FIRST + 63;
    internal const int TVGN_ROOT = 0, TVGN_NEXT = 1, TVGN_PARENT = 3, TVGN_CHILD = 4, TVGN_CARET = 9, TVE_EXPAND = 2;
    internal const uint TVIF_TEXT = 1, TVIF_STATE = 8, TVIS_BOLD = 0x10, TVIS_EXPANDED = 0x20, TVIS_EXPANDEDONCE = 0x40;
    const uint SMTO_ABORTIFHUNG = 2, TimeoutMs = 2000;
    const int TextChars = 512, MaxSiblings = 20000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct TVITEMW { public uint mask; public IntPtr hItem; public uint state, stateMask; public IntPtr pszText; public int cchTextMax, iImage, iSelectedImage, cChildren; public IntPtr lParam; }

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    static extern IntPtr SendItem(IntPtr h, uint msg, IntPtr w, ref TVITEMW l, uint flags, uint timeout, out IntPtr result);


    internal static IntPtr Next(IntPtr tree, int how, IntPtr item)
    {
        Native.SendNum(tree, TVM_GETNEXTITEM, (IntPtr)how, item, SMTO_ABORTIFHUNG, TimeoutMs, out var r);
        return r;
    }

    /// <summary>An item's text (cut at 512 characters); "" when the tree did not answer.</summary>
    internal static string Text(IntPtr tree, IntPtr item)
    {
        var buf = Marshal.AllocHGlobal((TextChars + 1) * 2);
        try
        {
            Marshal.WriteInt16(buf, 0);
            var it = new TVITEMW { mask = TVIF_TEXT, hItem = item, pszText = buf, cchTextMax = TextChars };
            if (SendItem(tree, TVM_GETITEMW, IntPtr.Zero, ref it, SMTO_ABORTIFHUNG, TimeoutMs, out _) == IntPtr.Zero) return "";
            // a tree that asks its owner for the text may point the item at its own buffer
            return Marshal.PtrToStringUni(it.pszText == IntPtr.Zero ? buf : it.pszText) ?? "";
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>The bits of an item's state that are in <paramref name="mask"/> (TVIS_*).</summary>
    internal static uint State(IntPtr tree, IntPtr item, uint mask)
    {
        var it = new TVITEMW { mask = TVIF_STATE, hItem = item, stateMask = mask };
        return SendItem(tree, TVM_GETITEMW, IntPtr.Zero, ref it, SMTO_ABORTIFHUNG, TimeoutMs, out _) == IntPtr.Zero ? 0 : it.state & mask;
    }

    static void SetState(IntPtr tree, IntPtr item, uint mask, uint state)
    {
        var it = new TVITEMW { mask = TVIF_STATE, hItem = item, state = state, stateMask = mask };
        SendItem(tree, TVM_SETITEMW, IntPtr.Zero, ref it, SMTO_ABORTIFHUNG, TimeoutMs, out _);
    }

    /// <summary>The rectangle of an item's label (the text only, not its icon, check box or indent), in client coordinates; null when the item is not
    /// in view. TVM_GETITEMRECT takes the item handle in the rectangle it fills.</summary>
    internal static (int L, int T, int R, int B)? TextRect(IntPtr tree, IntPtr item)
    {
        var buf = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.WriteInt64(buf, 8, 0);
            Marshal.WriteIntPtr(buf, item);
            Native.SendNum(tree, TVM_GETITEMRECT, (IntPtr)1, buf, SMTO_ABORTIFHUNG, TimeoutMs, out var ok);
            if (ok == IntPtr.Zero) return null;
            return (Marshal.ReadInt32(buf, 0), Marshal.ReadInt32(buf, 4), Marshal.ReadInt32(buf, 8), Marshal.ReadInt32(buf, 12));
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>The texts of <paramref name="item"/> and its parents, from the top level down.</summary>
    internal static List<string> TextsTo(IntPtr tree, IntPtr item)
    {
        var texts = new List<string>();
        for (int i = 0; item != IntPtr.Zero && i < TreePath.MaxDepth; i++, item = Next(tree, TVGN_PARENT, item)) texts.Insert(0, Text(tree, item));
        return texts;
    }

    /// <summary>The selected item's texts from the top level down, as one path; "" when nothing is selected (or the tree did not answer).</summary>
    public static string SelectedPath(IntPtr tree)
    {
        try { return TreePath.Join(TextsTo(tree, Next(tree, TVGN_CARET, IntPtr.Zero))); }
        catch (Exception ex) { Log.Error("tree selected path", ex); return ""; }
    }

    /// <summary>The children of <paramref name="parent"/>, or the top-level items for the null handle.</summary>
    internal static IEnumerable<IntPtr> Children(IntPtr tree, IntPtr parent)
    {
        int n = 0;
        for (var it = Next(tree, parent == IntPtr.Zero ? TVGN_ROOT : TVGN_CHILD, parent); it != IntPtr.Zero && n < MaxSiblings; it = Next(tree, TVGN_NEXT, it), n++)
            yield return it;
    }

    /// <summary>Opens a node, so that a tree that fills it on opening has done so. TVM_EXPAND sends the owner TVN_ITEMEXPANDING (WinForms:
    /// BeforeExpand) from inside this call, so the children are there when it returns; but only the first time an item is opened this way (the
    /// state TVIS_EXPANDEDONCE stays set), where a click on the +/- button sends it every time. So a collapsed node gets that state cleared first.
    /// (Not TVE_COLLAPSERESET: it deletes the children of a tree that is not filled on demand.)</summary>
    internal static void Expand(IntPtr tree, IntPtr item)
    {
        var s = State(tree, item, TVIS_EXPANDED | TVIS_EXPANDEDONCE);
        if ((s & TVIS_EXPANDED) == 0 && (s & TVIS_EXPANDEDONCE) != 0) SetState(tree, item, TVIS_EXPANDEDONCE, 0);
        Native.SendNum(tree, TVM_EXPAND, (IntPtr)TVE_EXPAND, item, SMTO_ABORTIFHUNG, TimeoutMs, out _);
    }

    /// <summary>The item at <paramref name="levels"/>, walking down from the top level, each level matched by its text (trimmed, case-insensitive)
    /// and each parent opened on the way. The messages go to the tree while it is walked, so only for a tree of this thread.</summary>
    internal static TreeFound<IntPtr> Find(IntPtr tree, IReadOnlyList<string> levels) =>
        TreePath.Find(levels, IntPtr.Zero, p => Children(tree, p), it => Text(tree, it), p => Expand(tree, p));

    /// <summary>Selects the item at <paramref name="path"/> ("" = no selection), on this thread and inside this call: walks the path from the top
    /// level (opening each parent), then selects the item with TVM_SELECTITEM and scrolls it into view. The owner is told (TVN_SELCHANGED; WinForms
    /// AfterSelect) with the action TVC_UNKNOWN (<c>TreeViewAction.Unknown</c>), which a program that only reacts to the mouse or keyboard ignores.
    /// A click on the label (WM_LBUTTONDOWN / UP sent at the text rect) would say ByMouse, and did select in a stock tree (Purge), but a WinForms
    /// TreeView did not take it and the replay of a dialog with one took 6 s longer (2026-11-06), so fields are selected with TVM_SELECTITEM only;
    /// <see cref="NativePalette"/> clicks. What a selection handler does (fills other fields, shows a message box) runs inside this call, as for a
    /// combo box (<see cref="DialogFill"/> sends its notifications the same way), and so in the order the recorded values are set.
    /// Null = done, else why not.</summary>
    public static string? Select(IntPtr tree, string path) => SelectItem(tree, path, out _);

    /// <summary><see cref="Select"/>, and the item it ended on (zero for "" = no selection, or when it did not get there).</summary>
    public static string? SelectItem(IntPtr tree, string path, out IntPtr item)
    {
        item = IntPtr.Zero;
        try
        {
            if (!Native.OwnedByThisThread(tree)) return "the tree belongs to another thread";
            var levels = TreePath.Split(path);
            if (levels.Count > 0)
            {
                var found = Find(tree, levels);
                if (found.Problem != null) return found.Problem;
                item = found.Items[^1];
            }
            if (TreePath.Same(SelectedPath(tree), path)) return null;   // already there
            Native.SendNum(tree, TVM_SELECTITEM, (IntPtr)TVGN_CARET, item, SMTO_ABORTIFHUNG, TimeoutMs, out _);
            if (item != IntPtr.Zero) Native.SendNum(tree, TVM_ENSUREVISIBLE, IntPtr.Zero, item, SMTO_ABORTIFHUNG, TimeoutMs, out _);
            return null;
        }
        catch (Exception ex) { Log.Error("tree select", ex); return ex.Message; }
    }

    // ---- check boxes (TVS_CHECKBOXES, also WinForms TreeView.CheckBoxes) --------------------------------------------------------

    internal const uint TVM_HITTEST = TV_FIRST + 17, TVIS_STATEIMAGEMASK = 0xF000;
    const long TVS_CHECKBOXES = 0x100;
    const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203;
    const int VK_SPACE = 0x20;
    internal const uint TVHT_ONITEMICON = 0x2, TVHT_ONITEMLABEL = 0x4, TVHT_ONITEMSTATEICON = 0x40, TVHT_ONITEM = 0x46, TVHT_ITEMBODY = TVHT_ONITEMICON | TVHT_ONITEMLABEL;

    /// <summary>Does the tree show a check box in front of its items?</summary>
    public static bool HasCheckBoxes(IntPtr tree) => ((uint)DialogReader.GetWindowLong(tree, -16) & TVS_CHECKBOXES) != 0;

    /// <summary>"on" / "off" for the item's check box (state image 2 / 1), null when it has none.</summary>
    internal static string? CheckValue(IntPtr tree, IntPtr item) => TreeChecks.ValueOf((int)(State(tree, item, TVIS_STATEIMAGEMASK) >> 12));

    /// <summary>The check state of every item the tree has now, as paths (<see cref="TreeChecks.Collect{T}"/>). Nothing is opened: a node that fills when it opens shows its items once it was.
    /// Empty for a tree without check boxes or of another thread. <paramref name="truncated"/>: the cap of <see cref="TreeChecks.MaxItems"/> items was hit (a warning goes to the log, once per tree).</summary>
    public static List<(string Path, string Value)> ReadChecks(IntPtr tree, out bool truncated)
    {
        truncated = false;
        try
        {
            if (!HasCheckBoxes(tree) || !Native.OwnedByThisThread(tree)) return new();
            var list = TreeChecks.Collect(IntPtr.Zero, p => Children(tree, p), it => Text(tree, it), it => CheckValue(tree, it), out truncated);
            if (truncated && _warned.Add(tree)) Log.Info($"tree {tree}: more than {TreeChecks.MaxItems} items with check boxes; the rest are not recorded or replayed");
            return list;
        }
        catch (Exception ex) { Log.Error("tree checks", ex); return new(); }
    }

    static readonly HashSet<IntPtr> _warned = new();

    /// <summary>The check state of the item at <paramref name="path"/> without opening anything ("on", "off"), or null (no such item, no check box).</summary>
    public static string? ReadCheck(IntPtr tree, string path)
    {
        try
        {
            if (!Native.OwnedByThisThread(tree)) return null;
            var found = TreePath.Find(TreePath.Split(path), IntPtr.Zero, p => Children(tree, p), it => Text(tree, it));
            return found.Problem != null || found.Items.Count == 0 ? null : CheckValue(tree, found.Items[^1]);
        }
        catch (Exception ex) { Log.Error("tree check read", ex); return null; }
    }

    /// <summary>Ticks or unticks the item at <paramref name="path"/> so that its owner is told as after a person's tick, without moving the selection where it can be helped. The path is walked as
    /// <see cref="Select"/> does (each parent opened, a lazily filled node fills). The item that already is the selected one takes the space key (WM_KEYDOWN, WM_KEYUP: a native tree changes
    /// the state image and sends TVN_ITEMCHANGED, a WinForms <c>TreeView</c> raises BeforeCheck / AfterCheck as <c>ByKeyboard</c>). Any other item takes the mouse messages of a click on its state icon, as a
    /// person's click does (a click on the check box of an item that is not selected does not select it, so no AfterSelect is raised). Only when that did not change the state is the item selected, the
    /// space key sent and the selection put back (documented: the owner then sees two selection changes). There is no fallback that sets the state without telling the owner (TVM_SETITEM): when nothing
    /// worked the result is an error and a person ticks it. Null = done, else why not.</summary>
    public static string? SetChecked(IntPtr tree, string path, bool on)
    {
        try
        {
            if (!Native.OwnedByThisThread(tree)) return "the tree belongs to another thread";
            var levels = TreePath.Split(path);
            if (levels.Count == 0) return "no item path";
            var found = Find(tree, levels);
            if (found.Problem != null) return found.Problem;
            var item = found.Items[^1];
            var want = on ? "on" : "off";
            var now = CheckValue(tree, item);
            if (now == null) return $"the item {path} has no check box";
            if (now == want) return null;
            var caret = Next(tree, TVGN_CARET, IntPtr.Zero);
            string how;
            if (caret == item) { PressSpace(tree); how = "space key"; }
            else
            {
                how = "click on the state icon";
                if (StateIconPoint(tree, item) is { } pt) ClickAt(tree, pt.X, pt.Y);
                if (CheckValue(tree, item) != want)
                {
                    // the click did not tick it: the item is selected and takes the key, and the selection goes back
                    Native.SendNum(tree, TVM_SELECTITEM, (IntPtr)TVGN_CARET, item, SMTO_ABORTIFHUNG, TimeoutMs, out _);
                    Native.SendNum(tree, TVM_ENSUREVISIBLE, IntPtr.Zero, item, SMTO_ABORTIFHUNG, TimeoutMs, out _);
                    PressSpace(tree);
                    how = "select + space key";
                    if (caret != IntPtr.Zero) Native.SendNum(tree, TVM_SELECTITEM, (IntPtr)TVGN_CARET, caret, SMTO_ABORTIFHUNG, TimeoutMs, out _);
                }
            }
            if (CheckValue(tree, item) == want) { if (how == "select + space key") Log.Info($"tree tick {path}: the click on the state icon did not change it; select + space key did"); return null; }
            return $"the check box of {path} did not change (tried {how}; a person ticks it)";
        }
        catch (Exception ex) { Log.Error("tree set checked", ex); return ex.Message; }
    }

    static void PressSpace(IntPtr tree)
    {
        Native.SendNum(tree, WM_KEYDOWN, (IntPtr)VK_SPACE, (IntPtr)0x00390001, SMTO_ABORTIFHUNG, TimeoutMs, out _);
        Native.SendNum(tree, WM_KEYUP, (IntPtr)VK_SPACE, unchecked((IntPtr)(int)0xC0390001), SMTO_ABORTIFHUNG, TimeoutMs, out _);
    }

    static void ClickAt(IntPtr tree, int x, int y)
    {
        var lp = (IntPtr)((y << 16) | (x & 0xFFFF));
        Native.SendNum(tree, WM_LBUTTONDOWN, (IntPtr)1, lp, SMTO_ABORTIFHUNG, TimeoutMs, out _);
        Native.SendNum(tree, WM_LBUTTONUP, IntPtr.Zero, lp, SMTO_ABORTIFHUNG, TimeoutMs, out _);
    }

    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);

    /// <summary>A client point on the item's state icon: the middle of the icon at the left end of the item's rectangle (TVM_GETITEMRECT, the icon is one small-icon wide), checked with one hit test
    /// (TVHT_ONITEMSTATEICON); null when the point does not hit it.</summary>
    static (int X, int Y)? StateIconPoint(IntPtr tree, IntPtr item)
    {
        var buf = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.WriteInt64(buf, 8, 0); Marshal.WriteIntPtr(buf, item);
            Native.SendNum(tree, TVM_GETITEMRECT, IntPtr.Zero, buf, SMTO_ABORTIFHUNG, TimeoutMs, out var ok);
            if (ok == IntPtr.Zero) return null;
            int l = Marshal.ReadInt32(buf, 0), t = Marshal.ReadInt32(buf, 4), b = Marshal.ReadInt32(buf, 12), y = (t + b) / 2;
            int x = l + Math.Max(4, GetSystemMetrics(49 /* SM_CXSMICON */) / 2);
            var (hit, flags) = HitTest(tree, x, y);
            return hit == item && (flags & TVHT_ONITEMSTATEICON) != 0 ? (x, y) : null;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>The item of a tree under the client point <paramref name="lParam"/> of a mouse message, when the point is on the item's icon or label (not the +/- button, the indent, the check box or empty
    /// space) and the tree is a tree of this thread; zero otherwise. The one detector a double-click on a tree item of a dialog and of a palette share.</summary>
    internal static IntPtr ItemAt(IntPtr tree, IntPtr lParam)
    {
        if (DialogReader.Segment(DialogReader.ClassOf(tree)) != "systreeview32" || !Native.OwnedByThisThread(tree)) return IntPtr.Zero;
        int x = (short)((long)lParam & 0xFFFF), y = (short)(((long)lParam >> 16) & 0xFFFF);
        var (item, flags) = HitTest(tree, x, y);
        return item != IntPtr.Zero && (flags & TVHT_ITEMBODY) != 0 ? item : IntPtr.Zero;
    }

    // ---- double-click ---------------------------------------------------------------------------------------------------------

    /// <summary>The item under a client point, and the part of it that was hit (TVHT_*); (zero, flags) when none.</summary>
    internal static (IntPtr Item, uint Flags) HitTest(IntPtr tree, int x, int y)
    {
        var buf = Marshal.AllocHGlobal(24);
        try
        {
            for (int i = 0; i < 24; i += 8) Marshal.WriteInt64(buf, i, 0);
            Marshal.WriteInt32(buf, 0, x); Marshal.WriteInt32(buf, 4, y);
            Native.SendNum(tree, TVM_HITTEST, IntPtr.Zero, buf, SMTO_ABORTIFHUNG, TimeoutMs, out _);
            return (Marshal.ReadIntPtr(buf, 16), (uint)Marshal.ReadInt32(buf, 8));
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>Does the item have children (now)?</summary>
    internal static bool HasChildren(IntPtr tree, IntPtr item) => Next(tree, TVGN_CHILD, item) != IntPtr.Zero;

    /// <summary>The item at <paramref name="path"/> is selected (as <see cref="Select"/> does), then the mouse messages of a double-click are sent to its label: the double-click and the button-up. They are sent from the dispatcher after the caller's tick (<paramref name="later"/>): what the item's handler opens (a modal sub form) then runs with the replay's ticks
    /// going on, as for a pressed button. Null = scheduled, else why not.</summary>
    public static string? DoubleClick(IntPtr tree, string path, Action<Action> later)
    {
        try
        {
            if (SelectItem(tree, path, out var item) is { } why) return why;
            if (item == IntPtr.Zero) return "no item path";
            Native.SendNum(tree, TVM_ENSUREVISIBLE, IntPtr.Zero, item, SMTO_ABORTIFHUNG, TimeoutMs, out _);
            var r = TextRect(tree, item);
            if (r == null) return $"the item {path} is not in view";
            int x = Math.Min(r.Value.L + 8, (r.Value.L + r.Value.R) / 2), y = (r.Value.T + r.Value.B) / 2;
            var lp = (IntPtr)((y << 16) | (x & 0xFFFF));
            later(() =>
            {
                // no button down and up before it: the item is selected already, and a sent button-down makes the tree wait to see whether the mouse is dragged (seconds, as no button is
                // down for real), which let a sub form open late
                Native.SendNum(tree, WM_LBUTTONDBLCLK, (IntPtr)1, lp, SMTO_ABORTIFHUNG, 60000, out _);
                Native.SendNum(tree, WM_LBUTTONUP, IntPtr.Zero, lp, SMTO_ABORTIFHUNG, 60000, out _);
            });
            return null;
        }
        catch (Exception ex) { Log.Error("tree double-click", ex); return ex.Message; }
    }
}

/// <summary>The tick states of the trees of one recorded dialog, kept so that a read every ~150 ms does not walk every item of a big tree each time. A tree is read the first time, again when it received input
/// (<see cref="Invalidate"/>; the change an input makes shows after it was dispatched, so the first <see cref="DirtyMs"/> ms after the input still answer with the state before it, which is what a read made for that
/// input must show), when <see cref="ForceFresh"/> is set (the last read of a dialog), and at least every <see cref="MaxAgeMs"/> ms (a program that ticks by itself). UI thread only.</summary>
sealed class TreeTickCache
{
    public const long DirtyMs = 100, MaxAgeMs = 2000;
    sealed class Item { public List<(string Path, string Value)> List = new(); public bool Truncated; public long ReadAt, DirtyAt = -1; }
    readonly Dictionary<IntPtr, Item> _trees = new();

    public bool ForceFresh { get; set; }

    public void Invalidate(IntPtr tree) { if (_trees.TryGetValue(tree, out var i)) i.DirtyAt = Environment.TickCount64; }

    public List<(string Path, string Value)> Get(IntPtr tree, long nowMs, out bool truncated)
    {
        if (!_trees.TryGetValue(tree, out var i)) _trees[tree] = i = new Item { ReadAt = long.MinValue };
        bool stale = ForceFresh || i.ReadAt == long.MinValue || nowMs - i.ReadAt > MaxAgeMs || (i.DirtyAt >= 0 && nowMs - i.DirtyAt >= DirtyMs);
        if (stale)
        {
            i.List = TreeControl.ReadChecks(tree, out i.Truncated);
            i.ReadAt = nowMs; i.DirtyAt = -1;
        }
        truncated = i.Truncated;
        return i.List;
    }
}
