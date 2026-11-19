using Retrace.Core;

namespace Retrace.AutoCad;

/// <summary>Retrace's own windows out of the way for one picture of AutoCAD (the evidence camera and the replay's evidence pictures): every visible window of Retrace's own is hidden and, when it floats, so is the Guide palette. Always paired
/// with <see cref="Restore"/> in a finally: the windows are restored by the very method that hid them, never left to a later tick.</summary>
sealed class PictureWindows
{
    readonly List<IntPtr> _windows;
    readonly bool _guide;

    PictureWindows(List<IntPtr> windows, bool guide) { _windows = windows; _guide = guide; }

    /// <summary>Something was hidden (a window of Retrace's own or the floating Guide palette): the screen needs a moment before the picture.</summary>
    public bool AnyHidden => _windows.Count > 0 || _guide;

    /// <summary>Hides them (UI thread). Never throws. <paramref name="guide"/> false: the Guide palette is not touched (a picture taken while a command's modal dialog is open: showing a palette again may take the activation from the dialog).</summary>
    public static PictureWindows Hide(bool guide = true)
    {
        bool hiddenGuide = false;
        if (guide) { try { hiddenGuide = Retrace.Ui.GuidePalette.HideFloating(); } catch (Exception ex) { Log.Error("hide guide palette", ex); } }
        return new PictureWindows(Native.HideRetraceWindows(), hiddenGuide);
    }

    /// <summary>Shows them again, without taking the keyboard focus from AutoCAD. Calling it twice does nothing the second time.</summary>
    public void Restore()
    {
        try { Native.RestoreWindows(_windows); _windows.Clear(); } catch (Exception ex) { Log.Error("restore retrace windows", ex); }
        try { if (_guide) Retrace.Ui.GuidePalette.ShowAgain(); } catch (Exception ex) { Log.Error("restore guide palette", ex); }
    }
}

/// <summary>The evidence camera's shortcut (Ctrl+Shift+F12): one place that knows it, so the Evidence window handles it and the recorder does not count it as the person's input.</summary>
static class EvidenceKey
{
    const int WmKeyDown = 0x0100, WmSysKeyDown = 0x0104, VkF12 = 0x7B, VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12;

    /// <summary>An Evidence window is open and the shortcut is ours (not also the checkpoint shortcut).</summary>
    public static volatile bool Active;

    /// <summary>The shortcut the setting for the checkpoint key names, when it is the evidence chord: then the checkpoint wins.</summary>
    public static bool CheckpointUsesIt()
    {
        var c = RetraceSettings.Load().CheckpointChord(out _);
        return c is { } k && k.Vk == VkF12 && k.Ctrl && k.Shift && !k.Alt;
    }

    /// <summary>True for the key-down message of the chord while it is ours. Allocation-free for any other message.</summary>
    public static bool IsChord(int message, IntPtr wParam)
    {
        if (!Active || message != WmKeyDown && message != WmSysKeyDown || (int)wParam != VkF12) return false;
        return Down(VkControl) && Down(VkShift) && !Down(VkMenu);
    }

    static bool Down(int vk) => (Win32.GetKeyState(vk) & 0x8000) != 0;
}
