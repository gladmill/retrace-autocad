using System;
using System.Collections.Generic;

namespace AcadStatus.Core;

/// <summary>A top-level window of AutoCAD's UI thread, as far as choosing the dialog needs it.</summary>
/// <param name="OwnerHandle">The window that owns it (the main frame for a dialog AutoCAD opened), 0 for none.</param>
/// <param name="VisibleChildClasses">Window classes of its visible child windows; only filled for standard dialogs (#32770), which is where
/// the message-box rule needs them.</param>
public sealed record WindowInfo(long Handle, string Class, bool Visible, bool Enabled, bool Captioned, bool ToolWindow, long OwnerHandle,
    IReadOnlyList<string> VisibleChildClasses);

/// <summary>The window classes of a dialog's texts and buttons. Win32 and MFC dialogs use "Static" and "Button"; WinForms dialogs use
/// "WindowsForms10.STATIC.&lt;app&gt;" and "WindowsForms10.BUTTON.&lt;app&gt;". WPF dialogs have no child windows at all, and a Windows task
/// dialog draws its texts and buttons itself: both are title-only (reading them would need UI Automation, which must not be called from
/// the UI thread that owns the window: it can deadlock).</summary>
public static class DialogClasses
{
    public static bool IsText(string cls) =>
        cls == "Static" || cls.StartsWith("WindowsForms10.STATIC.", StringComparison.OrdinalIgnoreCase);

    public static bool IsButton(string cls) =>
        cls == "Button" || cls.StartsWith("WindowsForms10.BUTTON.", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Which window is "the dialog that is in the way" (CONTRACT.md: a dialog or message box). Pure, so the rule is tested.</summary>
public static class DialogPick
{
    /// <summary>Is a window of these children a message box: only texts and buttons, with a button.</summary>
    public static bool IsMessageBox(IReadOnlyList<string> visibleChildClasses)
    {
        bool button = false;
        foreach (var c in visibleChildClasses)
        {
            if (DialogClasses.IsButton(c)) button = true;
            else if (!DialogClasses.IsText(c)) return false;
        }
        return button;
    }

    static bool IsNoise(string cls) =>
        cls == "tooltips_class32" || cls == "#32768" || cls == "IME" || cls.StartsWith("MSCTFIME", StringComparison.Ordinal);

    /// <summary>Can this window be the dialog? While the main window is disabled (<paramref name="modal"/>): a visible, enabled, captioned
    /// dialog of the usual kinds (standard dialog, WinForms, WPF) that is not a tool window. Otherwise only a message box, so a modeless
    /// palette or dialog that stays open for hours never counts.</summary>
    public static bool IsCandidate(WindowInfo w, bool modal)
    {
        if (!w.Visible || !w.Enabled || IsNoise(w.Class)) return false;
        if (modal)
        {
            bool kind = w.Class == "#32770" || w.Class.StartsWith("WindowsForms10.Window", StringComparison.Ordinal)
                        || w.Class.StartsWith("HwndWrapper[", StringComparison.Ordinal);
            return kind && w.Captioned && !w.ToolWindow;
        }
        return w.Class == "#32770" && IsMessageBox(w.VisibleChildClasses);
    }

    /// <summary>The dialog among <paramref name="windows"/> (front to back), or null.
    /// While the main window is disabled the real modal is the one Windows says is the frame's last active popup, or the thread's active window:
    /// <paramref name="preferred"/> lists those handles, best first, and the first that is a candidate wins. Only when none is, the front-most
    /// candidate owned by the main frame wins, then the front-most candidate at all (a modeless window may be one of these).</summary>
    public static WindowInfo? Pick(IEnumerable<WindowInfo> windows, bool modal, long mainFrame, IReadOnlyList<long> preferred)
    {
        var candidates = new List<WindowInfo>();
        foreach (var w in windows)
            if (w.Handle != mainFrame && IsCandidate(w, modal)) candidates.Add(w);
        if (candidates.Count == 0) return null;
        if (modal)
        {
            foreach (var h in preferred)
                foreach (var w in candidates)
                    if (w.Handle == h) return w;
            foreach (var w in candidates)
                if (w.OwnerHandle == mainFrame && mainFrame != 0) return w;
        }
        return candidates[0];
    }

    /// <summary>The handle of the stand-in for a modal window nothing could be found for: the same one every tick, so the 15 s clock runs.</summary>
    public const long UnknownModalHandle = -1;

    public const string UnknownModalTitle = "(unknown modal window)";

    /// <summary>The dialog entry for a main window that is disabled while no window of the usual kinds is found (a modal window of a kind the
    /// scan does not know, a hidden one, one of another thread): AutoCAD cannot take input then, and "idle" would be wrong. Title and class
    /// say so, texts and buttons are empty, and the clock is keyed on "the main window is disabled" (the constant handle).</summary>
    public static DialogSnap UnknownModal() =>
        new(UnknownModalHandle, UnknownModalTitle, "", true, Array.Empty<string>(), Array.Empty<string>());

    /// <summary>What the scan reports: the picked dialog, else, while the main window is disabled, <see cref="UnknownModal"/>, else nothing.</summary>
    public static DialogSnap? OrUnknownModal(DialogSnap? picked, bool modal) => picked ?? (modal ? UnknownModal() : null);
}
