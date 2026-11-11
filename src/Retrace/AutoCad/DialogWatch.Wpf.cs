using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Retrace.Core;

namespace Retrace.AutoCad;

/// <summary>
/// The WPF half of <see cref="DialogWatch"/>: a modal WPF window of AutoCAD's process has one window and no child windows, so the Win32 press tracking sees nothing in it. Its fields and push buttons are read in process
/// from the visual tree (<see cref="WpfDialogs"/>) on this thread; UI Automation is never used on it.
/// A click is what WPF itself does: a class handler on <see cref="ButtonBase.ClickEvent"/> (class handlers run before every instance handler, and before the button's Command) sees every press of a push button,
/// whatever made it: a mouse click that comes up on the button, Space or Enter on the focused button, the default button's Enter, the cancel button's Esc, an access key with or without Alt, ClickMode Press. So no
/// heuristic decides what a key or a mouse press does (a click swallowed by an open drop-down, Enter kept by a grid or a text box that takes returns, two buttons with the same access key are not clicks because WPF
/// raises no Click). The Click counts when it follows an input of a person (<see cref="WpfClickCorrelator"/>: the mouse went down on that same button, however long it was held; a key is down or came up a moment ago); a button
/// clicked from code is no press. The fields are read in that handler, so the button's own handlers have not run. Only real push buttons count (<see cref="WpfPalette.IsPushButton"/>): a ToggleButton (check box, radio) is a field,
/// a scroll bar's arrow or a column header is no click.
/// </summary>
sealed partial class DialogWatch
{
    static bool _classHandlerRegistered;
    /// <summary>The watch that is listening (class handlers cannot be removed; this one returns at once when it is null).</summary>
    static DialogWatch? _listening;
    InputManager? _tapped;
    readonly WpfClickCorrelator _clicks = new();

    bool IsWpf(Entry e)
    {
        if (e.Hwnd == IntPtr.Zero) return false;
        if (e.Wpf == null) { e.Wpf = WpfDialogs.IsWindow(e.Hwnd); if (e.Wpf == true) WpfTap(); }
        return e.Wpf == true;
    }

    /// <summary>Called when the stack changed: stop listening when no WPF dialog is tracked any more.</summary>
    void WpfSync()
    {
        if (_tapped != null && !_stack.Items.Any(x => x.Wpf == true)) WpfUntap();
    }

    void WpfTap()
    {
        if (_tapped != null) return;
        if (!_classHandlerRegistered)
        {
            _classHandlerRegistered = true;
            EventManager.RegisterClassHandler(typeof(ButtonBase), ButtonBase.ClickEvent, new RoutedEventHandler(OnAnyClick), true);
        }
        _tapped = InputManager.Current;
        _tapped.PreProcessInput += OnWpfInput;
        _listening = this;
    }

    void WpfUntap()
    {
        if (_tapped == null) return;
        _tapped.PreProcessInput -= OnWpfInput;
        _tapped = null;
        if (ReferenceEquals(_listening, this)) _listening = null;
    }

    /// <summary>One read of a WPF dialog, in process; its source is "wpf".</summary>
    void WpfRead(Entry e, long nowMs, bool statics = false, WpfPalette.Scan? taken = null)
    {
        e.Source = "wpf";
        var scan = taken ?? WpfDialogs.Read(e.Hwnd);
        // the static texts on request (a press, the input that closes the dialog), the parts Retrace does not follow then or about once a second: a part on another tab page shows up when its page does
        // (a window that is closing or hidden shows nothing: its read keeps what the click took, and walks nothing again)
        if ((statics || nowMs - e.PartsMs >= 1000) && Native.IsVisible(e.Hwnd))
        {
            e.PartsMs = nowMs;
            var parts = scan.ReadParts(statics);
            if (statics) e.Statics = parts.Statics;   // none now: none for this event, not the last page's texts
            NotePartsOf(e, parts);
        }
        var fields = scan.Fields;
        if (fields.Count == 0) return;
        e.FieldSeen = true; e.Tracker.Observe(fields);
        if (e.Settle.OnRead(nowMs)) e.Tracker.Rebase();   // the first read after a click, when no input has come first: what the handler did is not the person's change
    }

    /// <summary>The parts of the window Retrace does not follow are kept for the dialog's `unsupported`, and a new one is named in the log, once (by <see cref="WpfPresses.Unsupported.Key"/>).</summary>
    static void NotePartsOf(Entry e, WpfPalette.Parts parts) => NoteParts(e, parts.Unsupported, "wpf");

    /// <summary>Parts a Win32 dialog holds that no message reads (MFC Feature Pack tabs, property grids and toolbars: <see cref="NativeParts.Classify"/>) are kept the same way, and named in the log once each.</summary>
    static void NoteParts(Entry e, IEnumerable<WpfPresses.Unsupported> found, string source)
    {
        var fresh = found.Where(u => e.WpfParts.TryAdd(u.Key, u)).ToList();
        if (WpfPresses.UnsupportedParts(fresh) is { } note) Log.Info($"dialog \"{e.Title}\" ({source}): {note}");
    }

    /// <summary>The message hook leaves the mouse and the keys of a WPF dialog to WPF's own events (true = nothing more to do with the message); the close box, Alt+F4 and the rest are the common code's.</summary>
    bool WpfMessage(Entry e, Native.Msg m)
    {
        if (!Native.IsInside(e.Hwnd, m.hwnd)) return false;
        switch (m.message)
        {
            case WM_LBUTTONDOWN or WM_LBUTTONDBLCLK or WM_KEYDOWN or WM_KEYUP: return true;
            case WM_SYSKEYDOWN when (long)m.wParam != VK_F4: return true;
        }
        return false;
    }

