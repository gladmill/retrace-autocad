namespace Retrace.Core;

/// <summary>The tries of one retried action of the replay (where the mouse rests to open a palette, the clicks on a palette's tab): which try it is, when it was made, and whether
/// it is time for the next. <see cref="Reset"/> for a new step.</summary>
public struct Attempts
{
    /// <summary>The number of the last try, 0-based; -1 before the first.</summary>
    public int Index;
    /// <summary>When the last try was made.</summary>
    public DateTime At;

    public Attempts() { Index = -1; At = default; }

    /// <summary>true before the first try, and when the last one is more than <paramref name="every"/> old.</summary>
    public readonly bool Due(DateTime now, TimeSpan every) => Index < 0 || now - At > every;

    /// <summary>Starts the next try now; returns its number.</summary>
    public int Next(DateTime now) { At = now; return ++Index; }

    public void Reset() => this = new();
}
