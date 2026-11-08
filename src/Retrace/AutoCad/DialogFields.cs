using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace Retrace.AutoCad;

/// <summary>
/// Reads the field values of a dialog from outside (no source code needed, no AutoCAD API). Win32 first: walks the child windows and
/// reads edit boxes, check boxes, radios, combo and list boxes, sliders and the selected item of a tree with SendMessage. A dialog without child windows (WPF) is read
/// through UI Automation, which must only be called from a pool thread (<see cref="ReadUia"/>). The pure parts (labels, groups, keys)
/// are in <see cref="DialogFieldsCore"/>.
/// </summary>
static partial class DialogReader
{
    const uint WM_GETTEXT = 0x000D, WM_GETTEXTLENGTH = 0x000E, BM_GETCHECK = 0x00F0;
    const uint CB_GETCURSEL = 0x0147, CB_GETLBTEXT = 0x0148, CB_GETLBTEXTLEN = 0x0149;
    const uint LB_GETCURSEL = 0x0188, LB_GETTEXT = 0x0189, LB_GETTEXTLEN = 0x018A, TBM_GETPOS = 0x0400;
    const uint SMTO_ABORTIFHUNG = 2, TimeoutMs = 100;
    const int GWL_STYLE = -16;
    const uint WS_VISIBLE = 0x10000000;
    const long ES_PASSWORD = 0x20, ES_READONLY = 0x800, WS_GROUP = 0x00020000, WS_DISABLED = 0x08000000, CBS_DROPDOWNLIST = 3, LBS_MULTIPLESEL = 0x8, LBS_EXTENDEDSEL = 0x800;

