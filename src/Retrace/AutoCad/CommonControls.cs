using System.Runtime.InteropServices;
using System.Text;
using Retrace.Core;

namespace Retrace.AutoCad;

/// <summary>
/// Reading and setting the Win32 common controls that are not edit boxes, lists or trees: the date and time picker, the month calendar, the IP address control, the hot key control and ComboBoxEx. Messages go to
/// controls of this thread, with pointers to buffers of the call (the controls are handled inside the call). The text of a value is in Core (<see cref="DateText"/>, <see cref="CalendarText"/>, <see cref="IpText"/>,
/// <see cref="HotkeyText"/>). Nothing here throws.
/// </summary>
static class CommonControls
{
    const uint WM_SETTEXT = 0x000C;
    const uint DTM_GETSYSTEMTIME = 0x1001, DTM_SETSYSTEMTIME = 0x1002;
    const uint MCM_GETCURSEL = 0x1001, MCM_SETCURSEL = 0x1002, MCM_GETSELRANGE = 0x1005, MCM_SETSELRANGE = 0x1006;
    const uint HKM_SETHOTKEY = 0x0401, HKM_GETHOTKEY = 0x0402;
    const uint CBEM_GETEDITCONTROL = 0x0407, CBEM_GETITEMW = 0x040D;
    const uint CB_GETCURSEL = 0x0147, CB_GETCOUNT = 0x0146, CB_SETCURSEL = 0x014E;
    const int GDT_VALID = 0, GDT_NONE = 1;
    const int DTN_DATETIMECHANGE = -759, MCN_SELCHANGE = -749, MCN_SELECT = -746, CBN_SELCHANGE = 1, CBN_SELENDOK = 9, CBN_EDITCHANGE = 5, EN_CHANGE = 0x0300;
    const long CBS_DROPDOWNLIST = 3;

