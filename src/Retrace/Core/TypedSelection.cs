using System.Globalization;
using System.Text;

namespace Retrace.Core;

/// <summary>What the recorder keeps while a selection is made: the keys typed since the last answer, the points that arrived in the meantime
/// and whether the text can still be trusted. Pure logic; the recorder feeds it keys, points and command events (<see cref="Recorder"/>), tests feed it the same.
/// <see cref="Take"/> gives two texts for the finished selection, each only when it makes the whole selection (a half buffer is never given):
/// <c>Typed</c>, the text the person typed (`typed` in the recording; the replay's first choice), and <c>Clicks</c>, the same text with the
/// mouse clicks written as point lines in their place (`clicks`; the replay uses it only where handles cannot express the selection, see <see cref="SelectionReplay.UseClicks"/>).
/// A selection with a click has no `typed`: `typed` keeps meaning text the person typed and nothing else.
/// Typed or clicked is decided from the buffer itself: a point is typed exactly when the keys since the last point end with a completed point line.</summary>
public sealed class TypedSelection
{
    readonly StringBuilder _keys = new();     // keys only
    readonly StringBuilder _text = new();     // keys and click lines, in order
    readonly StringBuilder _since = new();    // keys since the last point
    readonly List<double[]> _points = new();
    bool _bad;          // neither text can be trusted
    bool _clicksBad;    // the clicks cannot be written as points (Shift / Ctrl, a half-typed line before a click)
    bool _clicked;

    public string Text => _keys.ToString();
    public int PointCount => _points.Count;
    public bool Bad => _bad;

    public void Reset() { _keys.Clear(); _text.Clear(); _since.Clear(); _points.Clear(); _bad = false; _clicksBad = false; _clicked = false; }

    /// <summary>A key the person typed (see <see cref="TypedInput.Append"/>).</summary>
    public void Key(char c) { TypedInput.Append(_keys, c); TypedInput.Append(_text, c); TypedInput.Append(_since, c); }

    /// <summary>A click went down with Shift or Ctrl held: a deselect, a sub-object or a cycling pick. Its point cannot stand for it, so the clicks are not stored.</summary>
    public void ClickModifier() => _clicksBad = true;

    /// <summary>A point or corner result arrived (not GetCorner's inner point: the recorder skips that one by the prompt nesting, so two clicks at the same spot count twice).
    /// <paramref name="line"/> is the point line that types it (<see cref="PointLine"/>, current UCS); <paramref name="click"/> says that a mouse click in the drawing window
    /// led to it. Typed: the keys since the last point end with a completed point line, nothing is added. Clicked: the line is added to the clicks text.
    /// Neither (a point from LISP or other code): neither text can be trusted.</summary>
    public void Point(double[] wcs, string? line = null, bool click = false)
    {
        _points.Add(wcs);
        if (EndsWithPointLine(_since.ToString())) { _since.Clear(); return; }
        if (!click || line == null) { _bad = true; _since.Clear(); return; }
        if (_since.Length > 0 && _since[^1] != '\n') _clicksBad = true;   // half-typed line before the click
        _clicked = true;
        if (_text.Length + line.Length + 1 <= TypedInput.MaxLength) _text.Append(line).Append('\n'); else _clicksBad = true;
        _since.Clear();
    }

    static bool EndsWithPointLine(string s)
    {
        if (s.Length < 2 || s[^1] != '\n') return false;
        var body = s[..^1];
        return TypedInput.IsPointLine(body[(body.LastIndexOf('\n') + 1)..]);
    }

    /// <summary>The point line that types a point: "x,y" or "x,y,z", in fixed notation (never an exponent: AutoCAD reads "1E-05" wrongly) rounded to 1e-8.</summary>
    public static string PointLine(double x, double y, double z)
    {
        var s = Fixed(x) + "," + Fixed(y);
        return Math.Abs(z) >= 5e-9 ? s + "," + Fixed(z) : s;
    }

    static string Fixed(double v)
    {
        var s = Math.Round(v, 8).ToString("0.########", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }

    /// <summary>A command starts (<paramref name="nested"/>: another command or LISP is running). A top-level command ends the text before it;
    /// a transparent one inside a prompt leaves the outer text, which is then unusable.</summary>
    public void CommandStart(bool nested) { if (nested) _bad = true; else Reset(); }

    /// <summary>A LISP expression starts: its own keys (before an ssget) are not selection keys; typed inside a running prompt it spoils the text.</summary>
    public void LispStart(bool nested) => CommandStart(nested);

    /// <summary>One of our own commands (RETRACEMARK ...) starts. Typed at a prompt, its keys are in the buffer: unusable.</summary>
    public void OwnCommandStart(bool nested) { if (nested) _bad = true; }

    /// <summary>An answer that is not part of a selection arrived (a string, a number, a point at "Specify base point:", a corner at RECTANG): it ends the text.</summary>
    public void OtherAnswer() => Reset();

    /// <summary>The texts for a finished selection: <c>Typed</c> when the typed keys alone make the whole selection (no click took part), <c>Clicks</c> when
    /// keys and clicks together do and a click took part; null for a text that cannot. Always starts over.</summary>
    public (string? Typed, string? Clicks) Take()
    {
        string? typed = null, clicks = null;
        if (!_bad)
        {
            var keys = _keys.ToString();
            if (!_clicked) { if (TypedInput.MakesSelection(keys, _points.Count)) typed = keys; }
            else if (!_clicksBad && TypedInput.MakesSelection(_text.ToString(), _points.Count)) clicks = _text.ToString();
        }
        Reset();
        return (typed, clicks);
    }
}
