using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using static Retrace.AutoCad.Win32;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Retrace.AutoCad;

/// <summary>
/// RETRACECAPTUREDCL: shows every dialog of the given DCL files in AutoCAD, one at a time, and writes what Windows drew
/// (&lt;stem&gt;__&lt;dialog&gt;.png) plus its geometry (.json, same fields as dialog-capture) for calibrating the DCL wireframes.
///
/// The only LISP is one generated line per dialog (load_dialog/new_dialog/start_dialog: DCL has no other API). Everything else runs
/// here: a dispatcher timer keeps ticking inside start_dialog's modal loop, finds the new window, captures it and closes it
/// (WM_CLOSE, which DCL treats as Cancel; IDCANCEL if that is ignored).
/// </summary>
public sealed unsafe class DclCapture
{
    // process-wide, not a static: every RETRACECAPTUREDCL run loads a fresh copy of this assembly
    const string RunningKey = "Retrace.dclcapture.running";
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    const int SettleMs = 600, AppearTimeoutMs = 10000, CloseTimeoutMs = 4000;
    const uint WM_CLOSE = 0x0010, WM_COMMAND = 0x0111, IDCANCEL = 2;
    const long WS_CAPTION = 0x00C00000;

    enum State { Next, Waiting, Settling, Closing }

    readonly Document _doc;
    readonly Queue<DclDialog> _queue;
    readonly string _out;
    readonly int _total;
    readonly List<string> _failed = new();
    readonly DispatcherTimer _timer;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    State _state = State.Next;
    DclDialog? _job;
    HashSet<IntPtr> _before = new();
    IntPtr _hwnd;
    long _since;
    bool _lispDone;
    int _ok;

    public static void Run()
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument ?? throw new InvalidOperationException("open a drawing first");
        var ed = doc.Editor;
        if (AppDomain.CurrentDomain.GetData(RunningKey) != null) { ed.WriteMessage("\nRETRACECAPTUREDCL: a capture is already running.\n"); return; }

        // default: the folder AutoCAD finds acad.dcl in on its support path (the user's Support folder in 2026)
        string support;
        try { support = Path.GetDirectoryName(HostApplicationServices.Current.FindFile("acad.dcl", doc.Database, FindFileHint.Default))!; }
        catch { support = Path.Combine(Path.GetDirectoryName(Process.GetCurrentProcess().MainModule!.FileName)!, "Support"); }
        var src = Ask(ed, $"\nDCL file or folder <{support}>: ", support);
        if (src == null) return;
        var outDir = Ask(ed, $"\nOutput folder <{DefaultOut}>: ", DefaultOut);
        if (outDir == null) return;
        var filter = Ask(ed, "\nOnly dialogs whose file or name contains <all>: ", "");
        if (filter == null) return;

        if (!File.Exists(src) && !Directory.Exists(src)) { ed.WriteMessage($"\nRETRACECAPTUREDCL: not found: {src}\n"); return; }
        var jobs = DclList.In(src).Where(d => filter == "" || d.FormId.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (jobs.Count == 0) { ed.WriteMessage($"\nRETRACECAPTUREDCL: no dialogs in {src}.\n"); return; }
        Directory.CreateDirectory(outDir);
        ed.WriteMessage($"\nRETRACECAPTUREDCL: {jobs.Count} dialogs -> {outDir}. Don't touch AutoCAD until it says done.\n");
        AppDomain.CurrentDomain.SetData(RunningKey, new DclCapture(doc, jobs, outDir));
    }

    /// <summary>The captures folder of the Retrace settings.</summary>
    static string DefaultOut => RetraceSettings.Load().Captures;

    static string? Ask(Editor ed, string msg, string def)
    {
        var r = ed.GetString(new PromptStringOptions(msg) { AllowSpaces = true });
        if (r.Status == PromptStatus.Cancel) return null;
        var s = r.StringResult?.Trim().Trim('"');
        return string.IsNullOrEmpty(s) ? def : s;
    }

    DclCapture(Document doc, List<DclDialog> jobs, string outDir)
    {
        _doc = doc; _queue = new Queue<DclDialog>(jobs); _total = jobs.Count; _out = outDir;
        _doc.LispEnded += OnLispEnded;
        _doc.LispCancelled += OnLispEnded;
        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Log($"--- {DateTime.Now:s} {_total} dialogs");
    }

    void OnLispEnded(object? s, EventArgs e) => _lispDone = true;