    delegate bool EnumProc(IntPtr h, IntPtr lp);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] internal static extern int GetDlgCtrlID(IntPtr h);
    [DllImport("user32.dll")] internal static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] internal static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)]
    static extern IntPtr SendText(IntPtr h, uint msg, IntPtr w, StringBuilder l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    static extern IntPtr SendNum(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);

    sealed class Ctl
    {
        public IntPtr H, Parent;
        public string Cls = "", Seg = "", ParentSeg = "";
        /// <summary>The control that holds this window as a part of itself (the edit boxes of an IP address control, the combo box of a ComboBoxEx), or null; and whether that control is read as one field: only then is this window not a field of its own.</summary>
        public Ctl? CompositeParent;
        public bool CompositeRead;
        public long Style;
        public int Id;
        public Box Rect;
    }

    /// <summary>What one Win32 pass found: the fields, the control(s) behind each (all radios of a group), and the push buttons. With tab controls in the window: <see cref="Tabs"/> (parallel to
    /// <see cref="TabHandles"/>), <see cref="Showing"/>, the tab path the dialog as a whole shows, and <see cref="ButtonTabs"/> (parallel to <see cref="Buttons"/>): the tab each button sits on, else
    /// the one showing. A window with no tab control has none of it (and its fields' tab is "").</summary>
    internal sealed class Win32Scan
    {
        public List<DialogField> Fields = new();
        public List<List<IntPtr>> Handles = new();
        public List<(IntPtr H, int Id, string Text)> Buttons = new();
        public List<TabInfo> Tabs = new();
        public List<IntPtr> TabHandles = new();
        public string Showing = "";
        public List<string> ButtonTabs = new();
        /// <summary>The labels of the trees whose check boxes were cut at <see cref="TreeChecks.MaxItems"/> items.</summary>
        public List<string> Truncated = new();
        /// <summary>The toolbars (<c>ToolbarWindow32</c>) of this thread in the window: their buttons are clickable (<see cref="ToolbarControl"/>), not fields.</summary>
        public List<IntPtr> Toolbars = new();
        /// <summary>Parts the scan saw that no Win32 message reads (an MFC Feature Pack tab control, property grid or toolbar, by window class: <see cref="NativeParts.Classify"/>): named in the dialog's `unsupported`.</summary>
        public List<WpfPresses.Unsupported> Unsupported = new();

        /// <summary>The tab of <paramref name="button"/> (one of <see cref="Buttons"/>): "" when the window has no tabs or the button is not in the scan.</summary>
        public string TabOf(IntPtr button)
        {
            var at = Buttons.FindIndex(b => b.H == button);
            return at >= 0 && at < ButtonTabs.Count ? ButtonTabs[at] : "";
        }
    }

    /// <summary>Win32 pass. Cheap (SendMessage to this process's own windows); safe on the UI thread. Never throws.</summary>
    public static List<DialogField> ReadWin32(IntPtr dialog, TreeTickCache? ticks = null) => Scan(dialog, ticks: ticks).Fields;

    /// <summary>The one enumeration that reading and filling share. Never throws (an empty scan on failure).</summary>
    /// <param name="hiddenToo">also while <paramref name="dialog"/> itself is hidden (an auto-hidden palette): a control counts when its own
    /// WS_VISIBLE is set, not only when it is on screen</param>
    /// <param name="disabledToo">also list disabled push buttons (for describing what is near the cursor; never for pressing)</param>
    /// <param name="ticks">where the tick states of trees with check boxes are kept between reads (the recorder's); null reads them each time</param>
    /// <param name="readTicks">false for a replay, which finds a tree's items itself and never needs the whole list</param>
    internal static Win32Scan Scan(IntPtr dialog, bool hiddenToo = false, bool disabledToo = false, TreeTickCache? ticks = null, bool readTicks = true)
    {
        var scan = new Win32Scan();
        try
        {
            var handles = new List<IntPtr>();
            EnumChildWindows(dialog, (h, _) => { handles.Add(h); return true; }, IntPtr.Zero);

            var ctls = new List<Ctl>();
            var byHandle = new Dictionary<IntPtr, Ctl>();
            foreach (var h in handles)
            {
                if (hiddenToo ? !OwnVisibleUpTo(h, dialog) : !IsWindowVisible(h)) continue;
                GetWindowRect(h, out var r);
                var cls = ClassOf(h);   // once per window: the segment, the parent's segment and the composite parent below come from the controls gathered here
                var c = new Ctl
                {
                    H = h, Parent = GetParent(h), Cls = cls, Seg = Segment(cls),
                    Style = (uint)GetWindowLong(h, GWL_STYLE), Id = GetDlgCtrlID(h), Rect = new Box(r.Left, r.Top, r.Right, r.Bottom),
                };
                ctls.Add(c); byHandle[h] = c;
            }
            // EnumChildWindows lists a parent before its children: a child's parent is a control gathered already (or the dialog, or a window that is not visible and so not in the list)
            foreach (var c in ctls)
            {
                if (c.Parent == IntPtr.Zero || c.Parent == dialog) continue;
                c.ParentSeg = byHandle.TryGetValue(c.Parent, out var pc) ? pc.Seg : Segment(ClassOf(c.Parent));
                if (CompositeAncestor(c, byHandle) is { } holder) { c.CompositeParent = holder; c.CompositeRead = ReadsComposite(holder); }
            }

            var statics = new List<(Box Rect, string Text)>();
            foreach (var c in ctls)
                if (c.Seg == "static") { var t = Text(c.H); if (t.Length > 0) statics.Add((c.Rect, t)); }

            // the tab controls of the window, and which page each control sits in (a field on a page that is not showing is not visible, so not read)
            var tabScan = TabScan.Read(ctls);
            if (tabScan != null)
            {
                scan.Tabs = tabScan.Infos; scan.TabHandles = tabScan.Handles; scan.Showing = TabPlan.Showing(tabScan.Infos);
            }

            var raw = new List<RawControl>();
            var rects = new List<Box>();
            var rawHandles = new List<IntPtr>();
            void Add(Ctl c, RawControl r)
            {
                if (tabScan != null) r = r with { Tab = tabScan.PathOf(c.H) };
                // a WinForms control has a name that stays the same between runs, and maybe an accessible name: the label it was given
                if (WinFormsControls.NamesOf(c.H) is var (name, accessible) && name.Length > 0)
                    r = r with { Name = name, Label = accessible.Length > 0 ? DialogFieldsCore.CleanLabel(accessible) : r.Label };
                raw.Add(r); rects.Add(c.Rect); rawHandles.Add(c.H);
            }
            foreach (var c in ctls)
            {
                string? kind = null, value = null;
                var seg = c.Seg;
                if (c.CompositeRead) continue;   // the edit boxes of an IP address control, the combo box inside a ComboBoxEx: read through the control that holds them (when it is read; else they are read as they always were)
                if (NativeParts.Classify(c.Cls) is { } part && !scan.Unsupported.Contains(part)) scan.Unsupported.Add(part);   // by the class name alone: no window is sent a message to find out what it is
                if (NativeParts.IsToolbarClass(c.Cls)) { if (Native.OwnedByThisThread(c.H)) scan.Toolbars.Add(c.H); continue; }
                if (seg == "edit" || seg.Contains("richedit"))
                {
                    if ((c.Style & (ES_PASSWORD | ES_READONLY)) != 0 || c.ParentSeg.StartsWith("combobox")) continue;   // a combo's own edit is read through the combo
                    kind = "edit"; value = Text(c.H);
                }
                else if (seg == "button")
                {
                    switch (c.Style & 0xF)
                    {
                        case 2 or 3 or 5 or 6:
                        {
                            var st = Num(c.H, BM_GETCHECK);
                            var own = DialogFieldsCore.CleanLabel(Text(c.H));
                            Add(c, new RawControl(c.Id, own.Length > 0 ? own : (DialogFieldsCore.FindLabel(c.Rect, statics) ?? $"#{c.Id}"), "check", st == 1 ? "on" : st == 2 ? "mixed" : "off"));
                            break;
                        }
                        case 0xB when WinFormsControls.KindOf(c.H) is ("check" or "radio") and var wk:
                        {
                            // WinForms draws its check boxes and radios itself (owner-drawn, like its buttons): ask WinForms what it is
                            var own = DialogFieldsCore.CleanLabel(Text(c.H));
                            var on = WinFormsControls.IsChecked(c.H) ? "on" : "off";
                            Add(c, wk == "check"
                                ? new RawControl(c.Id, own.Length > 0 ? own : (DialogFieldsCore.FindLabel(c.Rect, statics) ?? $"#{c.Id}"), "check", on)
                                : new RawControl(c.Id, own.Length > 0 ? own : $"#{c.Id}", "radio1", on));
                            break;
                        }
                        case 4 or 9:
                        {
                            var own = DialogFieldsCore.CleanLabel(Text(c.H));
                            Add(c, new RawControl(c.Id, own.Length > 0 ? own : $"#{c.Id}", "radio", Num(c.H, BM_GETCHECK) == 1 ? "on" : "off", (c.Style & WS_GROUP) != 0, c.Parent.ToInt64()));
                            break;
                        }
                    }
                    continue;
                }
                else if (seg == "combobox")
                {
                    kind = "combo";
                    value = (c.Style & 3) == CBS_DROPDOWNLIST ? ListText(c.H, CB_GETCURSEL, CB_GETLBTEXTLEN, CB_GETLBTEXT) : Text(c.H);
                }
                else if (seg == "listbox")
                {
                    if ((c.Style & (LBS_MULTIPLESEL | LBS_EXTENDEDSEL)) != 0) continue;
                    kind = "list"; value = ListText(c.H, LB_GETCURSEL, LB_GETTEXTLEN, LB_GETTEXT);
                }
                else if (seg == "msctls_trackbar32")
                {
                    kind = "slider"; value = Num(c.H, TBM_GETPOS).ToString(CultureInfo.InvariantCulture);
                }
                else if (seg == "sysdatetimepick32")
                {
                    if (!Native.OwnedByThisThread(c.H) || CommonControls.ReadDate(c.H, c.Style) is not { } d) continue;
                    kind = DateText.Kind; value = d;
                }
                else if (seg == "sysmonthcal32")
                {
                    if (!Native.OwnedByThisThread(c.H) || CommonControls.ReadCalendar(c.H, c.Style) is not { } d) continue;
                    kind = CalendarText.Kind; value = d;
                }
                else if (seg == "sysipaddress32")
                {
                    kind = IpText.Kind; value = CommonControls.ReadIp(c.H);
                }
                else if (seg == "msctls_hotkey32")
                {
                    kind = HotkeyText.Kind; value = CommonControls.ReadHotkey(c.H);
                }
                else if (seg == "comboboxex32")
                {
                    if (!Native.OwnedByThisThread(c.H)) continue;
                    kind = "comboex"; value = CommonControls.ReadComboEx(c.H, c.Style);
                }
                else if (seg == "systreeview32")
                {
                    // the selected item and its parents (also a WinForms TreeView). Only a tree of this thread: the TVITEM messages pass pointers
                    // to buffers of this call, and a tree of another thread (or process) answers later, after a timeout has freed them
                    if (!Native.OwnedByThisThread(c.H)) continue;
                    kind = "tree"; value = TreeControl.SelectedPath(c.H);
                }
                else if (seg == "syslistview32")
                {
                    // the selected item(s), whatever the view mode (also a WinForms ListView). A list of another thread is skipped: the LVITEM messages pass pointers to our buffers.
                    if (!Native.OwnedByThisThread(c.H)) continue;
                    var label = DialogFieldsCore.FindLabel(c.Rect, statics) ?? $"#{c.Id}";
                    // a selection that cannot be read is a value of its own, never the last one read: the tracker must not keep an earlier selection as the final one
                    if (MenuListView.ReadSelection(c.H) is not { } items) { Add(c, new RawControl(c.Id, label, ListSelection.Kind, ListSelection.NotRecorded)); continue; }
                    Add(c, new RawControl(c.Id, label, ListSelection.Kind, ListSelection.Format(items), Items: items));
                    continue;
                }
                if (kind != null) Add(c, new RawControl(c.Id, DialogFieldsCore.FindLabel(c.Rect, statics) ?? $"#{c.Id}", kind, value ?? ""));
            }
            // group boxes name the section a field sits in: "Length › Precision" when two fields are both labelled "Precision"
            var groups = new List<(Box Rect, string Text)>();
            foreach (var c in ctls)
                if (c.Seg == "button" && (c.Style & 0xF) == 7) { var t = DialogFieldsCore.CleanLabel(Text(c.H)); if (t.Length > 0) groups.Add((c.Rect, t)); }
            foreach (var (f, idx) in DialogFieldsCore.BuildMapped(DialogFieldsCore.Qualify(raw, rects, groups)))
            {
                scan.Fields.Add(f);
                scan.Handles.Add(idx.Select(i => rawHandles[i]).ToList());
                // a tree with check boxes: one field per item (TreeChecks), after the tree itself, keyed by the tree's key so that it is the same field in every read
                if (f.Kind == "tree" && idx.Count == 1)
                    if (readTicks)
                    {
                        bool cut;
                        var list = ticks != null ? ticks.Get(rawHandles[idx[0]], Environment.TickCount64, out cut) : TreeControl.ReadChecks(rawHandles[idx[0]], out cut);
                        if (cut) scan.Truncated.Add(f.Label);
                        foreach (var (path, value) in list)
                        {
                            scan.Fields.Add(new DialogField(TreeChecks.KeyOf(f.Key, path), TreeChecks.LabelOf(f.Label, path), TreeChecks.Kind, value, f.Tab));
                            scan.Handles.Add(new List<IntPtr> { rawHandles[idx[0]] });
                        }
                    }
            }
            foreach (var c in ctls)
                if (c.Seg == "button" && ((c.Style & 0xF) is 0 or 1 or 0xB) && (disabledToo || (c.Style & WS_DISABLED) == 0) && WinFormsControls.KindOf(c.H) is null or "button")
                {
                    scan.Buttons.Add((c.H, c.Id, Text(c.H)));
                    if (tabScan != null) scan.ButtonTabs.Add(tabScan.PathOf(c.H) is { Length: > 0 } onTab ? onTab : scan.Showing);   // a button outside the tabs (Apply) is pressed with the tab showing
                }
            return scan;
        }
        catch (Exception ex) { Log.Error("dialog fields (win32)", ex); return new(); }
    }

    static readonly HashSet<string> Composites = new() { "sysipaddress32", "comboboxex32", "sysdatetimepick32", "sysmonthcal32" };

    /// <summary>Is the control read as a field? An IP address control is read from the text of its boxes and a hot key from a plain value (both are safe messages to a window of another thread); a date picker, a month calendar and a
    /// ComboBoxEx are read with messages that carry pointers to our buffers, so only when they belong to this thread. When one is not read, the windows inside it are read as they were before this control was (the inner combo box of a
    /// ComboBoxEx is a list field again), and the control is named once in the log: it never just disappears.</summary>
    static bool ReadsComposite(Ctl holder)
    {
        if (NativeParts.Classify(holder.Cls) is { } part) return NativeParts.HoldsOnlyItsOwnParts(part);   // a property grid's in-place editor, a toolbar's buttons: no fields
        if (holder.Seg == "sysipaddress32") return true;
        if (Native.OwnedByThisThread(holder.H)) return true;
        if (LoggedForeign.Count > 500) LoggedForeign.Clear();
        if (LoggedForeign.Add(holder.H)) Log.Info($"{holder.Cls} (control {holder.Id}) belongs to another thread: not read as one field");
        return false;
    }

    static readonly HashSet<IntPtr> LoggedForeign = new();

    /// <summary>The class segment of the control that holds <paramref name="c"/> as a part of itself (the edit boxes of an IP address control, the combo box of a ComboBoxEx), "" when it is a control of its own. Looks only at the
    /// controls gathered (<paramref name="gathered"/>): no window is asked its class or parent again.</summary>
    static Ctl? CompositeAncestor(Ctl c, Dictionary<IntPtr, Ctl> gathered)
    {
        for (int n = 0; n < 4 && gathered.TryGetValue(c.Parent, out var p); n++, c = p)
            if (Composites.Contains(p.Seg) || NativeParts.Classify(p.Cls) is not null) return p;
        return null;
    }

    /// <summary>For a scan of a hidden window (an auto-hidden palette): the control and every window between it and <paramref name="top"/> have their own WS_VISIBLE. A page that is not the shown one
    /// (a WinForms TabPage, a property sheet's page, a control a plain dialog hides) has none, so its controls, and a tab control on it, are left out.</summary>
    internal static bool OwnVisibleUpTo(IntPtr h, IntPtr top)
    {
        for (int n = 0; n < 40 && h != IntPtr.Zero && h != top; n++, h = GetParent(h))
            if (((uint)GetWindowLong(h, GWL_STYLE) & WS_VISIBLE) == 0) return false;
        return true;
    }

    /// <summary>The distinct window classes of all child windows of <paramref name="dialog"/> with their counts ("Button x4, SysListView32 x1, Edit x2"), at most 40 classes: a diagnostic for a control
    /// Retrace does not read (a click that changed no field).</summary>
    internal static string ChildClassSummary(IntPtr dialog)
    {
        try
        {
            var classes = new List<string>();
            EnumChildWindows(dialog, (h, _) => { classes.Add(ClassOf(h)); return true; }, IntPtr.Zero);
            return ClassSummary.Format(classes);
        }
        catch (Exception) { return "(unreadable)"; }
    }

    /// <summary>Lower-case class name; for WinForms ("WindowsForms10.EDIT.app.0.141b42a_r9_ad1") the part after "WindowsForms10.".</summary>
    internal static string Segment(string cls)
    {
        var l = cls.ToLowerInvariant();
        const string wf = "windowsforms10.";
        if (l.StartsWith(wf)) { var rest = l[wf.Length..]; var dot = rest.IndexOf('.'); return dot < 0 ? rest : rest[..dot]; }
        return l;
    }

    /// <summary>Which of the push buttons of <paramref name="page"/> with the same text <paramref name="button"/> is, counted in window order (1 = the
    /// first); 1 for a WPF button or one not found.</summary>
    internal static int ButtonIndex(IntPtr page, IntPtr button) => ButtonIndex(Scan(page, readTicks: false), button);

    /// <summary>Like <see cref="ButtonIndex(IntPtr, IntPtr)"/>, for a scan already made.</summary>
    internal static int ButtonIndex(Win32Scan scan, IntPtr button)
    {
        var all = scan.Buttons;
        var at = all.FindIndex(b => b.H == button);
        if (at < 0) return 1;
        var text = PaletteFill.ButtonLabel(all[at].Text);
        return all.Take(at + 1).Count(b => PaletteFill.ButtonLabel(b.Text) == text);
    }

    /// <summary>The text of <paramref name="h"/> if it is an enabled push button (BUTTON class, push or owner-drawn style), else null.</summary>
    internal static string? PushButtonText(IntPtr h)
    {
        if (Segment(ClassOf(h)) != "button") return null;
        var style = (uint)GetWindowLong(h, GWL_STYLE);
        if ((style & 0xF) is not (0 or 1 or 0xB) || (style & WS_DISABLED) != 0 || WinFormsControls.KindOf(h) is not (null or "button")) return null;
        var t = DialogFieldsCore.CleanLabel(Text(h));
        return t.Length > 0 ? t : "(button)";
    }

    internal static string ClassOf(IntPtr h) { var sb = new StringBuilder(128); GetClassName(h, sb, sb.Capacity); return sb.ToString(); }

    internal static long Num(IntPtr h, uint msg, long w = 0)
    {
        SendNum(h, msg, (IntPtr)w, IntPtr.Zero, SMTO_ABORTIFHUNG, TimeoutMs, out var r);
        return r.ToInt64();
    }

    internal static string Text(IntPtr h)
    {
        var len = (int)Math.Min(Num(h, WM_GETTEXTLENGTH), 4000);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        SendText(h, WM_GETTEXT, (IntPtr)(len + 1), sb, SMTO_ABORTIFHUNG, TimeoutMs, out _);
        return sb.ToString();
    }

    static string ListText(IntPtr h, uint getSel, uint getLen, uint getText)
    {
        var i = (int)Num(h, getSel);
        if (i < 0) return "";
        var len = (int)Math.Min(Num(h, getLen, i), 4000);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        SendText(h, getText, (IntPtr)i, sb, SMTO_ABORTIFHUNG, TimeoutMs, out _);
        return sb.ToString();
    }

    // ---- UI Automation (WPF dialogs) ---------------------------------------------------------------------------------

    /// <summary>UI Automation pass for dialogs without child windows. POOL THREAD ONLY: the calls go through the dialog's UI thread.
    /// Visits at most <paramref name="maxElements"/> elements and stops after <paramref name="timeoutMs"/>, keeping what it has. Never throws.</summary>
    public static List<DialogField> ReadUia(IntPtr dialog, int maxElements = 200, int timeoutMs = 1000) => ScanUia(dialog, maxElements, timeoutMs).Fields;

    /// <summary>What one UI Automation pass found: the fields, the element behind each, and the push buttons (Name only; UIA has no control ids).</summary>
    internal sealed class UiaScan
    {
        public List<DialogField> Fields = new();
        public List<AutomationElement> Elements = new();
        public List<(AutomationElement El, string Name)> Buttons = new();
    }

    /// <summary>The one UIA enumeration that reading and filling share. POOL THREAD ONLY. Never throws.</summary>
    internal static UiaScan ScanUia(IntPtr dialog, int maxElements = 200, int timeoutMs = 1000)
    {
        var scan = new UiaScan();
        var raw = new List<RawControl>();
        var rawEl = new List<AutomationElement>();
        try
        {
            var sw = Stopwatch.StartNew();
            var walker = TreeWalker.ControlViewWalker;
            var queue = new Queue<AutomationElement>();
            queue.Enqueue(AutomationElement.FromHandle(dialog));
            int seen = 0;
            while (queue.Count > 0 && seen < maxElements && sw.ElapsedMilliseconds < timeoutMs)
            {
                var el = queue.Dequeue();
                seen++;
                try
                {
                    int before = raw.Count;
                    ReadElement(el, raw);
                    if (raw.Count > before) rawEl.Add(el);
                    else if (el.Current.ControlType == ControlType.Button && !el.Current.IsOffscreen && el.Current.IsEnabled) scan.Buttons.Add((el, el.Current.Name ?? ""));
                }
                catch (Exception) { /* one broken element must not stop the walk */ }
                for (var ch = walker.GetFirstChild(el); ch != null && queue.Count < maxElements; ch = walker.GetNextSibling(ch)) queue.Enqueue(ch);
            }
        }
        catch (Exception ex) { Log.Error("dialog fields (uia)", ex); }
        foreach (var (f, idx) in DialogFieldsCore.BuildMapped(raw)) { scan.Fields.Add(f); scan.Elements.Add(rawEl[idx[0]]); }
        return scan;
    }

    static void ReadElement(AutomationElement el, List<RawControl> raw)
    {
        var cur = el.Current;
        if (cur.IsOffscreen) return;
        var type = cur.ControlType;
        string? kind = null, value = null;
        if (type == ControlType.Edit)
        {
            if (cur.IsPassword || !el.TryGetCurrentPattern(ValuePattern.Pattern, out var p)) return;
            var vp = (ValuePattern)p;
            if (vp.Current.IsReadOnly) return;
            kind = "edit"; value = vp.Current.Value;
        }
        else if (type == ControlType.CheckBox)
        {
            if (!el.TryGetCurrentPattern(TogglePattern.Pattern, out var p)) return;
            kind = "check"; value = ((TogglePattern)p).Current.ToggleState switch { ToggleState.On => "on", ToggleState.Indeterminate => "mixed", _ => "off" };
        }
        else if (type == ControlType.ComboBox)
        {
            kind = "combo";
            if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var p)) value = ((ValuePattern)p).Current.Value;
            else if (el.TryGetCurrentPattern(SelectionPattern.Pattern, out var sp)) value = ((SelectionPattern)sp).Current.GetSelection().FirstOrDefault()?.Current.Name ?? "";
            else return;
        }
        else if (type == ControlType.RadioButton)
        {
            if (!el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var p)) return;
            kind = "radio1"; value = ((SelectionItemPattern)p).Current.IsSelected ? "on" : "off";
        }
        else if (type == ControlType.Slider)
        {
            if (!el.TryGetCurrentPattern(RangeValuePattern.Pattern, out var p)) return;
            kind = "slider"; value = ((RangeValuePattern)p).Current.Value.ToString(CultureInfo.InvariantCulture);
        }
        if (kind == null) return;
        var label = cur.Name;
        if (string.IsNullOrWhiteSpace(label)) { try { label = cur.LabeledBy?.Current.Name; } catch (Exception) { label = null; } }
        if (string.IsNullOrWhiteSpace(label)) label = cur.AutomationId;
        var clean = DialogFieldsCore.CleanLabel(label);
        raw.Add(new RawControl(0, clean.Length > 0 ? clean : "(unnamed)", kind, value ?? ""));
    }
}