    /// <summary>The preview mouse and key events of the dispatcher. Only these four are looked at, first thing, and only while a WPF dialog is tracked. They tell <see cref="WpfClickCorrelator"/> what a person did; an input
    /// that is not a modifier or a system key also moves the baseline past the last button's handler and ends the window of the nested-dialog gate (<see cref="Touch"/>), and says how the dialog closes next (Esc: cancel).
    /// A modifier or a system key (Alt alone, Alt+F4, which the message hook has dealt with) changes none of that. Nothing is read here.</summary>
    void OnWpfInput(object sender, PreProcessInputEventArgs a)
    {
        try
        {
            var input = a.StagingItem.Input;
            var re = input?.RoutedEvent;
            if (re == null) return;
            bool mouseDown = re == Mouse.PreviewMouseDownEvent, mouseUp = re == Mouse.PreviewMouseUpEvent, keyDown = re == Keyboard.PreviewKeyDownEvent, keyUp = re == Keyboard.PreviewKeyUpEvent;
            if (!(mouseDown || mouseUp || keyDown || keyUp)) return;
            if (_stack.Innermost() is not { } e || e.Wpf != true || !Native.IsEnabled(e.Hwnd)) return;
            long now = Clock();
            if (mouseDown || mouseUp)
            {
                if (input is not MouseButtonEventArgs { ChangedButton: MouseButton.Left } || !WpfDialogs.IsMouseIn(e.Hwnd)) return;
                if (mouseUp) { _clicks.MouseUp(now); return; }
                _clicks.MouseDown(WpfDialogs.ButtonUnderCursor(e.Hwnd));
                e.ClosedWith = null;   // a press that follows says how it closes
                Touch(e);
                return;
            }
            var k = (KeyEventArgs)input!;
            if (!WpfDialogs.IsKeyboardIn(e.Hwnd)) return;
            if (keyUp) { _clicks.KeyUp(now); return; }
            _clicks.KeyDown(now);
            var key = k.Key == Key.System ? k.SystemKey : k.Key;
            if (k.Key == Key.System || key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.ImeProcessed) return;
            // Esc closes the dialog by its cancel button (a Click, which says so), or by the dialog's own handler: then it was cancelled
            e.ClosedWith = key == Key.Escape ? "cancel" : null;
            Touch(e);
        }
        catch (Exception ex) { Log.Error("dialog input (wpf)", ex); }
    }

    static void OnAnyClick(object sender, RoutedEventArgs a)
    {
        var watch = _listening;
        if (watch == null || sender is not ButtonBase b || b is ToggleButton || !ReferenceEquals(a.Source, sender)) return;
        try { watch.WpfClicked(b); }
        catch (Exception ex) { Log.Error("dialog click (wpf)", ex); }
    }

    /// <summary>A push button was clicked by WPF in the innermost tracked dialog: OK and Cancel set how the dialog closes, any other (not Help) is a click, written at once, before its handlers run.</summary>
    void WpfClicked(ButtonBase b)
    {
        if (_stack.Innermost() is not { } e || e.Wpf != true || !Native.IsEnabled(e.Hwnd) || !WpfDialogs.IsIn(b, e.Hwnd)) return;
        long now = Clock();
        bool viaMouse = _clicks.IsMousePress(b, now);
        if (!_clicks.IsPress(b, now)) return;   // from code, not a person
        var scan = WpfDialogs.Read(e.Hwnd);
        var d = WpfDialogs.Describe(scan, b);
        string text = d?.Text ?? WpfPalette.ButtonText(b);
        int index = d?.Index ?? 1;
        var role = DialogButtons.Role(text);
        e.ClosedWith = role;
        e.Trail.Input();
        if (role == null)
        {
            if (!DialogButtons.IsHelp(text)) WpfCommit(e, scan, text, index, now); else SettleIfPending(e, null);
            return;
        }
        SettleIfPending(e, null);
        e.LastReadMs = now;
        WpfRead(e, now, statics: true, taken: scan);   // the read for the input that closes the dialog: the scan this handler took before the button's own handlers
        if (role == "ok") Confirmed?.Invoke(e.ForN, e.Hwnd, viaMouse ? WpfDialogs.CursorPos() : null);   // a key press that closes the dialog has no point
    }

    /// <summary>The press is a click: the fields and static texts of <paramref name="scan"/> (read in the Click class handler, before the button's handlers), the click goes out, and the baseline moves to
    /// the values read, so the close holds only what changes after this click.</summary>
    void WpfCommit(Entry e, WpfPalette.Scan scan, string text, int index, long now)
    {
        e.LastReadMs = now;
        if (scan.Fields.Count > 0) { e.FieldSeen = true; e.Tracker.Observe(scan.Fields); }
        var parts = scan.ReadParts();
        e.Statics = parts.Statics;   // none: none for this click, not the last page's texts
        NotePartsOf(e, parts);
        SettleIfPending(e, null);
        var click = new Click(e.ForN, text, 0, index, e.Tracker.Changes(), e.Tracker.ReadCount, e.Statics);
        e.Tracker.Rebase();
        e.Settle.Clicked(now);
        e.Trail.Clicked(text, now);
        Clicked?.Invoke(click);
    }
}
