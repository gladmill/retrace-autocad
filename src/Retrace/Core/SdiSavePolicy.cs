namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project).

/// <summary>When the end-of-run save of a test drawing may happen (SDI). Never during a command. A dialog open blocks it too, except Retrace's own verdict box of RETRACERUN when it is the
/// only modal open: the replay has stopped by then, and the box can stay open as long as a person likes, so the save must not wait for it (it would give up after a minute and give the
/// drawing up as a person's). Without a modal, AutoCAD must also be quiescent (no input queued).</summary>
public static class SdiSavePolicy
{
    /// <param name="verdictBoxIsTheModal">a modal is open and it is Retrace's own verdict box (the foreground dialog is it), nothing else</param>
    public static bool CanSaveNow(bool commandInProgress, bool quiescent, bool modalOpen, bool verdictBoxIsTheModal) =>
        !commandInProgress && (modalOpen ? verdictBoxIsTheModal : quiescent);
}
