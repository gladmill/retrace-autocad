using System.Runtime.InteropServices;

namespace Retrace.AutoCad;

/// <summary>Raw Win32 declarations. No WinForms: everything is P/Invoke.</summary>
internal static unsafe class Win32
{
    public const uint LOAD_LIBRARY_AS_DATAFILE = 0x2;
    public const uint LOAD_LIBRARY_AS_IMAGE_RESOURCE = 0x20;
    public static readonly IntPtr RT_DIALOG = (IntPtr)5;

    public const uint WM_INITDIALOG = 0x0110;
    public const uint WM_GETFONT = 0x0031;
    public const uint WM_PAINT = 0x000F;
    public const uint WM_ERASEBKGND = 0x0014;

    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const int SW_HIDE = 0;
    public const int SW_SHOW = 5;
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_SHOWWINDOW = 0x40;
    public static readonly IntPtr HWND_TOPMOST = (IntPtr)(-1);
    public const uint PM_REMOVE = 1;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; public uint lPrivate; }

    [StructLayout(LayoutKind.Sequential)]
    public struct INITCOMMONCONTROLSEX { public uint dwSize, dwICC; }

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEX
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground, lpszMenuName, lpszClassName, hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PAINTSTRUCT
    {
        public IntPtr hdc; public int fErase; public RECT rcPaint; public int fRestore, fIncUpdate;
        public fixed byte rgbReserved[32];
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct LOGFONT
    {
        public int lfHeight, lfWidth, lfEscapement, lfOrientation, lfWeight;
        public byte lfItalic, lfUnderline, lfStrikeOut, lfCharSet, lfOutPrecision, lfClipPrecision, lfQuality, lfPitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string lfFaceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] public delegate bool EnumResNameProc(IntPtr hModule, IntPtr type, IntPtr name, IntPtr lParam);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] public delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] public delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] public delegate IntPtr DlgProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    // kernel32
    [DllImport("kernel32", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)] public static extern IntPtr LoadLibraryEx(string file, IntPtr hFile, uint flags);
    [DllImport("kernel32", SetLastError = true)] public static extern bool FreeLibrary(IntPtr h);
    [DllImport("kernel32", EntryPoint = "EnumResourceNamesW", SetLastError = true)] public static extern bool EnumResourceNames(IntPtr hModule, IntPtr type, EnumResNameProc proc, IntPtr lParam);
    [DllImport("kernel32", EntryPoint = "FindResourceW", SetLastError = true)] public static extern IntPtr FindResource(IntPtr hModule, IntPtr name, IntPtr type);
    [DllImport("kernel32", SetLastError = true)] public static extern IntPtr LoadResource(IntPtr hModule, IntPtr hRes);
    [DllImport("kernel32")] public static extern IntPtr LockResource(IntPtr hGlobal);
    [DllImport("kernel32")] public static extern uint SizeofResource(IntPtr hModule, IntPtr hRes);
    [DllImport("kernel32", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32")] public static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32")] public static extern void Sleep(uint ms);

    // comctl32 / dwm
    [DllImport("comctl32")] public static extern bool InitCommonControlsEx(ref INITCOMMONCONTROLSEX icc);
    [DllImport("dwmapi")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);

    // user32
    [DllImport("user32", EntryPoint = "GetClassInfoExW", CharSet = CharSet.Unicode)] public static extern bool GetClassInfoEx(IntPtr hInst, string name, ref WNDCLASSEX wc);
    [DllImport("user32", EntryPoint = "RegisterClassExW", SetLastError = true)] public static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32", EntryPoint = "DefWindowProcW")] public static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32", EntryPoint = "CreateDialogIndirectParamW", SetLastError = true)] public static extern IntPtr CreateDialogIndirectParam(IntPtr hInst, byte* template, IntPtr parent, IntPtr dlgProc, IntPtr param);
    [DllImport("user32")] public static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32", EntryPoint = "PeekMessageW")] public static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32")] public static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32", EntryPoint = "DispatchMessageW")] public static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32", EntryPoint = "SendMessageW")] public static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32")] public static extern bool GetClientRect(IntPtr hwnd, out RECT r);
    [DllImport("user32")] public static extern bool ClientToScreen(IntPtr hwnd, ref POINT p);
    [DllImport("user32")] public static extern bool MapDialogRect(IntPtr hwnd, ref RECT r);
    [DllImport("user32")] public static extern bool EnumChildWindows(IntPtr hwnd, EnumChildProc proc, IntPtr lParam);
    [DllImport("user32", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, char* buf, int max);
    [DllImport("user32", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, char* buf, int max);
    [DllImport("user32", EntryPoint = "GetWindowTextLengthW")] public static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32")] public static extern int GetDlgCtrlID(IntPtr hwnd);
    [DllImport("user32")] public static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32")] public static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32")] public static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT ps);
    [DllImport("user32")] public static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT ps);
    [DllImport("user32")] public static extern bool GetClientRect(IntPtr hwnd, RECT* r);
    [DllImport("user32")] public static extern int FillRect(IntPtr hdc, ref RECT r, IntPtr brush);
    [DllImport("user32", EntryPoint = "DrawTextW", CharSet = CharSet.Unicode)] public static extern int DrawText(IntPtr hdc, string text, int len, ref RECT r, uint format);
    [DllImport("user32")] public static extern IntPtr LoadCursor(IntPtr hInst, IntPtr name);
    [DllImport("user32")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    // gdi32
    [DllImport("gdi32", EntryPoint = "GetObjectW")] public static extern int GetObject(IntPtr h, int cb, out LOGFONT lf);
    [DllImport("gdi32")] public static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32")] public static extern IntPtr CreatePen(int style, int width, uint color);
    [DllImport("gdi32")] public static extern IntPtr GetStockObject(int obj);
    [DllImport("gdi32")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32")] public static extern bool Rectangle(IntPtr hdc, int l, int t, int r, int b);
    [DllImport("gdi32")] public static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32")] public static extern uint SetTextColor(IntPtr hdc, uint color);
    [DllImport("gdi32")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32")] public static extern bool DeleteDC(IntPtr hdc);
}
