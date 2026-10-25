using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace Retrace.AutoCad;

/// <summary>
/// Reads the field values of a dialog from outside (no source code needed, no AutoCAD API). Win32 first: walks the child windows and
/// reads edit boxes, check boxes, radios, combo and list boxes and sliders with SendMessage. A dialog without child windows (WPF) is read
/// through UI Automation, which must only be called from a pool thread (<see cref="ReadUia"/>). The pure parts (labels, groups, keys)
/// are in <see cref="DialogFieldsCore"/>.
/// </summary>
static class DialogReader
{
    const uint WM_GETTEXT = 0x000D, WM_GETTEXTLENGTH = 0x000E, BM_GETCHECK = 0x00F0;
    const uint CB_GETCURSEL = 0x0147, CB_GETLBTEXT = 0x0148, CB_GETLBTEXTLEN = 0x0149;
    const uint LB_GETCURSEL = 0x0188, LB_GETTEXT = 0x0189, LB_GETTEXTLEN = 0x018A, TBM_GETPOS = 0x0400;
    const uint SMTO_ABORTIFHUNG = 2, TimeoutMs = 100;
    const int GWL_STYLE = -16;
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
        public string Seg = "", ParentSeg = "";
        public long Style;
        public int Id;
        public Box Rect;
    }

    /// <summary>What one Win32 pass found: the fields, the control(s) behind each (all radios of a group), and the push buttons.</summary>
    internal sealed class Win32Scan
    {
        public List<DialogField> Fields = new();
        public List<List<IntPtr>> Handles = new();
        public List<(IntPtr H, int Id, string Text)> Buttons = new();
    }

    /// <summary>Win32 pass. Cheap (SendMessage to this process's own windows); safe on the UI thread. Never throws.</summary>
    public static List<DialogField> ReadWin32(IntPtr dialog) => Scan(dialog).Fields;

    /// <summary>The one enumeration that reading and filling share. Never throws (an empty scan on failure).</summary>
    internal static Win32Scan Scan(IntPtr dialog)
    {
        var scan = new Win32Scan();
        try
        {
            var handles = new List<IntPtr>();
            EnumChildWindows(dialog, (h, _) => { handles.Add(h); return true; }, IntPtr.Zero);

            var ctls = new List<Ctl>();
            foreach (var h in handles)
            {
                if (!IsWindowVisible(h)) continue;
                GetWindowRect(h, out var r);
                var p = GetParent(h);
                ctls.Add(new Ctl
                {
                    H = h, Parent = p, Seg = Segment(ClassOf(h)), ParentSeg = p == IntPtr.Zero || p == dialog ? "" : Segment(ClassOf(p)),
                    Style = (uint)GetWindowLong(h, GWL_STYLE), Id = GetDlgCtrlID(h), Rect = new Box(r.Left, r.Top, r.Right, r.Bottom),
                });
            }

            var statics = new List<(Box Rect, string Text)>();
            foreach (var c in ctls)
                if (c.Seg == "static") { var t = Text(c.H); if (t.Length > 0) statics.Add((c.Rect, t)); }

            var raw = new List<RawControl>();
            var rects = new List<Box>();
            var rawHandles = new List<IntPtr>();
            void Add(Ctl c, RawControl r) { raw.Add(r); rects.Add(c.Rect); rawHandles.Add(c.H); }
            foreach (var c in ctls)
            {
                string? kind = null, value = null;
                var seg = c.Seg;
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
            }
            foreach (var c in ctls)
                if (c.Seg == "button" && ((c.Style & 0xF) is 0 or 1 or 0xB) && (c.Style & WS_DISABLED) == 0)
                    scan.Buttons.Add((c.H, c.Id, Text(c.H)));
            return scan;
        }
        catch (Exception ex) { Log.Error("dialog fields (win32)", ex); return new(); }
    }

    /// <summary>Lower-case class name; for WinForms ("WindowsForms10.EDIT.app.0.141b42a_r9_ad1") the part after "WindowsForms10.".</summary>
    internal static string Segment(string cls)
    {
        var l = cls.ToLowerInvariant();
        const string wf = "windowsforms10.";
        if (l.StartsWith(wf)) { var rest = l[wf.Length..]; var dot = rest.IndexOf('.'); return dot < 0 ? rest : rest[..dot]; }
        return l;
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
