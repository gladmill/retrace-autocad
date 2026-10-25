using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using static DialogCapture.Native;

namespace DialogCapture;

// DialogCapture <dir-with-dlls> <out-dir> [--only <dllname>:<dialogname>] [--hide-console]
//
// Loads each resource-only DLL as data, creates every RT_DIALOG as a real modeless Win32 dialog on the
// interactive desktop, and saves a screenshot (as DWM draws it) plus a <dialog>.json with the geometry.
internal static unsafe class Program
{
    const uint ICC_ALL = 0xFFFF;
    const int PumpMs = 300;

    static readonly DlgProc DlgProcInstance = (h, m, w, l) => m == WM_INITDIALOG ? (IntPtr)1 : IntPtr.Zero;
    static readonly IntPtr DlgProcPtr = Marshal.GetFunctionPointerForDelegate(DlgProcInstance);
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    sealed record ResKey(int? Id, string? Name)
    {
        public string Key => Id is int i ? i.ToString() : Name!.ToUpperInvariant();
        public IntPtr Ptr() => Id is int i ? (IntPtr)i : Marshal.StringToHGlobalUni(Name!);
    }

    sealed record Known(string Name, string? FormId);

    static StreamWriter? progress;

    static int Main(string[] args)
    {
        string? dllDir = null, outDir = null, onlyDll = null, onlyDialog = null;
        bool hideConsole = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--only" && i + 1 < args.Length)
            {
                var spec = args[++i];
                int c = spec.IndexOf(':');
                onlyDll = c < 0 ? spec : spec[..c];
                onlyDialog = c < 0 ? null : spec[(c + 1)..];
            }
            else if (args[i] == "--hide-console") hideConsole = true;
            else if (dllDir == null) dllDir = args[i];
            else if (outDir == null) outDir = args[i];
        }
        if (dllDir == null || outDir == null)
        {
            Console.Error.WriteLine("usage: DialogCapture <dir-with-dlls> <out-dir> [--only <dllname>:<dialogname>] [--hide-console]");
            return 2;
        }
        dllDir = Path.GetFullPath(dllDir);
        outDir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(outDir);
        var marker = Path.Combine(outDir, "done.txt");
        File.Delete(marker);                                    // run-capture.ps1 waits for this file
        progress = new StreamWriter(Path.Combine(outDir, "progress.log"), append: false) { AutoFlush = true };

        if (hideConsole) { var con = GetConsoleWindow(); if (con != IntPtr.Zero) ShowWindow(con, SW_HIDE); }

