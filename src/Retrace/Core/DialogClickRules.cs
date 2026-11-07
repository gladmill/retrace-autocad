namespace Retrace.Core;

// NO Win32 and NO AutoCAD references in this file (linked into the engine test project).
// The rules the recorder applies to the presses it sees in a dialog (which one is a click, when a window that opens is a nested dialog, when a dialog
// was closed by a click) and the readers' rule for attaching a nested dialog to its click.

/// <summary>What one tracked dialog remembers about its last button click: for the nested-dialog gate (a window that opens over the dialog is recorded as a nested dialog only
/// right after a click, or right after the nested dialog before it closed, with no other input to the dialog in between) and for the explicit close
/// (<c>closedWith: "click"</c> when the dialog went away after the last click with no other input to it since).</summary>
public sealed class ClickTrail
{
    /// <summary>The button text of the last recorded click, null = none yet.</summary>
    public string? Button { get; private set; }
    public long ClickMs { get; private set; } = -1;
    bool _input, _open;
    long _lastEndMs = -1;

    /// <summary>A click was recorded (written) at <paramref name="nowMs"/>.</summary>
    public void Clicked(string button, long nowMs) { Button = button; ClickMs = nowMs; _input = false; _open = false; _lastEndMs = -1; }

    /// <summary>Any other press or key aimed at the dialog after the click (the click's own button-up is not one).</summary>
    public void Input() => _input = true;

    /// <summary>A window opened over the dialog: it is a nested dialog of the last click when it comes right after that click, or right after the nested dialog of that click before it closed
    /// (<see cref="DialogClickRules.NestedBelongsTo"/>: Clear opens "Remove all?", answering it opens "3 items removed"), and no other input reached the dialog since. A window with no click
    /// before it (a validation message box after OK, Help, a dialog from a double-click in a list), after other input, or long after, is not.</summary>
    public bool MayOpenNested(long nowMs) =>
        ClickMs >= 0 && !_input && !_open && DialogClickRules.NestedBelongsTo(ClickMs, _lastEndMs >= 0 ? _lastEndMs : null, nowMs);

    /// <summary>A nested dialog of the last click is open: no other window is one of its siblings until it has closed.</summary>
    public void NestedOpened() => _open = true;

    /// <summary>The nested dialog closed at <paramref name="nowMs"/>: the next window within the window after it is the click's next nested dialog.</summary>
    public void NestedClosed(long nowMs) { _open = false; _lastEndMs = nowMs; }

    /// <summary>The dialog went away at <paramref name="closeMs"/> with <paramref name="closedWith"/> (what OK / Cancel / Enter / Esc / the title bar said, null = nothing):
    /// "click" and the button when no other input reached it after the last click (and not longer than <see cref="DialogClickRules.ClosedByClickMaxMs"/> ago), else what it was.</summary>
    public (string? ClosedWith, string? Button) Final(string? closedWith, long closeMs) =>
        closedWith == null && Button != null && !_input && ClickMs >= 0 && closeMs - ClickMs <= DialogClickRules.ClosedByClickMaxMs ? ("click", Button) : (closedWith, null);
}

/// <summary>When the baseline moves past what a clicked button's handler did to the dialog: at the EARLIEST of the first input to the dialog after the click (read before that input is
/// dispatched, so the person's typing is never in the baseline) and the first read at least <see cref="SettleMs"/> after the click (the timer's).</summary>
public sealed class ClickSettle
{
    public const long SettleMs = 100;
    bool _pending;
    long _from = -1;
    public bool Pending => _pending;

    /// <summary>A click was recorded at <paramref name="nowMs"/>; the baseline is still before what its handler did.</summary>
    public void Clicked(long nowMs) { _pending = true; _from = nowMs; }

    /// <summary>A dialog this one opened has closed at <paramref name="nowMs"/>: what its OK did to this dialog (STYLE > New... adds and selects the style, changes the font fields) is its
    /// handler's work too, so the baseline moves again, by the same rule.</summary>
    public void Rearm(long nowMs) => Clicked(nowMs);