    long Now => _clock.ElapsedMilliseconds;

    void Tick()
    {
        try
        {
            switch (_state)
            {
                case State.Next:
                    if (!_queue.TryDequeue(out var job)) { Finish(null); return; }
                    _job = job; _before = TopWindows(); _lispDone = false; _hwnd = IntPtr.Zero; _since = Now;
                    _doc.SendStringToExecute(Lisp(job) + "\n", true, false, false);
                    _state = State.Waiting;
                    break;

                case State.Waiting:
                    _hwnd = TopWindows().FirstOrDefault(h => !_before.Contains(h) && IsDialogLike(h));
                    if (_hwnd != IntPtr.Zero) { _state = State.Settling; _since = Now; }
                    else if (_lispDone) Fail("no dialog appeared (load_dialog or new_dialog failed)");
                    else if (Now - _since > AppearTimeoutMs) Fail("no dialog appeared within 10 s");
                    break;

                case State.Settling:
                    if (Now - _since < SettleMs) break;
                    try { Capture(_hwnd, _job!); _ok++; }
                    catch (Exception ex) { _failed.Add($"{_job!.FormId}: capture failed: {ex.Message}"); Log(_failed[^1]); }
                    PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    _state = State.Closing; _since = Now;
                    break;

                case State.Closing:
                    if (!IsWindow(_hwnd) && _lispDone) { _state = State.Next; break; }
                    if (Now - _since > CloseTimeoutMs * 2)
                        Finish($"{_job!.FormId} would not close; close it by hand (Esc), then rerun with a filter for the rest.");
                    else if (Now - _since > CloseTimeoutMs && IsWindow(_hwnd))
                        PostMessage(_hwnd, WM_COMMAND, (IntPtr)IDCANCEL, IntPtr.Zero);
                    break;
            }
        }
        catch (Exception ex) { Finish("stopped: " + ex.Message); }
    }

    void Fail(string why)
    {
        _failed.Add($"{_job!.FormId}: {why}");
        Log(_failed[^1]);
        _state = State.Next;
    }

    void Finish(string? error)
    {
        _timer.Stop();
        _doc.LispEnded -= OnLispEnded;
        _doc.LispCancelled -= OnLispEnded;
        AppDomain.CurrentDomain.SetData(RunningKey, null);
        var msg = $"RETRACECAPTUREDCL: done, {_ok} of {_total} captured" + (_failed.Count > 0 ? $", {_failed.Count} failed (see capture.log)" : "") + $" -> {_out}"
                  + (error != null ? $"\nRETRACECAPTUREDCL: {error}" : "");
        Log(msg);
        try { _doc.Editor.WriteMessage("\n" + msg + "\n"); } catch { }
    }

    void Log(string line)
    {
        try { File.AppendAllText(Path.Combine(_out, "capture.log"), line + Environment.NewLine); } catch { }
    }