        var started = DateTime.Now;
        int ok = 0, failed = 0;
        var failures = new List<string>();
        try
        {
            var icc = new INITCOMMONCONTROLSEX { dwSize = (uint)sizeof(INITCOMMONCONTROLSEX), dwICC = ICC_ALL };
            if (!InitCommonControlsEx(ref icc)) Log("WARN InitCommonControlsEx failed");
            foreach (var lib in new[] { "riched20.dll", "msftedit.dll" }) LoadLibraryEx(lib, IntPtr.Zero, 0);

            var names = LoadNames(dllDir);
            var dlls = Directory.GetFiles(dllDir, "*.dll").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            if (onlyDll != null)
                dlls = dlls.Where(f => Path.GetFileNameWithoutExtension(f).Equals(Path.GetFileNameWithoutExtension(onlyDll), StringComparison.OrdinalIgnoreCase)).ToList();
            Log($"{dlls.Count} DLL(s) in {dllDir}");

            foreach (var dll in dlls)
            {
                var dllName = Path.GetFileName(dll);
                var hMod = LoadLibraryEx(dll, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE);
                if (hMod == IntPtr.Zero)
                {
                    failed++; failures.Add($"{dllName}: LoadLibraryEx failed (error {Marshal.GetLastWin32Error()})");
                    Log("FAIL " + failures[^1]);
                    continue;
                }
                var keys = new List<ResKey>();
                EnumResNameProc cb = (m, t, n, l) =>
                {
                    ulong v = (ulong)n;
                    keys.Add(v >> 16 == 0 ? new ResKey((int)v, null) : new ResKey(null, Marshal.PtrToStringUni(n)));
                    return true;
                };
                EnumResourceNames(hMod, RT_DIALOG, cb, IntPtr.Zero);
                GC.KeepAlive(cb);
                names.TryGetValue(dllName, out var dllNames);
                Log($"{dllName}: {keys.Count} dialog(s)");

                foreach (var key in keys)
                {
                    Known? known = null; dllNames?.TryGetValue(key.Key, out known);
                    var dlgName = known?.Name ?? (key.Id is int id ? $"id_{id}" : key.Name!);
                    if (onlyDialog != null && !Matches(onlyDialog, dlgName, key)) continue;
                    var baseName = Sanitize(Path.GetFileNameWithoutExtension(dllName) + "__" + dlgName);
                    Log($"START {dllName}:{dlgName}");
                    try
                    {
                        CaptureOne(hMod, key, dllName, dlgName, known?.FormId, Path.Combine(outDir, baseName));
                        ok++; Log($"OK    {dllName}:{dlgName}");
                    }
                    catch (Exception ex)
                    {
                        failed++; failures.Add($"{dllName}:{dlgName}: {ex.Message}");
                        Log($"FAIL  {failures[^1]}");
                    }
                }
                FreeLibrary(hMod);
            }
        }
        catch (Exception ex)
        {
            failed++; failures.Add("fatal: " + ex);
            Log("FATAL " + ex);
        }
        var summary = new StringBuilder();
        summary.AppendLine($"DialogCapture finished {DateTime.Now:s} (started {started:s})");
        summary.AppendLine($"ok: {ok}");
        summary.AppendLine($"failed: {failed}");
        summary.AppendLine($"os: {Environment.OSVersion} arch: {RuntimeInformation.ProcessArchitecture}");
        foreach (var f in failures) summary.AppendLine("  " + f);
        File.WriteAllText(marker, summary.ToString());
        Log(summary.ToString());
        return failed == 0 ? 0 : 1;
    }

    static void Log(string s)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {s}";
        Console.WriteLine(line);
        progress?.WriteLine(line);
    }

    static string Sanitize(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '#' ? '_' : c));

    static bool Matches(string want, string name, ResKey key) =>
        want.Equals(name, StringComparison.OrdinalIgnoreCase) || want.TrimStart('#').Equals(key.Key, StringComparison.OrdinalIgnoreCase);

    /// <summary>dialogs.json (written by build-res.sh) gives symbolic names: dll -> resource key -> name.</summary>
    static Dictionary<string, Dictionary<string, Known>> LoadNames(string dir)
    {
        var map = new Dictionary<string, Dictionary<string, Known>>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(dir, "dialogs.json");
        if (!File.Exists(path)) return map;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var d = new Dictionary<string, Known>(StringComparer.OrdinalIgnoreCase);
                foreach (var x in e.GetProperty("dialogs").EnumerateArray())
                {
                    var r = x.GetProperty("resource");
                    var k = r.ValueKind == JsonValueKind.Number ? r.GetInt32().ToString() : r.GetString()!.ToUpperInvariant();
                    d[k] = new Known(x.GetProperty("name").GetString()!, x.TryGetProperty("formId", out var f) ? f.GetString() : null);
                }
                map[e.GetProperty("dll").GetString()!] = d;
            }
        }
        catch (Exception ex) { Log("WARN could not read dialogs.json: " + ex.Message); }
        return map;
    }

    static void CaptureOne(IntPtr hMod, ResKey key, string dll, string name, string? formId, string outBase)
    {
        var hRes = FindResource(hMod, key.Ptr(), RT_DIALOG);
        if (hRes == IntPtr.Zero) throw new InvalidOperationException($"FindResource failed (error {Marshal.GetLastWin32Error()})");
        var hGlobal = LoadResource(hMod, hRes);
        var p = LockResource(hGlobal);
        uint size = SizeofResource(hMod, hRes);
        if (p == IntPtr.Zero || size < 18) throw new InvalidOperationException("could not lock resource");
        var bytes = new byte[size];
        Marshal.Copy(p, bytes, 0, (int)size);

        Template tpl;
        try { tpl = Template.Parse(bytes); }
        catch (Exception ex) { throw new InvalidOperationException("template parse failed: " + ex.Message); }

        var stubbed = StubClasses.Ensure(tpl.Controls.Select(c => c.ClassName));
        var mods = new List<string>();
        uint style = tpl.Style;
        bool wasChild = (style & Template.WS_CHILD) != 0;
        if (wasChild) { style &= ~Template.WS_CHILD; mods.Add("stripped WS_CHILD"); }
        if ((style & Template.DS_CONTROL) != 0) { style &= ~Template.DS_CONTROL; mods.Add("stripped DS_CONTROL"); }
        if (wasChild) { style |= Template.WS_POPUP | Template.WS_CAPTION; mods.Add("added WS_POPUP|WS_CAPTION (was a child/property page)"); }
        if (style != tpl.Style) BitConverter.TryWriteBytes(bytes.AsSpan(tpl.StyleOffset), style);

        IntPtr hwnd = IntPtr.Zero;
        fixed (byte* pb = bytes)
        {
            hwnd = CreateDialogIndirectParam(hMod, pb, IntPtr.Zero, DlgProcPtr, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                var err1 = Marshal.GetLastWin32Error();
                // second try: no instance (resources of the DLL unavailable) and tolerate controls that fail to create
                BitConverter.TryWriteBytes(bytes.AsSpan(tpl.StyleOffset), style | 0x10 /* DS_NOFAILCREATE */);
                hwnd = CreateDialogIndirectParam(IntPtr.Zero, pb, IntPtr.Zero, DlgProcPtr, IntPtr.Zero);
                if (hwnd == IntPtr.Zero) throw new InvalidOperationException($"CreateDialogIndirectParam failed (error {err1}, retry {Marshal.GetLastWin32Error()})");
                mods.Add($"first CreateDialogIndirectParam failed (error {err1}); retried with hInstance=NULL and DS_NOFAILCREATE");
            }
        }

        try
        {
            SetWindowPos(hwnd, HWND_TOPMOST, 40, 40, 0, 0, SWP_NOSIZE | SWP_SHOWWINDOW);
            ShowWindow(hwnd, SW_SHOW);
            keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, 2, UIntPtr.Zero);   // lets SetForegroundWindow succeed from a task
            SetForegroundWindow(hwnd);
            Pump(PumpMs);
            bool foreground = GetForegroundWindow() == hwnd;

            RECT win; GetWindowRect(hwnd, out win);
            string boundsSource = "DWMWA_EXTENDED_FRAME_BOUNDS";
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var vis, sizeof(RECT)) != 0) { vis = win; boundsSource = "GetWindowRect (DWM call failed)"; }

            SaveScreenshot(vis, outBase + ".png");

            GetClientRect(hwnd, out var client);
            var origin = new POINT(); ClientToScreen(hwnd, ref origin);
            var units = new RECT { Left = 0, Top = 0, Right = 4, Bottom = 8 };
            MapDialogRect(hwnd, ref units);

            object? font = null;
            var hFont = SendMessage(hwnd, WM_GETFONT, IntPtr.Zero, IntPtr.Zero);
            if (hFont != IntPtr.Zero && GetObject(hFont, 92 /* sizeof(LOGFONTW) */, out var lf) > 0)
                font = new { face = lf.lfFaceName, height = lf.lfHeight, weight = lf.lfWeight, italic = lf.lfItalic != 0, charset = lf.lfCharSet };

            var children = new List<object>();
            EnumChildProc ecb = (h, l) =>
            {
                GetWindowRect(h, out var r);
                children.Add(new
                {
                    hwnd = h.ToInt64(),
                    @class = ClassOf(h),
                    text = TextOf(h),
                    id = GetDlgCtrlID(h),
                    style = hex((uint)(long)GetWindowLongPtr(h, GWL_STYLE)),
                    exStyle = hex((uint)(long)GetWindowLongPtr(h, GWL_EXSTYLE)),
                    visible = IsWindowVisible(h),
                    directChild = GetParent(h) == hwnd,
                    rect = new { x = r.Left - origin.X, y = r.Top - origin.Y, w = r.Width, h = r.Height },
                });
                return true;
            };
            EnumChildWindows(hwnd, ecb, IntPtr.Zero);
            GC.KeepAlive(ecb);

            var info = new
            {
                dll,
                resource = key.Key,
                name,
                formId,
                caption = TextOf(hwnd),
                style = hex((uint)(long)GetWindowLongPtr(hwnd, GWL_STYLE)),
                exStyle = hex((uint)(long)GetWindowLongPtr(hwnd, GWL_EXSTYLE)),
                dpi = GetDpiForWindow(hwnd),
                foreground,
                boundsSource,
                visibleBounds = new { x = vis.Left, y = vis.Top, w = vis.Width, h = vis.Height },
                windowRect = new { x = win.Left, y = win.Top, w = win.Width, h = win.Height },
                clientOrigin = new { x = origin.X - vis.Left, y = origin.Y - vis.Top },
                clientSize = new { w = client.Width, h = client.Height },
                font,
                templateFont = new { face = tpl.FontFace, points = tpl.FontPoints },
                dialogBaseUnits = new { x4 = units.Right, y8 = units.Bottom },
                template = new { extended = tpl.Extended, title = tpl.Title, style = hex(tpl.Style), exStyle = hex(tpl.ExStyle), controls = tpl.Controls.Count },
                modifications = mods,
                stubbedClasses = stubbed,
                children,
            };
            File.WriteAllText(outBase + ".json", JsonSerializer.Serialize(info, Json));
        }
        finally
        {
            DestroyWindow(hwnd);
            Pump(30);
        }
    }

    static string hex(uint v) => "0x" + v.ToString("X8");

    static void Pump(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (PeekMessage(out var m, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                // Drop keyboard input: the synthetic ALT press/release that lets SetForegroundWindow work would
                // otherwise reach DefDlgProc as WM_SYSKEYUP and enter the system-menu modal loop, which waits for a
                // real click before the capture can continue.
                if (m.message is >= 0x0100 and <= 0x0109) continue;   // WM_KEYFIRST..WM_KEYLAST
                TranslateMessage(ref m); DispatchMessage(ref m);
            }
            else Sleep(5);
        }
    }

    static string ClassOf(IntPtr h)
    {
        char* buf = stackalloc char[256];
        int n = GetClassName(h, buf, 256);
        return new string(buf, 0, n);
    }

    static string TextOf(IntPtr h)
    {
        int len = GetWindowTextLength(h);
        if (len <= 0) return "";
        var buf = new char[len + 2];
        fixed (char* p = buf) { int n = GetWindowText(h, p, len + 1); return new string(buf, 0, n); }
    }

    static void SaveScreenshot(RECT r, string path)
    {
        int w = r.Width, h = r.Height;
        if (w <= 0 || h <= 0) throw new InvalidOperationException($"empty bounds {w}x{h}");
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var bmi = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var dib = CreateDIBSection(screen, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        try
        {
            if (dib == IntPtr.Zero) throw new InvalidOperationException("CreateDIBSection failed");
            var old = SelectObject(mem, dib);
            if (!BitBlt(mem, 0, 0, w, h, screen, r.Left, r.Top, SRCCOPY | CAPTUREBLT)) throw new InvalidOperationException("BitBlt failed");
            var px = new byte[w * h * 4];
            Marshal.Copy(bits, px, 0, px.Length);
            SelectObject(mem, old);
            Png.Write(path, w, h, px);
        }
        finally
        {
            if (dib != IntPtr.Zero) DeleteObject(dib);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }
}
