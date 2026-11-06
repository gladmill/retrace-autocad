using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using AcadStatus.Core;

namespace AcadStatus.AutoCad;

/// <summary>Finds the dialog or message box that is open on AutoCAD's UI thread, and reads its title, class, texts and button captions.
/// Read only: it sends no message that changes anything and presses nothing. Call it from the UI thread (a timer tick): the windows of
/// this thread are enumerated and their texts are read without a cross-thread call. Which window counts is decided by
/// <see cref="DialogPick"/> (pure, tested); this class only describes the windows to it.</summary>
static class DialogScan
{
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumThreadWindows(uint thread, EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc f, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetLastActivePopup(IntPtr owner);
    [DllImport("user32.dll")] static extern IntPtr GetActiveWindow();
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    const int WS_CAPTION = 0x00C00000, WS_EX_TOOLWINDOW = 0x00000080, BS_TYPEMASK = 0xF;
    const uint GW_OWNER = 4;
    const int MaxTexts = 10, MaxButtons = 10, MaxText = 300;

    static string ClassOf(IntPtr h) { var s = new StringBuilder(128); GetClassName(h, s, s.Capacity); return s.ToString(); }
    static string TextOf(IntPtr h) { var s = new StringBuilder(512); GetWindowText(h, s, s.Capacity); return s.ToString(); }

    /// <summary>The dialog of this thread, or null. <paramref name="mainFrame"/> is AutoCAD's main window: while it is disabled the dialog is
    /// modal. <paramref name="exclude"/> is a window of this add-in (the details window) that never counts.</summary>
    public static DialogSnap? Find(IntPtr mainFrame, IntPtr exclude)
    {
        bool modal = mainFrame != IntPtr.Zero && IsWindow(mainFrame) && !IsWindowEnabled(mainFrame);
        var windows = new List<WindowInfo>();
        EnumThreadWindows(GetCurrentThreadId(), (h, _) =>
        {
            if (h == mainFrame || h == exclude || !IsWindowVisible(h)) return true;
            var cls = ClassOf(h);
            var style = GetWindowLong(h, GWL_STYLE);
            var ex = GetWindowLong(h, GWL_EXSTYLE);
            // the children are only needed to tell a message box from another dialog, and only while the main window is enabled
            IReadOnlyList<string> children = !modal && cls == "#32770" ? VisibleChildClasses(h) : Array.Empty<string>();
            windows.Add(new WindowInfo((long)h, cls, true, IsWindowEnabled(h), (style & WS_CAPTION) == WS_CAPTION, (ex & WS_EX_TOOLWINDOW) != 0,
                (long)GetWindow(h, GW_OWNER), children));
            return true;
        }, IntPtr.Zero);

        // Windows knows the real modal: the last popup the main window's owner activated, and the thread's active window
        var preferred = new List<long>();
        if (modal)
        {
            var popup = GetLastActivePopup(mainFrame);
            if (popup != IntPtr.Zero && popup != mainFrame) preferred.Add((long)popup);
            var active = GetActiveWindow();
            if (active != IntPtr.Zero && active != mainFrame) preferred.Add((long)active);
        }
        var picked = DialogPick.Pick(windows, modal, (long)mainFrame, preferred);
        if (picked == null) return DialogPick.OrUnknownModal(null, modal);   // disabled main window, no window found: still "in the way"

        var found = (IntPtr)picked.Handle;
        var texts = new List<string>();
        var buttons = new List<string>();
        EnumChildWindows(found, (c, _) =>
        {
            if (!IsWindowVisible(c)) return true;
            var ccls = ClassOf(c);
            if (DialogClasses.IsText(ccls))
            {
                var t = TextOf(c).Trim();
                if (t.Length > 0 && texts.Count < MaxTexts) texts.Add(Limit(t));
            }
            else if (DialogClasses.IsButton(ccls) && IsPushButton(c))
            {
                var t = TextOf(c).Replace("&", "").Trim();
                if (t.Length > 0 && buttons.Count < MaxButtons) buttons.Add(Limit(t));
            }
            return true;
        }, IntPtr.Zero);
        return new DialogSnap(picked.Handle, TextOf(found).Trim(), picked.Class, modal, texts, buttons);
    }

    /// <summary>At most <see cref="MaxText"/> characters, never cut through an emoji; a lone surrogate in a window text is replaced.</summary>
    static string Limit(string s) => TextUtil.SafeTruncate(TextUtil.Sanitize(s), MaxText);

    static bool IsPushButton(IntPtr c)
    {
        var type = GetWindowLong(c, GWL_STYLE) & BS_TYPEMASK;
        return type == 0 /* BS_PUSHBUTTON */ || type == 1 /* BS_DEFPUSHBUTTON */ || type == 14 /* BS_COMMANDLINK */ || type == 15 /* BS_DEFCOMMANDLINK */;
    }

    static List<string> VisibleChildClasses(IntPtr h)
    {
        var list = new List<string>();
        EnumChildWindows(h, (c, _) =>
        {
            if (IsWindowVisible(c)) list.Add(ClassOf(c));
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
