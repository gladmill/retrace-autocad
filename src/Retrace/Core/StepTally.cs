namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project).

/// <summary>
/// Counts the steps of a recording as it is made ("Rec 12" on the status board): the events <see cref="StatusBoard.CountsAsStep"/> says are steps, and a click on a
/// palette right after the values changed in it is one step with them, as the replay plan makes it (<see cref="Plan.Build"/>). Plain data, fed from the recorder's
/// event writer, never from an AutoCAD event handler.
/// <para>A ribbon click whose LISP runs a SCRIPT file (<see cref="Plan.ScriptChain"/>) is one step. Live, the tally follows the plan's rule as far as it can be known when the event is
/// written: a <c>lisp_start</c> within a second of a counted click is counted at once, and uncounted (with whatever was counted inside that LISP) when the <c>SCRIPT</c> command starts in it;
/// after that, a top-level <c>typed/other</c> command or LISP that starts within <see cref="Plan.ScriptGapMs"/> of the previous end is not counted, until a person's input (<c>prompt</c> with
/// <c>input: true</c>) or any counted step. What the tally cannot know live: the plan's "a person's answer before the chain's first dialog leaves the click split" (it counts such a click as
/// one step), and a person's event inside a later command (it counts that command with the chain).</para>
/// </summary>
public sealed class StepTally
{
    string? _openPalette;   // palette|set of the last counted step when it was a palette_values step: a click on that palette joins it
    long _clickT = -1;      // when the last counted ribbon click was, while no other step followed it
    bool _candidate;        // a lisp_start within a second of that click was counted: the chain's LISP, if SCRIPT starts inside it
    int _lispBase;          // Count before that lisp_start was counted
    bool _chain;            // inside a confirmed script chain: its commands are not counted
    bool _noMore;           // a person answered inside the chain: what starts after the current command is theirs
    long _lastEnd;          // when the chain's last command or LISP ended

    public int Count { get; private set; }

    public void Reset() { Count = 0; _openPalette = null; _clickT = -1; _candidate = false; _chain = false; _noMore = false; _lastEnd = 0; }

    /// <summary>One recorded event (the person's; not a drawing's start-up LISP). The arguments are those of <see cref="StatusBoard.CountsAsStep"/>, plus the palette and set
    /// a palette event names, and for the script chain rule the event's time <paramref name="t"/> (ms; -1 = not known, the rule is off), a command's name and a prompt's <paramref name="input"/> flag.</summary>
    public void Note(string type, string? origin, int commandDepth, int lispDepth, string? path, string? tabId, string? palette, string? set,
        long t = -1, string? command = null, bool? input = null, bool? script = null)
    {
        var key = palette + "|" + set;
        if (type == "palette_click" && _openPalette == key) { _openPalette = null; return; }   // joins the values step before it
        if (t >= 0 && ChainAbsorbs(type, origin, commandDepth, lispDepth, command, input, script, t)) return;
        if (!StatusBoard.CountsAsStep(type, origin, commandDepth, lispDepth, path, tabId)) return;
        if (t >= 0)
        {
            if (type == "lisp_start" && !_candidate && _clickT >= 0 && lispDepth == 0 && t - _clickT <= 1000) { _candidate = true; _lispBase = Count; }
            else if (!(_candidate && lispDepth > 0)) { _candidate = false; _chain = false; }   // a command the candidate LISP runs before SCRIPT does not end the candidate
            _clickT = type == "ribbon" ? t : -1;
        }
        Count++;
        _openPalette = type == "palette_values" ? key : null;
    }

    /// <summary>True when the event belongs to the click's script chain and is not counted. Keeps the chain's state.</summary>
    bool ChainAbsorbs(string type, string? origin, int commandDepth, int lispDepth, string? command, bool? input, bool? script, long t)
    {
        switch (type)
        {
            case "prompt":
                if (input == true && _chain) _noMore = true;
                return false;
            case "cmd_end" or "cmd_cancel" or "cmd_fail":
                if (_chain && commandDepth == 1) _lastEnd = t;
                return false;
            case "lisp_end" or "lisp_cancel":
                if (lispDepth == 1) { if (_chain) _lastEnd = t; _candidate = false; }
                return false;
            case "cmd_start":
                if (_candidate && lispDepth > 0 && string.Equals(command, "SCRIPT", StringComparison.OrdinalIgnoreCase))
                {
                    Count = _lispBase;          // the LISP and whatever it ran before SCRIPT belong to the click
                    _candidate = false; _chain = true; _noMore = false; _lastEnd = t;
                    return true;
                }
                if (_chain && !_noMore && commandDepth == 0 && lispDepth == 0 && Plan.ContinuesChain(origin, script, t, _lastEnd)) return true;
                // a ribbon macro that is SCRIPT (^C^C_SCRIPT file): part of the click, not counted itself; what the script runs is the click's
                if (commandDepth == 0 && lispDepth == 0 && origin == "ribbon" && string.Equals(command, "SCRIPT", StringComparison.OrdinalIgnoreCase)) { _chain = true; _noMore = false; _lastEnd = t; }
                return false;
            case "lisp_start":
                if (_chain && commandDepth > 0) return true;     // a LISP inside a command of the chain: part of it
                return _chain && !_noMore && commandDepth == 0 && lispDepth == 0 && Plan.ContinuesChain("typed/other", script, t, _lastEnd);
        }
        return false;
    }
}
