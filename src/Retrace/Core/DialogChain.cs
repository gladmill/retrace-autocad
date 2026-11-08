namespace Retrace.Core;

// NO Win32 and NO AutoCAD references in this file (linked into the engine test project).

/// <summary>The rule of the replay's dialog wait for "the next recorded step is a dialog and it is open already": the command showed it right after this one closed (a LISP alert after its DCL
/// dialog, the dialogs of a script chain, a button that closes one dialog and opens the next). This step is done and the next one is filled or waited for as its own step.</summary>
public static class DialogChain
{
    /// <summary>Is the next recorded step the dialog that is open in front now? A modal is open (<paramref name="modalDepth"/> above 0), the next step is a dialog, and the foreground window of the process
    /// (<paramref name="foregroundTitle"/>, null when there is none or it is AutoCAD's own window or Retrace's) has its title: <see cref="FillMatcher.TitleMatches"/>, so a dialog recorded with no title
    /// is any modal in front. An unexpected warning box in front keeps the wait.</summary>
    public static bool IsNext(int modalDepth, Step? next, string? foregroundTitle) =>
        modalDepth > 0 && next is { Kind: "dialog" } && foregroundTitle != null && FillMatcher.TitleMatches(next.DialogTitle, foregroundTitle);
}
