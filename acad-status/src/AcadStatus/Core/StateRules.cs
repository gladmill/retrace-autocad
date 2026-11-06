using System;
using System.Collections.Generic;

namespace AcadStatus.Core;

/// <summary>A dialog or message box open on AutoCAD's UI thread. <see cref="Handle"/> tells two dialogs with the same title apart.</summary>
public sealed record DialogSnap(long Handle, string Title, string Class, bool Modal, IReadOnlyList<string> Texts, IReadOnlyList<string> Buttons);

/// <summary>What one look at AutoCAD found. <see cref="Prompt"/> is null when no prompt is pending and "" when one is pending but its text is
/// unknown. <see cref="DialogSince"/> is when the dialog was first seen.</summary>
public sealed record Snapshot(string? Doc, int Docs, int CmdActive, string CmdNames, string? Prompt, DialogSnap? Dialog, DateTime? DialogSince);

/// <summary>The state rules of the <c>acad</c> writer (CONTRACT.md, "States").</summary>
public static class StateRules
{
    /// <summary>A dialog or message box open this long is blocked: nothing is dealing with it.</summary>
    public const int BlockedAfterSec = 15;

    public static string Decide(Snapshot s, DateTime nowUtc)
    {
        if (s.Dialog != null)
        {
            var since = s.DialogSince ?? nowUtc;
            return (nowUtc - since).TotalSeconds >= BlockedAfterSec ? States.Blocked : States.WaitingInput;
        }
        if (s.Prompt != null) return States.WaitingInput;
        if (s.CmdActive != 0) return States.Running;
        return States.Idle;
    }

    /// <summary>The first command of CMDNAMES: "ZOOM'PAN" gives "ZOOM", "" when none.</summary>
    public static string FirstCommand(string? cmdNames)
    {
        if (string.IsNullOrWhiteSpace(cmdNames)) return "";
        var t = cmdNames.Trim();
        var end = t.IndexOfAny(new[] { '\'', ' ', '\t' });
        return end < 0 ? t : t[..end];
    }
}

/// <summary>Remembers when a dialog was first seen. A dialog is the same one while its window handle stays the same: a caption that changes
/// (a progress counter in the title) does not make it a new dialog, or it would never be open long enough to be blocked.</summary>
public sealed class DialogClock
{
    long _handle;
    DateTime _since;

    /// <summary>The time the dialog in <paramref name="d"/> was first seen, null when there is none.</summary>
    public DateTime? Observe(DialogSnap? d, DateTime nowUtc)
    {
        if (d == null) { _handle = 0; return null; }
        if (d.Handle != _handle) { _handle = d.Handle; _since = nowUtc; }
        return _since;
    }
}
