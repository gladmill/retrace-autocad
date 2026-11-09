using System.Runtime.InteropServices;

namespace Retrace.AutoCad;

/// <summary>
/// Which modal window opened on top of a dialog. AutoCAD raises <c>EnterModal</c> / <c>LeaveModal</c> for the first modal window only, not for one that a
/// button of that dialog opens (seen with Text Style > New... and with a WinForms dialog shown through <c>Application.ShowModalDialog</c> from a
/// button handler), so the recorder and the replay look at the windows themselves. A modal window disables its owner, so nothing is looked for while the
/// dialog is enabled. UI thread only.
/// </summary>
static class ModalAbove
{
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    const uint GW_OWNER = 4;

    /// <summary>Is <paramref name="h"/> a window of Retrace's own (its replay window, a prompt or settings window, any WPF window whose root is a class of this assembly)? Never a dialog under test.</summary>
    internal static bool IsRetraceWindow(IntPtr h)
    {
        try { return System.Windows.Interop.HwndSource.FromHwnd(h)?.RootVisual?.GetType().Assembly == typeof(ModalAbove).Assembly; }
        catch (Exception) { return false; }
    }

    /// <summary>The modal window open on top of <paramref name="dialog"/>, or zero. A candidate is a visible, enabled window of the dialog's thread with a title bar
    /// (tooltips, drop-down lists and other pop-ups have none) that is neither <paramref name="dialog"/> itself, AutoCAD's <paramref name="main"/> window, one of Retrace's own windows
    /// (<paramref name="ignore"/>, or any WPF window of this assembly: the recorder has no handle to give) nor one <paramref name="skip"/> names. Of the candidates: the one <paramref name="dialog"/> owns, else the one in front, else
    /// (<paramref name="onlyCandidate"/>) the only one.</summary>
    public static IntPtr Find(IntPtr dialog, IntPtr main, IntPtr ignore, Func<IntPtr, bool>? skip = null, bool onlyCandidate = false)
    {
        if (!Native.Exists(dialog) || Native.IsEnabled(dialog)) return IntPtr.Zero;
        var front = Native.ForegroundOfThisProcess();
        IntPtr owned = IntPtr.Zero, shown = IntPtr.Zero;
        var candidates = new List<IntPtr>();
        foreach (var w in Native.WindowsOfThread(GetWindowThreadProcessId(dialog, out _)))
        {
            var h = (IntPtr)w.Handle;
            if (h == dialog || h == main || h == ignore || !w.Visible || !w.Enabled || !w.Captioned || w.Retrace) continue;
            if (skip != null && skip(h)) continue;
            candidates.Add(h);
            if (owned == IntPtr.Zero && GetWindow(h, GW_OWNER) == dialog) owned = h;
            if (h == front) shown = h;
        }
        return owned != IntPtr.Zero ? owned : shown != IntPtr.Zero ? shown : onlyCandidate && candidates.Count == 1 ? candidates[0] : IntPtr.Zero;
    }
}
