using Autodesk.AutoCAD.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Retrace.AutoCad;

/// <summary>
/// The checkpoint shortcut. <see cref="Recorder"/> calls <see cref="TryHandle"/> from its PreTranslateMessage handler, which is attached only
/// while recording, so with no recording the key is never looked at. A match swallows the key press and takes the checkpoint right after,
/// outside any command (<see cref="Commands.MarkFromKey"/>): no command is typed, so the user's command keeps running and text half-typed
/// on the command line or at a text prompt stays as it was.
/// </summary>
static class CheckpointKey
{
    const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
    const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;

    static KeyChord? _chord;

    /// <summary>Reads the setting again (when a recording starts and when Settings are saved). A bad value is logged and the default is used.</summary>
    public static void Reload()
    {
        var chord = RetraceSettings.Load().CheckpointChord(out var warning);
        if (warning.Length > 0) Log.Info(warning);
        _chord = chord;
    }

    /// <summary>true if the message was the shortcut and was queued: the caller marks it handled. Allocation-free for any other message.</summary>
    public static bool TryHandle(int message, IntPtr wParam, IntPtr lParam)
    {
        if (message != WM_KEYDOWN && message != WM_SYSKEYDOWN) return false;
        var c = _chord;
        if (c == null || (int)wParam != c.Value.Vk) return false;
        if (Down(VK_CONTROL) != c.Value.Ctrl || Down(VK_SHIFT) != c.Value.Shift || Down(VK_MENU) != c.Value.Alt) return false;
        if (Recorder.Instance.ModalOpen) return false;   // a dialog is open: the key is the dialog's
        if (((long)lParam & (1L << 30)) != 0) return true;   // auto-repeat: swallowed, not queued again
        Request();
        return true;
    }

    static bool Down(int vk) => (Win32.GetKeyState(vk) & 0x8000) != 0;

    static void Request()
    {
        try
        {
            // after the key message is handled; still on the UI thread, while AutoCAD's own loop (a prompt's, too) pumps messages
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                () => Log.Safe("checkpoint shortcut", () => Commands.Instance.MarkFromKey(CursorContext.Now())));
        }
        catch (System.Exception ex) { Log.Error("checkpoint shortcut", ex); }
    }
}
