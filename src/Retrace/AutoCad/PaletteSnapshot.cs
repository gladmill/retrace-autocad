using System.Runtime.InteropServices;
using System.Windows.Interop;
using Retrace.Core;

namespace Retrace.AutoCad;

/// <summary>
/// Reads the palette pages of AutoCAD's process as they are now, for the recorder's "this command opened a palette" mark (<see cref="PaletteOpening"/> decides). A page is a window titled with the palette's name
/// whose parent is the palette set (AfxWnd) and whose grandparent is a control bar (AfxControlBar), the same chain <see cref="PaletteFill"/> looks for. The ribbon (hosted the same way) and Retrace's own guide are left out.
/// Read-only: nothing is moved, shown or clicked. UI thread only.
/// </summary>
static class PaletteSnapshot
{
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr h, EnumProc cb, IntPtr data);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsChild(IntPtr parent, IntPtr h);
    delegate bool EnumProc(IntPtr h, IntPtr data);

    /// <summary>Every palette page of this process: shown or not, with the rectangle its picture would cover (null when it is not shown).</summary>
    public static List<PaletteState> Read()
    {
        var list = new List<PaletteState>();
        try
        {
            uint me = (uint)Environment.ProcessId;
            var ribbon = RibbonHost();
            EnumWindows((top, _) =>
            {
                GetWindowThreadProcessId(top, out var pid);
                if (pid != me) return true;
                EnumChildWindows(top, (h, _) =>
                {
                    if (!DialogReader.ClassOf(h).StartsWith("AfxWnd", StringComparison.Ordinal)) return true;
                    var name = Native.TitleOf(h);
                    if (name.Length == 0) return true;
                    var set = DialogReader.GetParent(h);
                    if (!DialogReader.ClassOf(set).StartsWith("AfxWnd", StringComparison.Ordinal) || !DialogReader.ClassOf(DialogReader.GetParent(set)).StartsWith("AfxControlBar", StringComparison.Ordinal)) return true;
                    if (ribbon != IntPtr.Zero && (ribbon == h || IsChild(h, ribbon))) return true;
                    var setTitle = Native.TitleOf(set);
                    if (setTitle == Retrace.Ui.GuidePalette.SetTitle) return true;
                    bool shown = PaletteReveal.IsShown(h);
                    PxRect? rect = shown && PaletteReveal.PictureRect(h) is { } r ? new PxRect(r.Left, r.Top, r.Right, r.Bottom) : null;
                    list.Add(new PaletteState(h.ToInt64(), name, setTitle, shown, rect));
                    return true;
                }, IntPtr.Zero);
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) { Log.Error("palette snapshot", ex); }
        return list;
    }

    static IntPtr RibbonHost()
    {
        try { return Autodesk.Windows.ComponentManager.Ribbon is { } r && System.Windows.PresentationSource.FromVisual(r) is HwndSource src ? src.Handle : IntPtr.Zero; }
        catch (Exception) { return IntPtr.Zero; }
    }
}