    // every message here is sent with a timeout (Native.SendTimed): the controls that carry pointers are only reached when they belong to this thread, but a hot key or an IP address control may not, and a hung window must not hang us
    static IntPtr Send(IntPtr h, uint msg, IntPtr w, IntPtr l) => Native.SendTimed(h, msg, w, l);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)]
    static extern IntPtr SendStrTimeout(IntPtr h, uint msg, IntPtr w, string l, uint flags, uint timeout, out IntPtr result);
    static IntPtr SendStr(IntPtr h, uint msg, IntPtr w, string l) { SendStrTimeout(h, msg, w, l, Native.SMTO_ABORTIFHUNG, Native.SendTimeoutMs, out var r); return r; }
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    delegate bool EnumProc(IntPtr h, IntPtr lp);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    // ---- SYSTEMTIME ---------------------------------------------------------------------------------------------------

    static IntPtr Alloc(int bytes) { var p = Marshal.AllocHGlobal(bytes); for (int i = 0; i < bytes; i += 2) Marshal.WriteInt16(p, i, 0); return p; }

    static SysTime Read(IntPtr p) => new(Marshal.ReadInt16(p, 0), Marshal.ReadInt16(p, 2), Marshal.ReadInt16(p, 6), Marshal.ReadInt16(p, 8), Marshal.ReadInt16(p, 10), Marshal.ReadInt16(p, 12));

    static void Write(IntPtr p, SysTime t)
    {
        Marshal.WriteInt16(p, 0, (short)t.Year); Marshal.WriteInt16(p, 2, (short)t.Month); Marshal.WriteInt16(p, 4, 0); Marshal.WriteInt16(p, 6, (short)t.Day);
        Marshal.WriteInt16(p, 8, (short)t.Hour); Marshal.WriteInt16(p, 10, (short)t.Minute); Marshal.WriteInt16(p, 12, (short)t.Second); Marshal.WriteInt16(p, 14, 0);
    }

    /// <summary>WM_NOTIFY to the control's parent, as the control sends it: NMHDR (window, id, code) followed by <paramref name="extra"/> bytes (zero) for the notification's own data (copied from <paramref name="data"/>).</summary>
    static void Notify(IntPtr ctl, int code, byte[]? data = null)
    {
        var parent = DialogReader.GetParent(ctl);
        if (parent != IntPtr.Zero) Native.SendNotify(parent, ctl, DialogReader.GetDlgCtrlID(ctl), code, data);
    }

    static byte[] TimeBytes(params SysTime[] t)
    {
        var p = Alloc(32);
        try
        {
            Write(p, t[0]); if (t.Length > 1) Write(p + 16, t[1]);
            var b = new byte[32]; Marshal.Copy(p, b, 0, 32); return b;
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    // ---- date and time picker -----------------------------------------------------------------------------------------

    /// <summary>The value of a date and time picker: the date or the time (by its style), "" when it shows no date (unticked). Null when it could not be read.</summary>
    public static string? ReadDate(IntPtr h, long style)
    {
        var p = Alloc(16);
        try
        {
            int r = (int)Send(h, DTM_GETSYSTEMTIME, IntPtr.Zero, p);
            if (r == GDT_NONE) return "";
            return r == GDT_VALID ? DateText.Format(Read(p), DateText.IsTime(style)) : null;
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// <summary>Sets the picker and tells the owner (DTN_DATETIMECHANGE, which a program message does not send by itself). Null = done, else why not.</summary>
    public static string? SetDate(IntPtr h, long style, string text)
    {
        bool time = DateText.IsTime(style);
        var p = Alloc(16);
        try
        {
            var current = Send(h, DTM_GETSYSTEMTIME, IntPtr.Zero, p) == (IntPtr)GDT_VALID ? Read(p) : DateTime.Now is var n ? new SysTime(n.Year, n.Month, n.Day, n.Hour, n.Minute, n.Second) : default;
            if (!DateText.TryApply(time, text, current, out var t, out var none)) return $"\"{text}\" is not a {(time ? "time (HH:mm:ss)" : "date (yyyy-MM-dd)")}";
            if (none)
            {
                // a picker without DTS_SHOWNONE refuses "no date": the field is not set and the owner is not told of a change that did not happen
                if (Send(h, DTM_SETSYSTEMTIME, (IntPtr)GDT_NONE, IntPtr.Zero) == IntPtr.Zero) return "the date and time picker has no \"no date\" state (DTS_SHOWNONE)";
            }
            else
            {
                Write(p, t);
                if (Send(h, DTM_SETSYSTEMTIME, (IntPtr)GDT_VALID, p) == IntPtr.Zero) return "the date and time picker refused the value (outside its range?)";
            }
            // NMDATETIMECHANGE: NMHDR, dwFlags (GDT_VALID / GDT_NONE), SYSTEMTIME st
            var nm = new byte[4 + 16];
            BitConverter.GetBytes(none ? GDT_NONE : GDT_VALID).CopyTo(nm, 0);
            if (!none) TimeBytes(t).AsSpan(0, 16).CopyTo(nm.AsSpan(4));
            Notify(h, DTN_DATETIMECHANGE, nm);
            return null;
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    // ---- month calendar -----------------------------------------------------------------------------------------------

    public static string? ReadCalendar(IntPtr h, long style)
    {
        var p = Alloc(32);
        try
        {
            if (CalendarText.IsMulti(style))
                return Send(h, MCM_GETSELRANGE, IntPtr.Zero, p) != IntPtr.Zero ? CalendarText.Format(Read(p), Read(p + 16)) : null;
            return Send(h, MCM_GETCURSEL, IntPtr.Zero, p) != IntPtr.Zero ? CalendarText.Format(Read(p), null) : null;
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    public static string? SetCalendar(IntPtr h, long style, string text)
    {
        if (!CalendarText.TryParse(text, out var from, out var to)) return $"\"{text}\" is not a day (yyyy-MM-dd) or a range (yyyy-MM-dd..yyyy-MM-dd)";
        var p = Alloc(32);
        try
        {
            if (CalendarText.IsMulti(style)) { Write(p, from); Write(p + 16, to); if (Send(h, MCM_SETSELRANGE, IntPtr.Zero, p) == IntPtr.Zero) return "the calendar refused the range"; }
            else
            {
                if (from != to && (from.Year, from.Month, from.Day) != (to.Year, to.Month, to.Day)) return "the calendar selects one day only";
                Write(p, from);
                if (Send(h, MCM_SETCURSEL, IntPtr.Zero, p) == IntPtr.Zero) return "the calendar refused the day";
            }
            // NMSELCHANGE: NMHDR, SYSTEMTIME stSelStart, stSelEnd; MCN_SELCHANGE for a changed selection, MCN_SELECT for a chosen one
            var nm = TimeBytes(from, to);
            Notify(h, MCN_SELCHANGE, nm);
            Notify(h, MCN_SELECT, nm);
            return null;
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    // ---- IP address ----------------------------------------------------------------------------------------------------

    static List<IntPtr> FieldEdits(IntPtr ip)
    {
        var list = new List<(IntPtr H, int Left)>();
        EnumChildWindows(ip, (c, _) => { if (DialogReader.Segment(DialogReader.ClassOf(c)) == "edit") { GetWindowRect(c, out var r); list.Add((c, r.Left)); } return true; }, IntPtr.Zero);
        return list.OrderBy(x => x.Left).Select(x => x.H).ToList();
    }

    /// <summary>The four fields of an IP address control as text (the edit boxes inside it), so a partly filled control reads as it shows.</summary>
    public static string ReadIp(IntPtr h) => IpText.Format(FieldEdits(h).Select(DialogReader.Text).ToList());

    public static string? SetIp(IntPtr h, string text)
    {
        const uint IPM_CLEARADDRESS = 0x0464, IPM_SETADDRESS = 0x0465;
        if (!IpText.TryParse(text, out var b, out var blank))
        {
            if (!blank) return $"\"{text}\" is not a complete IP address (four numbers 0-255)";
            Send(h, IPM_CLEARADDRESS, IntPtr.Zero, IntPtr.Zero);
        }
        else Send(h, IPM_SETADDRESS, IntPtr.Zero, (IntPtr)(uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]));
        DialogFill.Notify(h, EN_CHANGE);   // (the IP control sends IPN_FIELDCHANGED itself for each field it set)
        return null;
    }

    // ---- hot key -------------------------------------------------------------------------------------------------------

    public static string ReadHotkey(IntPtr h) => HotkeyText.Format((int)Send(h, HKM_GETHOTKEY, IntPtr.Zero, IntPtr.Zero) & 0xFFFF);

    public static string? SetHotkey(IntPtr h, string text)
    {
        if (!HotkeyText.TryParse(text, out var word)) return $"\"{text}\" is not a key combination (like Ctrl+Shift+A)";
        Send(h, HKM_SETHOTKEY, (IntPtr)word, IntPtr.Zero);
        DialogFill.Notify(h, EN_CHANGE);   // the owner hears EN_CHANGE from the control when a person types
        return null;
    }

    // ---- ComboBoxEx ----------------------------------------------------------------------------------------------------

    static string ItemText(IntPtr h, int index)
    {
        var item = Alloc(64); var text = Alloc(1024);
        try
        {
            Marshal.WriteInt32(item, 0, 1 /* CBEIF_TEXT */); Marshal.WriteInt64(item, 8, index); Marshal.WriteIntPtr(item, 16, text); Marshal.WriteInt32(item, 24, 500);
            return Send(h, CBEM_GETITEMW, IntPtr.Zero, item) != IntPtr.Zero ? Marshal.PtrToStringUni(Marshal.ReadIntPtr(item, 16)) ?? "" : "";
        }
        finally { Marshal.FreeHGlobal(item); Marshal.FreeHGlobal(text); }
    }

    /// <summary>The text of a ComboBoxEx: the selected item of a drop-down list, the edit box of a drop-down.</summary>
    public static string ReadComboEx(IntPtr h, long style)
    {
        if ((style & 3) != CBS_DROPDOWNLIST && Send(h, CBEM_GETEDITCONTROL, IntPtr.Zero, IntPtr.Zero) is var edit && edit != IntPtr.Zero) return DialogReader.Text(edit);
        int sel = (int)Send(h, CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);
        return sel < 0 ? "" : ItemText(h, sel);
    }

    public static string? SetComboEx(IntPtr h, long style, string text)
    {
        int count = (int)Send(h, CB_GETCOUNT, IntPtr.Zero, IntPtr.Zero);
        for (int i = 0; i < count; i++)
            if (FillMatcher.ValueMatches("combo", text, ItemText(h, i)))
            {
                Send(h, CB_SETCURSEL, (IntPtr)i, IntPtr.Zero);
                DialogFill.Notify(h, CBN_SELCHANGE);
                DialogFill.Notify(h, CBN_SELENDOK);
                return null;
            }
        if ((style & 3) == CBS_DROPDOWNLIST) return $"no entry \"{text}\" in the list";
        var edit = Send(h, CBEM_GETEDITCONTROL, IntPtr.Zero, IntPtr.Zero);
        if (edit == IntPtr.Zero) return "the combo box has no edit box";
        SendStr(edit, WM_SETTEXT, IntPtr.Zero, text);
        DialogFill.Notify(h, CBN_EDITCHANGE);
        return null;
    }
}
