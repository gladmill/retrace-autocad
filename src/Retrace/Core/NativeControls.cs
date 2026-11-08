using System.Globalization;
using System.Text;

namespace Retrace.Core;

// NO Win32 and NO AutoCAD references in this file (linked into the engine test project).
// The pure rules of the Win32 common controls a dialog or MFC window may hold besides edit boxes, lists and trees: the text a value has
// (date, month calendar, IP address, hot key), parsing it back for the replay, the label of a toolbar button, and which window classes are
// parts Retrace cannot read.

/// <summary>A SYSTEMTIME without the day of week and milliseconds.</summary>
public readonly record struct SysTime(int Year, int Month, int Day, int Hour, int Minute, int Second)
{
    /// <summary>The same calendar day (year, month, day), whatever the time of day.</summary>
    public bool SameDay(SysTime other) => (Year, Month, Day) == (other.Year, other.Month, other.Day);

    public bool IsValid => Year is >= 1601 and <= 30827 && Month is >= 1 and <= 12 && Day >= 1 && Day <= DateTime.DaysInMonth(Year, Month) && Hour is >= 0 and < 24 && Minute is >= 0 and < 60 && Second is >= 0 and < 60;
}

/// <summary>What a date and time picker (<c>SysDateTimePick32</c>, kind <c>datetime</c>) shows: the date (<c>2026-03-14</c>) or, with the DTS_TIMEFORMAT style, the time (<c>09:30:00</c>). "" = no date (DTS_SHOWNONE, box unticked).</summary>
public static class DateText
{
    public const string Kind = "datetime";
    const long DtsFormatMask = 0xC, DtsTimeFormat = 0x8;

    /// <summary>Time of day for DTS_TIMEFORMAT, else the date. (A picker with a custom format string, DTM_SETFORMAT, cannot be told apart: it is taken by its style.)</summary>
    public static bool IsTime(long style) => (style & DtsFormatMask) == DtsTimeFormat;

    public static string Format(SysTime t, bool time) => time
        ? string.Create(CultureInfo.InvariantCulture, $"{t.Hour:00}:{t.Minute:00}:{t.Second:00}")
        : string.Create(CultureInfo.InvariantCulture, $"{t.Year:0000}-{t.Month:00}-{t.Day:00}");

