using Retrace.Engine.Recordings;

namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project).

/// <summary>
/// Remembers that a person clicked a Retrace button (a ribbon button, a button in the Open test window) that is about to type a command, so the run that
/// command starts can say who started it ("user" on the status board). Retrace cannot tell a command typed at the command line from one sent over COM
/// (AutoCAD gives both CMDACTIVE 1), so only a click is ever recorded here, and a run started any other way has no owner.
/// The click is meant for the one invocation of its command that follows it: the host calls <see cref="Take"/> when a command of that name starts (the
/// command the click typed starts at once), and a click is used up by the first call that matches, so a later typed or COM invocation never gets it. A click
/// whose command did not start within <see cref="Window"/> is forgotten. The command is matched as a whole word, by the recorder's own rule for a macro
/// (<see cref="RecordingReader.MacroMentions"/>): `RETRACESTEPS` is not `RETRACESTEP`.
/// </summary>
public sealed class StartOrigin
{
    /// <summary>A click is credited only to a command that starts this soon after it (the click types it into the command line: it runs as soon as AutoCAD is idle).</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(15);

    string? _macro;
    DateTime _at;

    /// <summary>A person clicked a button whose macro (or command name) is <paramref name="macro"/>, e.g. "^C^C_RETRACERUN ".</summary>
    public void Click(string? macro, DateTime now)
    {
        _macro = string.IsNullOrWhiteSpace(macro) ? null : macro;
        _at = now;
    }

    /// <summary>True when the last click was for <paramref name="command"/> (a command name without the leading underscore, any case) and is recent. The click is used
    /// up when it matches, and forgotten when it is too old.</summary>
    public bool Take(DateTime now, string command)
    {
        var macro = _macro;
        if (macro == null) return false;
        if (now - _at > Window || now < _at) { _macro = null; return false; }
        if (!RecordingReader.MacroMentions(macro, command)) return false;
        _macro = null;
        return true;
    }
}
