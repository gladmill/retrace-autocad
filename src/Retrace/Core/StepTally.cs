namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project).

/// <summary>
/// Counts the steps of a recording as it is made ("Rec 12" on the status board): the events <see cref="StatusBoard.CountsAsStep"/> says are steps, and a click on a
/// palette right after the values changed in it is one step with them, as the replay plan makes it (<see cref="Plan.Build"/>). Plain data, fed from the recorder's
/// event writer, never from an AutoCAD event handler.
/// </summary>
public sealed class StepTally
{
    string? _openPalette;   // palette|set of the last counted step when it was a palette_values step: a click on that palette joins it

    public int Count { get; private set; }

    public void Reset() { Count = 0; _openPalette = null; }

    /// <summary>One recorded event (the person's; not a drawing's start-up LISP). The arguments are those of <see cref="StatusBoard.CountsAsStep"/>, plus the palette and set
    /// a palette event names.</summary>
    public void Note(string type, string? origin, int commandDepth, int lispDepth, string? path, string? tabId, string? palette, string? set)
    {
        var key = palette + "|" + set;
        if (type == "palette_click" && _openPalette == key) { _openPalette = null; return; }   // joins the values step before it
        if (!StatusBoard.CountsAsStep(type, origin, commandDepth, lispDepth, path, tabId)) return;
        Count++;
        _openPalette = type == "palette_values" ? key : null;
    }
}