    /// <summary>The time to set for a recorded value: the date part (or the time part) of <paramref name="current"/> is kept. <paramref name="none"/>: the recorded value is "" (no date). False when the text is no date / time.</summary>
    public static bool TryApply(bool time, string? text, SysTime current, out SysTime result, out bool none)
    {
        result = current; none = false;
        var s = (text ?? "").Trim();
        if (s.Length == 0) { none = true; return true; }
        if (time)
        {
            if (!DateTime.TryParseExact(s, new[] { "HH:mm:ss", "H:mm:ss", "HH:mm", "H:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) return false;
            result = current with { Hour = t.Hour, Minute = t.Minute, Second = t.Second };
            return true;
        }
        if (!DateTime.TryParseExact(s, new[] { "yyyy-MM-dd", "yyyy-M-d" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return false;
        result = current with { Year = d.Year, Month = d.Month, Day = d.Day };
        return result.IsValid;
    }
}

/// <summary>A month calendar (<c>SysMonthCal32</c>, kind <c>monthcal</c>): the selected day (<c>2026-03-14</c>), or with MCS_MULTISELECT the range <c>2026-03-14..2026-03-18</c>.</summary>
public static class CalendarText
{
    public const string Kind = "monthcal";
    public const string RangeSeparator = "..";
    const long McsMultiSelect = 0x2;

    public static bool IsMulti(long style) => (style & McsMultiSelect) != 0;

    public static string Format(SysTime from, SysTime? to) =>
        DateText.Format(from, false) + (to is { } t && !t.SameDay(from) ? RangeSeparator + DateText.Format(t, false) : "");

    /// <summary>The day or range of a recorded value; false for anything else.</summary>
    public static bool TryParse(string? text, out SysTime from, out SysTime to)
    {
        from = to = default;
        var s = (text ?? "").Trim();
        if (s.Length == 0) return false;
        var parts = s.Split(RangeSeparator, StringSplitOptions.None);
        if (parts.Length > 2) return false;
        var zero = new SysTime(2000, 1, 1, 0, 0, 0);
        if (!DateText.TryApply(false, parts[0], zero, out from, out var none) || none) return false;
        if (parts.Length == 1) { to = from; return true; }
        return DateText.TryApply(false, parts[1], zero, out to, out none) && !none && (to.SameDay(from) || (to.Year, to.Month, to.Day).CompareTo((from.Year, from.Month, from.Day)) > 0);
    }
}

/// <summary>An IP address control (<c>SysIPAddress32</c>, kind <c>ipaddress</c>): <c>192.168.1.10</c>; "" when all four fields are blank. A partly filled control reads as e.g. <c>192.168..</c> (kept as the text of its fields).</summary>
public static class IpText
{
    public const string Kind = "ipaddress";

    /// <summary>The value from the four field texts (the edit boxes inside the control): "" when all are blank.</summary>
    public static string Format(IReadOnlyList<string> fields)
    {
        var f = fields.Take(4).Select(x => x.Trim()).ToList();
        while (f.Count < 4) f.Add("");
        return f.All(x => x.Length == 0) ? "" : string.Join(".", f);
    }

    /// <summary>The four bytes of a complete address; false for "" (clear it) or a partly filled / wrong text, <paramref name="blank"/> says which.</summary>
    public static bool TryParse(string? text, out byte[] bytes, out bool blank)
    {
        bytes = new byte[4]; blank = false;
        var s = (text ?? "").Trim();
        if (s.Length == 0) { blank = true; return false; }
        var p = s.Split('.');
        if (p.Length != 4) return false;
        for (int i = 0; i < 4; i++)
            if (!byte.TryParse(p[i], NumberStyles.None, CultureInfo.InvariantCulture, out bytes[i])) return false;
        return true;
    }
}

/// <summary>A hot key control (<c>msctls_hotkey32</c>, kind <c>hotkey</c>): <c>Ctrl+Shift+A</c>; "" for none. The control's word is the virtual key (low byte) and HOTKEYF_* flags (high byte).</summary>
public static class HotkeyText
{
    public const string Kind = "hotkey";
    public const int Shift = 1, Control = 2, Alt = 4, Ext = 8;

    static readonly Dictionary<int, string> Names = BuildNames();

    static Dictionary<int, string> BuildNames()
    {
        var d = new Dictionary<int, string>
        {
            [0x08] = "Backspace", [0x09] = "Tab", [0x0D] = "Enter", [0x1B] = "Esc", [0x20] = "Space", [0x21] = "PageUp", [0x22] = "PageDown", [0x23] = "End", [0x24] = "Home",
            [0x25] = "Left", [0x26] = "Up", [0x27] = "Right", [0x28] = "Down", [0x2D] = "Insert", [0x2E] = "Delete", [0x6A] = "Num*", [0x6B] = "Num+", [0x6D] = "Num-", [0x6E] = "Num.", [0x6F] = "Num/",
            [0xBA] = ";", [0xBB] = "=", [0xBC] = ",", [0xBD] = "-", [0xBE] = ".", [0xBF] = "/", [0xC0] = "`", [0xDB] = "[", [0xDC] = "\\", [0xDD] = "]", [0xDE] = "'",
        };
        for (int c = '0'; c <= '9'; c++) d[c] = ((char)c).ToString();
        for (int c = 'A'; c <= 'Z'; c++) d[c] = ((char)c).ToString();
        for (int n = 0; n <= 9; n++) d[0x60 + n] = "Num" + n;
        for (int n = 1; n <= 24; n++) d[0x6F + n] = "F" + n;
        return d;
    }

    /// <summary>The text of the control's word (HKM_GETHOTKEY): "" when no key and no modifier is set.</summary>
    public static string Format(int word)
    {
        int vk = word & 0xFF, mod = (word >> 8) & 0xFF;
        if (vk == 0 && mod == 0) return "";
        var sb = new StringBuilder();
        if ((mod & Control) != 0) sb.Append("Ctrl+");
        if ((mod & Shift) != 0) sb.Append("Shift+");
        if ((mod & Alt) != 0) sb.Append("Alt+");
        if ((mod & Ext) != 0) sb.Append("Ext+");
        if (vk != 0) sb.Append(Names.TryGetValue(vk, out var n) ? n : "VK" + vk.ToString("X2", CultureInfo.InvariantCulture));
        else sb.Length--;   // modifiers only: no trailing '+'
        return sb.ToString();
    }

    /// <summary>The control's word for a recorded text (the inverse of <see cref="Format"/>); false for a text that is no hot key. "" is word 0 (none).</summary>
    public static bool TryParse(string? text, out int word)
    {
        word = 0;
        var s = (text ?? "").Trim();
        if (s.Length == 0) return true;
        int mod = 0, vk = 0;
        // modifiers are prefixes ("Ctrl+Shift+"); what is left is the key, which may itself end in '+' ("Num+")
        for (bool more = true; more;)
        {
            more = false;
            foreach (var (prefix, bit) in new[] { ("Ctrl+", Control), ("Shift+", Shift), ("Alt+", Alt), ("Ext+", Ext) })
                if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && s.Length > prefix.Length) { mod |= bit; s = s[prefix.Length..]; more = true; }
        }
        if (s.Length > 0)
        {
            var hit = Names.FirstOrDefault(kv => string.Equals(kv.Value, s, StringComparison.OrdinalIgnoreCase));
            if (hit.Value != null) vk = hit.Key;
            else if (s.StartsWith("VK", StringComparison.OrdinalIgnoreCase) && int.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) && h is > 0 and < 256) vk = h;
            else if (s.ToLowerInvariant() is "ctrl" or "shift" or "alt" or "ext") mod |= s.ToLowerInvariant() switch { "ctrl" => Control, "shift" => Shift, "alt" => Alt, _ => Ext };   // modifiers only
            else return false;
        }
        word = vk | (mod << 8);
        return true;
    }
}

/// <summary>A button of a toolbar (<c>ToolbarWindow32</c>, an MFC CToolBar) pressed in a dialog or frame: recorded as a <c>dialog_click</c> of kind <c>toolbar</c> whose <c>id</c> is the command id and
/// <c>button</c> its text or tooltip.</summary>
public static class ToolbarLabel
{
    /// <summary>The name of a button: its own text, else the tooltip (an MFC prompt string "status text\ntooltip" gives the tooltip), else "toolbar button 32771".</summary>
    public static string Choose(string? text, string? tooltip, int commandId)
    {
        var t = DialogFieldsCore.CleanLabel(text);
        if (t.Length > 0) return t;
        var tip = (tooltip ?? "").Replace("\r", "");
        var nl = tip.LastIndexOf('\n');
        if (nl >= 0) tip = tip[(nl + 1)..];
        tip = DialogFieldsCore.CleanLabel(tip);
        return tip.Length > 0 ? tip : "toolbar button " + commandId;
    }

    /// <summary>Is the client point inside the rectangle (left and top in, right and bottom out)?</summary>
    public static bool Inside(int x, int y, int left, int top, int right, int bottom) => x >= left && x < right && y >= top && y < bottom;

    /// <summary>Which toolbar button a recorded click means, over the command ids of the buttons of every toolbar of the window (<paramref name="ids"/>, in toolbar order then button order): the first one with
    /// that command id, in any toolbar (an MFC command id is the same in every run); -1 when no toolbar has it. There is no fallback by name: the name of a button is for steps.md and messages only.</summary>
    public static int FindById(IReadOnlyList<int> ids, int recordedId)
    {
        if (recordedId > 0)
            for (int i = 0; i < ids.Count; i++) if (ids[i] == recordedId) return i;
        return -1;
    }
}

/// <summary>What a replay may do with a toolbar button the way the toolbar itself does (a posted WM_COMMAND to its owner). A button style is the TBBUTTON <c>fsStyle</c> byte (BTNS_*).</summary>
public static class ToolbarPress
{
    public const int Check = 0x02, Group = 0x04, DropDown = 0x08, WholeDropDown = 0x80;

    public static bool IsDropDown(int style) => (style & (DropDown | WholeDropDown)) != 0;
    public static bool IsCheck(int style) => (style & Check) != 0;

    /// <summary>The toolbar's extended style TBSTYLE_EX_DRAWDDARROWS: its BTNS_DROPDOWN buttons are split buttons (the main part sends WM_COMMAND, the arrow opens the menu).</summary>
    public const int ExDrawDropDownArrows = 0x01;

    /// <summary>Why a replay must leave this button to a person, or null. A button whose whole face opens a menu (BTNS_WHOLEDROPDOWN, or BTNS_DROPDOWN on a toolbar without TBSTYLE_EX_DRAWDDARROWS) sends TBN_DROPDOWN, not
    /// WM_COMMAND: a WM_COMMAND would run the command without the choice the person made in the menu. The main part of a split button (BTNS_DROPDOWN with the arrows drawn) is a plain command and replays as one.</summary>
    public static string? StopFor(int style, int toolbarExtendedStyle) =>
        (style & WholeDropDown) != 0 || ((style & DropDown) != 0 && (toolbarExtendedStyle & ExDrawDropDownArrows) == 0) ? "a drop-down toolbar button: press it yourself" : null;

    /// <summary>The state a click leaves a check button in, or null for a button that has none: a plain check button toggles, a button of a check group ends checked. The replay sets this state (TB_CHECKBUTTON, as
    /// the toolbar does for a click) before it posts the command, and looks at it again afterwards.</summary>
    public static bool? ExpectedChecked(int style, bool before) => !IsCheck(style) ? null : (style & Group) != 0 ? true : !before;

    /// <summary>The reason to stop when the state a posted command left a check button in is not what a click gives, else null (also for a button that has no check state).</summary>
    public static string? CheckProblem(int style, bool before, bool after) =>
        ExpectedChecked(style, before) is { } want && want != after ? $"the toolbar button is {(after ? "checked" : "unchecked")}, a click leaves it {(want ? "checked" : "unchecked")}: press it yourself" : null;
}

/// <summary>Window classes Retrace cannot read through Win32 messages. They are named in the dialog's <c>unsupported</c> (as for a WPF dialog, <see cref="WpfPresses.Unsupported"/>): kind, and the class as the name.</summary>
public static class NativeParts
{
    /// <summary>Kinds that stand for a part of a Win32 dialog (<see cref="WpfPresses.Unsupported.Kind"/>).</summary>
    public const string MfcTabs = "mfc-tabs", MfcPropertyGrid = "mfc-propertygrid", MfcToolbar = "mfc-toolbar";

    /// <summary>The one place that knows the window classes: the MFC Feature Pack windows that draw themselves (the class with its per-process suffix cut, <see cref="StableClass"/>), the class of a Win32 toolbar. MFC registers the Feature Pack classes as <c>Afx:TabWnd:&lt;hex&gt;...</c>, <c>Afx:PropList:...</c> and <c>Afx:ToolBar:...</c> (seen in AutoCAD 2026 with an MFC add-in).</summary>
    static readonly (string Class, string Kind)[] DrawnClasses = { ("Afx:TabWnd", MfcTabs), ("Afx:PropList", MfcPropertyGrid), ("Afx:ToolBar", MfcToolbar) };
    public const string ToolbarClass = "ToolbarWindow32";

    /// <summary>Is this window class a Win32 toolbar (a CToolBar)? Messages of the TB_ family are sent to windows of this class only.</summary>
    public static bool IsToolbarClass(string windowClass) => string.Equals(windowClass, ToolbarClass, StringComparison.OrdinalIgnoreCase);

    /// <summary>The class of an MFC window with what is per process cut: every hex or numeric segment of an <c>Afx:</c> name (module handle, style, cursor, brush, icon), wherever it stands. <c>Afx:TabWnd:400000:8:10003:0</c>
    /// becomes <c>Afx:TabWnd</c>, and the unnamed <c>Afx:00007FF7:8:0000000000010003:...</c> (AutoCAD's own windows) <c>Afx</c>. Any other class is as it is.</summary>
    public static string StableClass(string cls)
    {
        if (!cls.StartsWith("Afx:", StringComparison.OrdinalIgnoreCase)) return cls;
        var parts = cls.Split(':');
        return string.Join(":", parts.Where((p, i) => i == 0 || !(p.Length > 0 && p.All(Uri.IsHexDigit))));
    }

    /// <summary>The part a window class stands for that is drawn by its owner (no Win32 message tells its state), or null: decided by the class name alone, no window is asked anything. Docking bars and frames
    /// (<c>Afx:ControlBar</c>, <c>AfxFrameOrView140u</c>) are containers; a CToolBar docked in a bar is a <c>ToolbarWindow32</c> child, which is a toolbar of its own (<see cref="IsToolbarClass"/>). A <c>CCheckListBox</c> is not
    /// flagged: it is an owner-drawn <c>ListBox</c> like any other, and its check marks cannot be told from outside (the owner answers LB_GETITEMDATA with the program's own data; see TODO.md).</summary>
    public static WpfPresses.Unsupported? Classify(string windowClass)
    {
        var stable = StableClass(windowClass);
        foreach (var (cls, kind) in DrawnClasses)
            if (string.Equals(stable, cls, StringComparison.OrdinalIgnoreCase)) return new WpfPresses.Unsupported(kind, stable);
        return null;
    }

    /// <summary>Do the windows inside such a part belong to it (the in-place editor of a property grid, the buttons of a toolbar), so that none of them is a field? A tab control is different: the controls on its
    /// page are the program's own and are read as any control is.</summary>
    public static bool HoldsOnlyItsOwnParts(WpfPresses.Unsupported part) => part.Kind is MfcPropertyGrid or MfcToolbar;

    /// <summary>The wording for a part that <see cref="Classify"/> found, for <see cref="WpfPresses.UnsupportedParts"/>; null for another kind.</summary>
    public static string? Phrase(WpfPresses.Unsupported u) => u.Kind switch
    {
        MfcTabs => "an MFC tab control (CMFCTabCtrl, window class \"" + u.Name + "\")",
        MfcPropertyGrid => "an MFC property grid (CMFCPropertyGridCtrl, window class \"" + u.Name + "\")",
        MfcToolbar => "an MFC Feature Pack toolbar (CMFCToolBar, window class \"" + u.Name + "\")",
        _ => null,
    };
}

/// <summary>The diagnostic line of a button click that changed no field: the child window classes of the window with counts.</summary>
public static class ClassSummary
{
    /// <summary>"Button x4, SysListView32 x1, Afx:TabWnd x1": the classes (MFC Feature Pack suffixes cut) by count then name, at most <paramref name="max"/>.</summary>
    public static string Format(IEnumerable<string> classes, int max = 40)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in classes) { var k = NativeParts.StableClass(c); counts[k] = counts.GetValueOrDefault(k) + 1; }
        return string.Join(", ", counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Take(max).Select(kv => kv.Key + " x" + kv.Value));
    }
}
