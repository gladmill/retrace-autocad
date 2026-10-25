using System.Runtime.InteropServices;
using static DialogCapture.Native;

namespace DialogCapture;

/// <summary>
/// Custom window classes (MFC/ATL/AutoCAD controls) are not registered in this process, which makes
/// CreateDialogIndirectParam fail. Register a stand-in class for each: a light gray rectangle with a
/// dashed border and the class name, so the layout still shows.
/// </summary>
internal static class StubClasses
{
    // Classes that always exist once comctl32 v6 / riched are loaded. Never shadow these.
    static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "Button", "Edit", "Static", "ListBox", "ScrollBar", "ComboBox", "ComboLBox", "#32770", "RichEdit", "RichEdit20A", "RichEdit20W",
        "RICHEDIT50W", "SysListView32", "SysTreeView32", "SysTabControl32", "SysHeader32", "SysAnimate32", "SysDateTimePick32",
        "SysMonthCal32", "SysIPAddress32", "SysLink", "SysPager", "ComboBoxEx32", "ToolbarWindow32", "ReBarWindow32", "tooltips_class32",
        "msctls_statusbar32", "msctls_progress32", "msctls_trackbar32", "msctls_updown32", "msctls_hotkey32", "NativeFontCtl",
    };

    static readonly WndProc Proc = Paint;          // keep the delegate alive
    static readonly IntPtr ProcPtr = Marshal.GetFunctionPointerForDelegate(Proc);
    static readonly HashSet<string> Registered = new(StringComparer.OrdinalIgnoreCase);

    public static bool Exists(string cls)
    {
        if (Known.Contains(cls)) return true;
        var wc = new WNDCLASSEX { cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>() };
        return GetClassInfoEx(IntPtr.Zero, cls, ref wc) || GetClassInfoEx(GetModuleHandle(null), cls, ref wc);
    }

    /// <summary>Registers stubs for every unknown class; returns the class names it stubbed.</summary>
    public static List<string> Ensure(IEnumerable<string?> classes)
    {
        var stubbed = new List<string>();
        foreach (var c in classes.Where(c => !string.IsNullOrEmpty(c)).Select(c => c!).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Exists(c)) continue;
            if (!Registered.Contains(c))
            {
                var wc = new WNDCLASSEX
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                    style = 0x0003,                                    // CS_HREDRAW | CS_VREDRAW
                    lpfnWndProc = ProcPtr,
                    hInstance = GetModuleHandle(null),
                    hCursor = LoadCursor(IntPtr.Zero, (IntPtr)32512),  // IDC_ARROW
                    lpszClassName = Marshal.StringToHGlobalUni(c),
                };
                if (RegisterClassEx(ref wc) == 0) throw new InvalidOperationException($"RegisterClassEx failed for '{c}' (error {Marshal.GetLastWin32Error()})");
                Registered.Add(c);
            }
            stubbed.Add(c);
        }
        return stubbed;
    }

    static unsafe IntPtr Paint(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_ERASEBKGND) return (IntPtr)1;
        if (msg == WM_PAINT)
        {
            var hdc = BeginPaint(hwnd, out var ps);
            GetClientRect(hwnd, out RECT rc);
            var brush = CreateSolidBrush(0x00E6E6E6);                  // COLORREF 0x00BBGGRR: light gray
            FillRect(hdc, ref rc, brush);
            DeleteObject(brush);
            var pen = CreatePen(1 /* PS_DASH */, 1, 0x00808080);
            var oldPen = SelectObject(hdc, pen);
            var oldBrush = SelectObject(hdc, GetStockObject(5 /* NULL_BRUSH */));
            Rectangle(hdc, rc.Left, rc.Top, rc.Right, rc.Bottom);
            SelectObject(hdc, oldBrush); SelectObject(hdc, oldPen); DeleteObject(pen);
            var oldFont = SelectObject(hdc, GetStockObject(17 /* DEFAULT_GUI_FONT */));
            SetBkMode(hdc, 1 /* TRANSPARENT */);
            SetTextColor(hdc, 0x00404040);
            var name = new char[256];
            int n;
            fixed (char* p = name) n = GetClassName(hwnd, p, 256);
            var text = new string(name, 0, n);
            var tr = new RECT { Left = rc.Left + 2, Top = rc.Top + 2, Right = rc.Right - 2, Bottom = rc.Bottom - 2 };
            DrawText(hdc, text, -1, ref tr, 0x20 | 0x04 | 0x01 | 0x8000 /* SINGLELINE|VCENTER|CENTER|END_ELLIPSIS */);
            SelectObject(hdc, oldFont);
            EndPaint(hwnd, ref ps);
            return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }
}
