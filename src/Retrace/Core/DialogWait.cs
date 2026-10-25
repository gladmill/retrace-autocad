using System.Text.RegularExpressions;

namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project).

public enum DialogWaitPhase { Idle, WaitingForOpen, WaitingForPerson, Done, Failed }

/// <summary>
/// RETRACERUN (interactive) at a dialog step. The replayed command opens the dialog itself; the replay then waits for a person.
/// Fed with plain events by the replayer: <see cref="Begin"/> when the command that opens the dialog was sent, <see cref="ModalEntered"/> /
/// <see cref="ModalLeft"/>, <see cref="CommandEnded"/> and <see cref="Tick"/> (time). Rules:
///  - no dialog within <see cref="OpenTimeoutSeconds"/> seconds: Failed;
///  - the command is cancelled before the dialog opens, or after it closed but before the replay moved on: Failed;
///  - the command ends normally without a dialog: Failed after <see cref="EndedGraceSeconds"/> seconds (a dialog may still be on its way);
///  - the dialog closes (last modal left): Done. A person waited, with no time limit.
/// </summary>
public sealed class DialogWait
{
    public const int OpenTimeoutSeconds = 30;
    public const int EndedGraceSeconds = 2;

    public DialogWaitPhase Phase { get; private set; } = DialogWaitPhase.Idle;
    public string? FailReason { get; private set; }
    /// <summary>A dialog really opened and a person was asked to handle it.</summary>
    public bool PersonWaited { get; private set; }
    public bool Active => Phase is DialogWaitPhase.WaitingForOpen or DialogWaitPhase.WaitingForPerson or DialogWaitPhase.Done or DialogWaitPhase.Failed;

    DateTime _begun, _endedAt = DateTime.MinValue;

    /// <summary>The recording closed this dialog with Cancel: its command ending (or being cancelled) after the dialog closed is expected.</summary>
    public bool ExpectCancel { get; private set; }

    public void Begin(DateTime now, bool dialogAlreadyOpen, bool expectCancel = false)
    {
        _begun = now; _endedAt = DateTime.MinValue; FailReason = null; PersonWaited = false; ExpectCancel = expectCancel;
        Phase = DialogWaitPhase.WaitingForOpen;
        if (dialogAlreadyOpen) ModalEntered();
    }

    public void ModalEntered()
    {
        if (Phase != DialogWaitPhase.WaitingForOpen) return;
        Phase = DialogWaitPhase.WaitingForPerson;
        PersonWaited = true;
    }

    /// <param name="depth">modal dialogs still open after this one closed</param>
    public void ModalLeft(int depth)
    {
        if (Phase == DialogWaitPhase.WaitingForPerson && depth <= 0) Phase = DialogWaitPhase.Done;
    }

    public void CommandEnded(bool cancelled, DateTime now)
    {
        if (Phase is DialogWaitPhase.Idle or DialogWaitPhase.Failed) return;
        if (cancelled && ExpectCancel && Phase == DialogWaitPhase.Done) return;
        if (cancelled) { Fail("the command was cancelled"); return; }
        if (Phase == DialogWaitPhase.WaitingForOpen && _endedAt == DateTime.MinValue) _endedAt = now;
    }

    public void Tick(DateTime now)
    {
        if (Phase != DialogWaitPhase.WaitingForOpen) return;
        if ((now - _begun).TotalSeconds > OpenTimeoutSeconds) Fail($"no dialog appeared within {OpenTimeoutSeconds} s");
        else if (_endedAt != DateTime.MinValue && (now - _endedAt).TotalSeconds > EndedGraceSeconds) Fail("the command ended without opening the dialog");
    }

    void Fail(string reason) { Phase = DialogWaitPhase.Failed; FailReason = reason; }

    /// <summary>The dialog closed, but what the replay saw next shows it was not confirmed (e.g. its command ended at once).</summary>
    public void FailAfterClose(string reason) { if (Phase == DialogWaitPhase.Done && !ExpectCancel) Fail(reason); }

    public void Reset() { Phase = DialogWaitPhase.Idle; FailReason = null; PersonWaited = false; _endedAt = DateTime.MinValue; }
}

/// <summary>Where the dialog is described in the test script the engine wrote (`N. A dialog opens: **Title**. ...`).</summary>
public static class ScriptRef
{
    static readonly Regex StepLine = Rx.Create(@"^(\d+)\.\s", RegexOptions.CultureInvariant);
    static readonly Regex DialogLine = Rx.Create(@"^(\d+\.|\s+-)\s+A dialog opens:", RegexOptions.CultureInvariant);

    /// <summary>Script step number holding the <paramref name="ordinal"/>-th (1-based) dialog, or null if the script has fewer. A dialog is
    /// its own step ("4. A dialog opens: ...") or, when the command that opens it is described in the same step, a sub-item of that step
    /// ("   - A dialog opens: ...").</summary>
    public static int? DialogStepNumber(string scriptText, int ordinal)
    {
        int step = 0, seen = 0;
        foreach (var line in (scriptText ?? "").Split('\n'))
        {
            var m = StepLine.Match(line);
            if (m.Success) step = int.Parse(m.Groups[1].Value);
            if (step > 0 && DialogLine.IsMatch(line) && ++seen == ordinal) return step;
        }
        return null;
    }

    /// <summary>The command-line text for a dialog pause.</summary>
    public static string Message(string? title, string? scriptFile, int? stepNumber, IReadOnlyList<FieldChange>? values = null, int valuesRead = 0, string? closedWith = null)
    {
        var t = string.IsNullOrWhiteSpace(title) ? "(untitled dialog)" : title;
        var see = scriptFile != null && stepNumber != null ? $" (see {scriptFile}, step {stepNumber})" : "";
        if (closedWith == "cancel") return $"A dialog opens: {t}. Close it with Cancel{see}.";
        if (values != null)
        {
            var changes = values.Select(DialogFieldsCore.Describe).Where(x => x != null).ToList();
            if (changes.Count > 0) return $"A dialog opens: {t}. {string.Join(", ", changes)}, then close it with OK{see}.";
            if (valuesRead > 0) return $"A dialog opens: {t}. Leave the values as they are, then close it with OK{see}.";
        }
        return $"A dialog opens: {t}. Fill it in and confirm{see}, then close it with OK.";
    }
}
