namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project).

/// <summary>
/// A dialog (or any window of a modal state) that opens and closes by itself within a fraction of a second, with no click or key from the person, is a flash: an add-in that reopens its dialog to refresh it.
/// It is not something a person saw or did, so it gets no picture and no step (the recorder writes `modal_flash` with `for` = the `n` of its `modal_enter`, the readers drop the dialog as they drop a
/// modal state with no window) and one line in retrace.log. A dialog the person answers (a click or a key press while it is open) is a dialog however short it was.
/// The time counts from the moment the dialog window was first seen showing, not from `EnterModal`: AutoCAD takes a few hundred ms to put a dialog on screen.
/// </summary>
public static class FlashDialog
{
    /// <summary>A dialog that was on screen for less than this, untouched, is a flash.</summary>
    public const int ThresholdMs = 500;

    /// <summary>How long a modal state is held for a window that is on screen but that the recorder has not looked at yet (it looks every 100 ms).</summary>
    public const int UnseenHoldMs = 1500;

    /// <summary>A dialog that may still turn out to be a flash. <paramref name="seenMs"/>: when its window was first seen showing (null = not yet); <paramref name="nowMs"/>; <paramref name="input"/>: a click or key press came while the
    /// modal state was open; <paramref name="inputVisible"/>: the dialog's input hook is really installed, so a press in a WinForms dialog's own loop would have been seen. Without it nobody can say the dialog was not answered,
    /// so it is never a flash. True: no picture of the dialog and no whole-window picture is taken now (it would show the dialog); at the leave it is a flash.</summary>
    public static bool Young(long? seenMs, long nowMs, bool input, bool inputVisible) => inputVisible && !input && seenMs is { } s && nowMs - s < ThresholdMs;

    /// <summary>Is the modal state that ends now a flash?</summary>
    public static bool IsFlash(long? seenMs, long leaveMs, bool input, bool inputVisible) => Young(seenMs, leaveMs, input, inputVisible);

    /// <summary>The hold before the window has been seen: a dialog window is on screen (<paramref name="windowShown"/>) that the recorder's look has not reached yet, the state is tracked (<paramref name="tracked"/>), nobody
    /// pressed anything and it is under <see cref="UnseenHoldMs"/> old. Never when the window was already found (<paramref name="windowFound"/>: a dialog that merged into one a button opened can never be a flash).</summary>
    public static bool HoldBeforeSeen(long startMs, long nowMs, bool windowShown, bool windowFound, bool tracked, bool input) =>
        tracked && !input && !windowFound && windowShown && nowMs - startMs < UnseenHoldMs;
}
