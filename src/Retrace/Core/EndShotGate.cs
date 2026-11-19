// NO AutoCAD references in this file (linked into the engine test project).
namespace Retrace.Core;

/// <summary>What to write as the `shot` event of an end picture once it is settled: <paramref name="Opened"/> is true only when the file on disk is the retaken picture with the box.</summary>
public readonly record struct ShotAnnounce(int N, string Rel, string What, bool Opened);

/// <summary>
/// The bookkeeping behind "the end picture's `shot` event waits for the late-palette watch": an end picture is written at once (<see cref="EarlyStarted"/> / <see cref="EarlyFinished"/>), its event is held (<see cref="Hold"/>) until the watch ends
/// (<see cref="Release"/>), and a retake (<see cref="RetakeStarted"/> / <see cref="RetakeFinished"/>) may write the same file in between. The event is written exactly once, after the last write that was started has finished and the watch is over:
/// with `opened` only when the retake's write succeeded; without it when only the early write succeeded; not at all when no write succeeded (the failure is logged where it happened). Every method returns the event to write now, or null.
/// Thread-safe: the pictures are written on pool threads, the watch runs on the UI thread.
/// </summary>
public sealed class EndShotGate
{
    sealed class Entry { public int InFlight; public bool Released, EarlyOk, RetakeStarted, RetakeOk, Announced; public string Rel = "", What = "window"; }

    readonly object _lock = new();
    readonly Dictionary<int, Entry> _held = new();

    /// <summary>Picture <paramref name="n"/> is an end picture whose event waits for the watch.</summary>
    public void Hold(int n) { lock (_lock) _held[n] = new Entry(); }

    public bool Holds(int n) { lock (_lock) return _held.ContainsKey(n); }

    /// <summary>Every held entry (the recording is stopping).</summary>
    public int[] HeldNumbers() { lock (_lock) return _held.Keys.ToArray(); }

    public void Clear() { lock (_lock) _held.Clear(); }

    /// <summary>The early picture's write is about to start (false: it is not a held picture).</summary>
    public bool EarlyStarted(int n) { lock (_lock) { if (!_held.TryGetValue(n, out var e)) return false; e.InFlight++; return true; } }

    public ShotAnnounce? EarlyFinished(int n, bool ok, string rel, string what)
    {
        lock (_lock)
        {
            if (!_held.TryGetValue(n, out var e)) return null;
            e.InFlight--; e.EarlyOk = ok; e.Rel = rel; e.What = what;
            return Settle(n, e);
        }
    }

    /// <summary>A retake's write is about to start; false when there is nothing to retake (not held, the watch is over, or one retake was started already).</summary>
    public bool RetakeStarted(int n, string rel, string what)
    {
        lock (_lock)
        {
            if (!_held.TryGetValue(n, out var e) || e.Released || e.RetakeStarted) return false;
            e.RetakeStarted = true; e.InFlight++; e.Rel = rel; e.What = what;
            return true;
        }
    }

    public ShotAnnounce? RetakeFinished(int n, bool ok)
    {
        lock (_lock)
        {
            if (!_held.TryGetValue(n, out var e)) return null;
            e.InFlight--; e.RetakeOk = ok;
            return Settle(n, e);
        }
    }

    /// <summary>The watch for picture <paramref name="n"/> is over (whatever the outcome).</summary>
    public ShotAnnounce? Release(int n)
    {
        lock (_lock)
        {
            if (!_held.TryGetValue(n, out var e)) return null;
            e.Released = true;
            return Settle(n, e);
        }
    }

    ShotAnnounce? Settle(int n, Entry e)
    {
        if (!e.Released || e.InFlight > 0 || e.Announced) return null;
        e.Announced = true;
        _held.Remove(n);
        if (e.RetakeStarted && e.RetakeOk) return new ShotAnnounce(n, e.Rel, e.What, true);
        if (e.EarlyOk) return new ShotAnnounce(n, e.Rel, e.What, false);
        return null;
    }
}

/// <summary>Which messages count as "the person's next input" for the late-palette watch: a button-down (client or non-client: a title-bar drag), the wheel, an X button, a key that is not a modifier. Mouse moves and button-ups do not
/// (a fast command's own click-up must not end the watch).</summary>
public static class LateInputFilter
{
    public const uint WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104, WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204, WM_MBUTTONDOWN = 0x0207, WM_XBUTTONDOWN = 0x020B,
        WM_MOUSEWHEEL = 0x020A, WM_MOUSEHWHEEL = 0x020E, WM_NCLBUTTONDOWN = 0x00A1, WM_NCRBUTTONDOWN = 0x00A4, WM_NCMBUTTONDOWN = 0x00A7;

    public static bool Counts(uint message, long wParam) => message switch
    {
        WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN or WM_MOUSEWHEEL or WM_MOUSEHWHEEL or WM_NCLBUTTONDOWN or WM_NCRBUTTONDOWN or WM_NCMBUTTONDOWN => true,
        WM_KEYDOWN or WM_SYSKEYDOWN => wParam is not (0x10 or 0x11 or 0x12 or (>= 0xA0 and <= 0xA5)),   // Shift, Ctrl, Alt alone answer nothing
        _ => false,
    };

    /// <summary>A button-down in the client area: a click that may land inside the palette (the point is in screen coordinates).</summary>
    public static bool IsClient(uint message) => message is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN;
}