    /// <summary>An input is about to reach the dialog: true = read the fields now and move the baseline (once), before the input is dispatched.</summary>
    public bool OnInput()
    {
        if (!_pending) return false;
        _pending = false;
        return true;
    }

    /// <summary>A read of the dialog at <paramref name="nowMs"/>: true = it is the one that moves the baseline (first one at least <see cref="SettleMs"/> after the click, no input first).</summary>
    public bool OnRead(long nowMs)
    {
        if (!_pending || nowMs - _from < SettleMs) return false;
        _pending = false;
        return true;
    }
}

public static class DialogClickRules
{
    /// <summary>A nested dialog belongs to a click when it opened at most this long after the click, or after the nested dialog of that click before it closed (the recorder's gate and the readers' rule).</summary>
    public const long NestedWithinMs = 10000;

    /// <summary>The dialog that went away at most this long after its last click, with no other input to it since, was closed by that click. A sanity cap, not a rule: a button can take a while.</summary>
    public const long ClosedByClickMaxMs = 30000;

    /// <summary>true when a nested dialog that opened at <paramref name="openMs"/> belongs to the click at <paramref name="clickMs"/>: it opened within <see cref="NestedWithinMs"/> of the click,
    /// or, when the click already had a nested dialog that closed at <paramref name="prevEndMs"/>, within that long of its close.</summary>
    public static bool NestedBelongsTo(long clickMs, long? prevEndMs, long openMs)
    {
        var from = prevEndMs ?? clickMs;
        return openMs >= from && openMs - from <= NestedWithinMs;
    }

    /// <summary>A button press is a click only if the button-up lands on the button that got the button-down (the button holds the mouse while it is down, so the
    /// up always arrives at it) and inside it.</summary>
    public static bool MouseUpCompletes(long downWindow, long upWindow, int x, int y, int clientWidth, int clientHeight) =>
        downWindow != 0 && downWindow == upWindow && x >= 0 && y >= 0 && x < clientWidth && y < clientHeight;

    /// <summary>The button-up of a pending press completes it when it lands on the button (<see cref="MouseUpCompletes"/>), and also when the press has opened a window over the dialog by now: the dialog
    /// is disabled under a modal window and the button-up went to that window (a button that acts as the mouse goes down). That is decided at the button-up itself, not by a later timer tick.</summary>
    public static bool UpCommits(bool landedOnButton, bool dialogDisabled, bool windowOver) => landedOnButton || (dialogDisabled && windowOver);

    /// <summary>Which scan a click is written from. A press that completes with its button-up (every normal button) is read at the button-up, as before: a focused field's Leave handler that reformats on the
    /// mouse-down has run by then, and the click's fields hold what the button's handler saw. The scan taken at the button-down is only the fallback for a press that commits before its button-up
    /// (<c>onDown</c>: the window it opened took the button-up).</summary>
    public static bool UseDownSnapshot(bool committedBeforeUp, bool haveDownSnapshot) => committedBeforeUp && haveDownSnapshot;

    /// <summary>A window that opened over a dialog while a press is pending is that button's nested dialog, except a message box that a focused field's validation opened as the mouse went down on the
    /// button (a field had the focus, and the window has nothing but a text and buttons): that is no step of the button, and the press is dropped as it was before mouse-down buttons were recorded.</summary>
    public static bool OnDownWindowIsNested(bool fieldFocusedAtDown, bool windowIsMessageBox) => !(fieldFocusedAtDown && windowIsMessageBox);

    /// <summary>The replay may press a button again with the mouse messages only when nothing happened since the first press: the dialog is still enabled, no window opened over it and no field or static
    /// text changed. Otherwise the first press did something (or is still doing it) and a second one would double it.</summary>
    public static bool MayRetryPress(bool dialogEnabled, bool windowOpened, bool somethingChanged) => dialogEnabled && !windowOpened && !somethingChanged;