    /// <summary>The one line of LISP per dialog. Paths use forward slashes, which load_dialog accepts and which need no escaping.</summary>
    public static string Lisp(DclDialog d) =>
        $"(progn (setq dclcap_id (load_dialog \"{d.File.Replace('\\', '/').Replace("\"", "\\\"")}\"))"
        + $" (if (and (>= dclcap_id 0) (new_dialog \"{d.Name}\" dclcap_id)) (start_dialog))"
        + " (if (>= dclcap_id 0) (unload_dialog dclcap_id)) (setq dclcap_id nil) (princ))";

    // ---- windows ---------------------------------------------------------------------------------

    static HashSet<IntPtr> TopWindows()
    {
        var pid = (uint)Environment.ProcessId;
        var set = new HashSet<IntPtr>();
        EnumWindowsProc cb = (h, _) =>
        {
            if (IsWindowVisible(h) && GetWindowThreadProcessId(h, out var p) != 0 && p == pid) set.Add(h);
            return true;
        };
        EnumWindows(cb, IntPtr.Zero);
        GC.KeepAlive(cb);
        return set;
    }

    static bool IsDialogLike(IntPtr h) => ((long)GetWindowLongPtr(h, GWL_STYLE) & WS_CAPTION) == WS_CAPTION;

    // ---- capture (geometry fields as in dialog-capture/DialogCapture/Program.cs) -----------------

    void Capture(IntPtr hwnd, DclDialog job)
    {
        var outBase = Path.Combine(_out, $"{Sanitize(job.Stem)}__{Sanitize(job.Name)}");
        GetWindowRect(hwnd, out var win);
        string boundsSource = "DWMWA_EXTENDED_FRAME_BOUNDS";
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var vis, sizeof(RECT)) != 0) { vis = win; boundsSource = "GetWindowRect (DWM call failed)"; }
        SaveScreenshot(vis, outBase + ".png");

        GetClientRect(hwnd, out var client);
        var origin = new POINT(); ClientToScreen(hwnd, ref origin);
        var units = new RECT { Left = 0, Top = 0, Right = 4, Bottom = 8 };
        bool haveUnits = MapDialogRect(hwnd, ref units);

        object? font = null;
        // DCL dialog windows (class adesk_dlg*) report no font of their own; the tiles carry it
        var hFont = SendMessage(hwnd, WM_GETFONT, IntPtr.Zero, IntPtr.Zero);
        string fontSource = "dialog";
        if (hFont == IntPtr.Zero)
        {
            EnumChildProc fcb = (h, _) => { hFont = SendMessage(h, WM_GETFONT, IntPtr.Zero, IntPtr.Zero); return hFont == IntPtr.Zero; };
            EnumChildWindows(hwnd, fcb, IntPtr.Zero);
            GC.KeepAlive(fcb);
            fontSource = "first child with a font";
        }
        if (hFont != IntPtr.Zero && GetObject(hFont, 92 /* sizeof(LOGFONTW) */, out var lf) > 0)
            font = new { face = lf.lfFaceName, height = lf.lfHeight, weight = lf.lfWeight, italic = lf.lfItalic != 0, charset = lf.lfCharSet };

        var children = new List<object>();
        EnumChildProc ecb = (h, _) =>
        {
            GetWindowRect(h, out var r);
            children.Add(new
            {
                hwnd = h.ToInt64(), @class = ClassOf(h), text = TextOf(h), id = GetDlgCtrlID(h),
                style = Hex((uint)(long)GetWindowLongPtr(h, GWL_STYLE)), exStyle = Hex((uint)(long)GetWindowLongPtr(h, GWL_EXSTYLE)),
                visible = IsWindowVisible(h), directChild = GetParent(h) == hwnd,
                rect = new { x = r.Left - origin.X, y = r.Top - origin.Y, w = r.Width, h = r.Height },
            });
            return true;
        };
        EnumChildWindows(hwnd, ecb, IntPtr.Zero);
        GC.KeepAlive(ecb);

        var info = new
        {
            dcl = job.File, name = job.Name, line = job.Line, formId = job.FormId,
            caption = TextOf(hwnd), @class = ClassOf(hwnd),
            style = Hex((uint)(long)GetWindowLongPtr(hwnd, GWL_STYLE)), exStyle = Hex((uint)(long)GetWindowLongPtr(hwnd, GWL_EXSTYLE)),
            dpi = GetDpiForWindow(hwnd), foreground = GetForegroundWindow() == hwnd, boundsSource,
            visibleBounds = new { x = vis.Left, y = vis.Top, w = vis.Width, h = vis.Height },
            windowRect = new { x = win.Left, y = win.Top, w = win.Width, h = win.Height },
            clientOrigin = new { x = origin.X - vis.Left, y = origin.Y - vis.Top },
            clientSize = new { w = client.Width, h = client.Height },
            font, fontSource,
            dialogBaseUnits = haveUnits ? new { x4 = units.Right, y8 = units.Bottom } : null,
            autocad = AcApp.Version.ToString(),
            children,
        };
        File.WriteAllText(outBase + ".json", JsonSerializer.Serialize(info, Json));
        Log($"{job.FormId}: ok {vis.Width}x{vis.Height}");
    }

    static string Hex(uint v) => "0x" + v.ToString("X8");
    static string Sanitize(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '#' ? '_' : c));

    static string ClassOf(IntPtr h)
    {
        char* buf = stackalloc char[256];
        return new string(buf, 0, GetClassName(h, buf, 256));
    }

    static string TextOf(IntPtr h)
    {
        int len = GetWindowTextLength(h);
        if (len <= 0) return "";
        var buf = new char[len + 2];
        fixed (char* p = buf) return new string(buf, 0, GetWindowText(h, p, len + 1));
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

    // ---- what Win32 lacks --------------------------------------------------------

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32")] static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32")] static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32", EntryPoint = "PostMessageW")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
