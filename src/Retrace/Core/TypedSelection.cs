using System.Text;

namespace Retrace.Core;

/// <summary>What the recorder keeps while a selection is made: the text typed since the last answer, the points that arrived in the meantime
/// and whether the text can still be trusted. <see cref="Take"/> gives the text for the selection result only when the typed text alone
/// makes the whole selection (see <see cref="TypedInput.MakesSelection"/>); a half buffer is never given. Pure logic; the recorder feeds it
/// keys, points and command events (<see cref="Recorder"/>), tests feed it the same.</summary>
public sealed class TypedSelection
{
    readonly StringBuilder _typed = new();
    readonly List<double[]> _points = new();
    bool _bad;

    public string Text => _typed.ToString();
    public int PointCount => _points.Count;
    public bool Bad => _bad;

    public void Reset() { _typed.Clear(); _points.Clear(); _bad = false; }

    /// <summary>A key the person typed (see <see cref="TypedInput.Append"/>).</summary>
    public void Key(char c) => TypedInput.Append(_typed, c);

    /// <summary>A point or corner result arrived (typed or picked). GetCorner's inner point repeats the corner: the same value twice in a row counts once.</summary>
    public void Point(double[] p)
    {
        if (_points.Count == 0 || !_points[^1].SequenceEqual(p)) _points.Add(p);
    }

    /// <summary>A command starts (<paramref name="nested"/>: another command or LISP is running). A top-level command ends the text before it;
    /// a transparent one inside a prompt leaves the outer text, which is then unusable.</summary>
    public void CommandStart(bool nested) { if (nested) _bad = true; else Reset(); }

    /// <summary>A LISP expression starts: its own keys (before an ssget) are not selection keys; typed inside a running prompt it spoils the text.</summary>
    public void LispStart(bool nested) => CommandStart(nested);

    /// <summary>One of our own commands (RETRACEMARK ...) starts. Typed at a prompt, its keys are in the buffer: unusable.</summary>
    public void OwnCommandStart(bool nested) { if (nested) _bad = true; }

    /// <summary>An answer that is not part of a selection arrived (a string, a number, a point at "Specify base point:"): it ends the text.</summary>
    public void OtherAnswer() => Reset();

    /// <summary>The typed text for a finished selection, or null when it cannot make the selection alone. Always starts over.</summary>
    public string? Take()
    {
        var text = _typed.ToString();
        bool ok = !_bad && TypedInput.MakesSelection(text, _points.Count);
        Reset();
        return ok ? text : null;
    }
}