    public const int DlgcWantAllKeys = 0x0004, DlgcWantChars = 0x0080;   // DLGC_WANTALLKEYS (= DLGC_WANTMESSAGE), DLGC_WANTCHARS

    /// <summary>Does the focused control keep Enter for itself? As the dialog manager (and WinForms) decide it: the control answers WM_GETDLGCODE for that key with DLGC_WANTALLKEYS /
    /// DLGC_WANTMESSAGE (a multi-line edit that takes Return, a combo box with its list open, a list view or tree view label edit, a grid cell in edit mode). Otherwise Enter presses the default button.</summary>
    public static bool EnterGoesToControl(int dlgCode) => (dlgCode & DlgcWantAllKeys) != 0;

    /// <summary>A letter pressed with no Alt is a mnemonic when the focused control does not take characters (WM_GETDLGCODE for that character has no DLGC_WANTCHARS: an edit box,
    /// a list or a grid does) and no Ctrl or Windows key is held (Ctrl+C, Win+E are shortcuts).</summary>
    public static bool LetterIsMnemonic(int dlgCode, bool ctrlDown, bool winDown) => (dlgCode & DlgcWantChars) == 0 && !ctrlDown && !winDown;

    public enum AfterPress { Wait, NextSegment, GiveUpNestedMissing, GiveUpStillOpen }

    /// <summary>What the replay does while it waits after a press whose nested dialog has not opened (yet). <paramref name="closingClick"/>: the recording says this click closed the dialog:
    /// wait for its window to go (the caller ends the dialog the moment it is gone) and give up after <paramref name="closeTimeout"/>; never go on to the next segment, which would press
    /// OK in a dialog the recording closed with this button. Otherwise: a nested dialog the recording has (<paramref name="nestedExpected"/>) must open within
    /// <paramref name="nestedTimeout"/>, else the dialog must stay quiet for <paramref name="quiet"/> before the next segment.</summary>
    public static AfterPress AfterPressRule(bool closingClick, bool nestedExpected, TimeSpan sincePress, TimeSpan quiet, TimeSpan nestedTimeout, TimeSpan closeTimeout)
    {
        if (closingClick && !nestedExpected) return sincePress > closeTimeout ? AfterPress.GiveUpStillOpen : AfterPress.Wait;
        if (sincePress < (nestedExpected ? nestedTimeout : quiet)) return AfterPress.Wait;
        return nestedExpected ? AfterPress.GiveUpNestedMissing : AfterPress.NextSegment;
    }

    /// <summary>Is a dialog read through UI Automation instead of Win32? When four reads in a row found no Win32 field and none ever did: a WPF dialog has no child windows. Only the way the
    /// fields are read: the push buttons of a dialog with Win32 buttons are tracked whatever the source is.</summary>
    public static bool FlipToUia(bool fieldSeen, int emptyReads) => !fieldSeen && emptyReads >= 4;

    /// <summary>The mnemonic of a control text ("&amp;New..." gives 'N', "A &amp;&amp; B" has none), upper case; null when there is none.</summary>
    public static char? MnemonicOf(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        for (int i = 0; i < text.Length - 1; i++)
        {
            if (text[i] != '&') continue;
            if (text[i + 1] == '&') { i++; continue; }
            return char.ToUpperInvariant(text[i + 1]);
        }
        return null;
    }

    /// <summary>The control a mnemonic press goes to: the index of the one control with that letter if it is an enabled push button; null when no control or
    /// several have it (the dialog manager would cycle between them: not told apart here) or the one is not a push button.</summary>
    public static int? UniqueMnemonicButton(char letter, IReadOnlyList<(bool PushButton, bool Enabled, string? Text)> controls)
    {
        int found = -1, count = 0;
        var want = char.ToUpperInvariant(letter);
        for (int i = 0; i < controls.Count; i++)
            if (MnemonicOf(controls[i].Text) == want) { found = i; count++; }
        return count == 1 && controls[found].PushButton && controls[found].Enabled ? found : null;
    }
}
