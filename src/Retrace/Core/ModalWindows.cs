namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project).

/// <summary>What the Win32 layer tells about one top-level window of AutoCAD's process (<see cref="ModalWindows"/> decides from these; the enumeration is in AutoCad/Native).</summary>
public readonly record struct WindowFacts(long Handle, bool Visible, bool Enabled, bool Captioned, bool ToolWindow, bool Transparent, int Width, int Height, string? Class, bool Retrace);

/// <summary>Is a dialog window of the application open? Decided from the process's own windows, never from which window is in front (another program may be: the dialog is still there).</summary>
public static class ModalWindows
{
/// <summary>A window a person could fill in: it appeared after the modal state started (<paramref name="before"/>: the handles of the visible windows at that moment; a floating palette or a modeless form that was
    /// already there is not the modal's dialog), is visible, enabled (a window under a modal one is not), not a tool window (tooltips) or a menu, not fully transparent or empty (an add-in's dialog that is never
    /// shown), and neither AutoCAD's main window, Retrace's own replay window, nor any other window of Retrace. A title bar is not required (a borderless dialog is a dialog).</summary>
    public static bool IsDialogWindow(WindowFacts w, long mainWindow, long replayWindow, IReadOnlyCollection<long>? before = null) =>
        w.Handle != 0 && w.Handle != mainWindow && w.Handle != replayWindow && !w.Retrace
        && w.Visible && w.Enabled && !w.ToolWindow && !w.Transparent && w.Width > 0 && w.Height > 0
        && w.Class != "#32768" && (before == null || !before.Contains(w.Handle));

    /// <summary>The window is really there to be seen: visible, not fully transparent, not empty. (The foreground window of the process counts as a dialog on this alone, a borderless dialog included.)</summary>
    public static bool IsShown(WindowFacts w) => w.Visible && !w.Transparent && w.Width > 0 && w.Height > 0 && !w.Retrace;

    /// <summary>The window in front of the dialog windows in <paramref name="inZOrder"/> (top first, as EnumWindows lists them), 0 when there is none.</summary>
    public static long Topmost(IEnumerable<WindowFacts> inZOrder, long mainWindow, long replayWindow = 0, IReadOnlyCollection<long>? before = null)
    {
        foreach (var w in inZOrder) if (IsDialogWindow(w, mainWindow, replayWindow, before)) return w.Handle;
        return 0;
    }
}

/// <summary>The recorder's view of one modal state while it is open: did a window other than AutoCAD's main window ever show? Written as `modal_nowindow` just before the `modal_leave` when none did.
/// A state the recorder stopped looking at (another modal opened over it before it was resolved) is unknown, never "no window".</summary>
public sealed class ModalEpisode
{
    public ModalEpisode(long n, IReadOnlyCollection<long>? visibleBefore = null) { N = n; Before = visibleBefore; }

    /// <summary>The `n` of the `modal_enter` event.</summary>
    public long N { get; }
    /// <summary>The handles of the visible windows when the state started: only a window that is not among them can be its dialog (<see cref="ModalWindows.IsDialogWindow"/>).</summary>
    public IReadOnlyCollection<long>? Before { get; }
    public bool WindowSeen { get; private set; }
    public bool Superseded { get; private set; }

    public void WindowFound() => WindowSeen = true;
    public void Abandon() => Superseded = true;

    /// <summary>At the `modal_leave`: no window other than the main window was seen while the state was open, and the recorder watched it to the end.</summary>
    public bool WindowlessAtLeave => !WindowSeen && !Superseded;
}

/// <summary>The replayer's view of the modal states AutoCAD is in. A modal state counts as a dialog (it feeds <see cref="DialogWait"/>, and the replay waits for a person) only when a dialog window showed during it
/// (<see cref="ModalWindows"/>). The exception is a recording from before the recorder told windowless states apart (<c>quickCounts</c> of <see cref="Left"/>): a state over in under <see cref="QuickMs"/> may have been a
/// dialog that came and went between two looks. A state with no dialog window open for <see cref="GraceMs"/> is a command's hidden interaction (<see cref="Windowless"/>): the replay goes on as if no dialog was open.
/// Time is passed in.</summary>
public sealed class ModalState
{
    public const int GraceMs = 1500;
    public const int QuickMs = 600;

    DateTime _since, _lastOpen;
    bool _seen, _counted;

    public int Depth { get; private set; }
    /// <summary>A dialog window showed during the current modal state.</summary>
    public bool WindowSeen => _seen;
    /// <summary>How many modal states counted as dialogs so far.</summary>
    public int Counted { get; private set; }
    /// <summary>The handles of the visible windows when the outermost modal state started (<see cref="ModalWindows.IsDialogWindow"/>).</summary>
    public IReadOnlyCollection<long>? Before { get; private set; }

    public void Entered(DateTime now, IReadOnlyCollection<long>? visibleBefore = null)
    {
        if (Depth == 0) { _since = _lastOpen = now; _seen = false; _counted = false; Before = visibleBefore; }
        Depth++;
    }

    /// <summary>A look at the windows (every tick while a modal state is open). True the first time a window is seen in this state: the state counts as a dialog now.</summary>
    public bool Observe(bool dialogWindowOpen, DateTime now)
    {
        if (Depth <= 0 || !dialogWindowOpen) return false;
        _seen = true; _lastOpen = now;
        if (_counted) return false;
        _counted = true; Counted++;
        return true;
    }

    /// <summary>One level closed. <c>CountedNow</c>: the last level closed and this state counts as a dialog that <see cref="Observe"/> did not report (<paramref name="quickCounts"/>, and it was over before a tick saw its
    /// window). <c>WasDialog</c>: the last level closed and the state was a dialog.</summary>
    public (bool CountedNow, bool WasDialog) Left(DateTime now, bool quickCounts = false)
    {
        if (Depth > 0) Depth--;
        if (Depth > 0) return (false, false);
        if (_counted) return (false, true);
        if (quickCounts && (now - _since).TotalMilliseconds < QuickMs) { _counted = true; Counted++; return (true, true); }
        return (false, false);
    }

    /// <summary>Modal, no dialog window is open now, and none was for long enough that a slow dialog would have shown: a dialog that closed inside the same modal state leaves a windowless phase behind it.</summary>
    public bool Windowless(DateTime now, bool dialogWindowOpen) =>
        Depth > 0 && !dialogWindowOpen && (now - _lastOpen).TotalMilliseconds >= GraceMs;
}
