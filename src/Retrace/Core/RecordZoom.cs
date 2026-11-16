namespace Retrace.Core;

/// <summary>Setting <c>recordZoom</c> ("Test record execution"): the view of model space follows what the session creates. It has the values of <see cref="PostTestZoomModes"/>
/// (none, extents, lastEntity) and reads them as leniently; the choice is made after each command that created model-space objects, while recording and while replaying.</summary>
public static class RecordZoomRules
{
    /// <summary>The canonical value of a text from a settings file or the command line (see <see cref="PostTestZoomModes.Normalize(string?)"/>); anything unknown is "none".</summary>
    public static string Normalize(string? text) => PostTestZoomModes.Normalize(text);

    /// <summary>True when a command that began with the handle seed <paramref name="seed"/> left at least one of <paramref name="handles"/> (the live model-space objects, erased ones
    /// not listed) at or above it: handles only grow, so an object with a handle at or above the seed was made after the seed was read. A seed that could not be read (zero or less) means no.</summary>
    public static bool Created(long seed, IEnumerable<long> handles)
    {
        if (seed <= 0) return false;
        foreach (var h in handles) if (h >= seed) return true;
        return false;
    }

    /// <summary>The header field of a recording that says which `recordZoom` value it was made with (written by the recorder; recordings made before it lack it).</summary>
    public const string HeaderField = "recordZoom";

    /// <summary>The recorded `recordZoom` of a recording's header event, normalized, or null when the header has none (older recordings).</summary>
    public static string? FromHeader(IEnumerable<System.Text.Json.JsonElement> events)
    {
        foreach (var e in events)
        {
            if (Plan.Str(e, "type") != "header") continue;
            return Plan.Str(e, HeaderField) is { } v ? Normalize(v) : null;
        }
        return null;
    }

    /// <summary>A note for the result and the log when the recording was made with another value than the replay uses (the zoom is an undo step in some cases, so an UNDO after a script chain may
    /// then behave differently); null when they are the same or the recording does not say.</summary>
    public static string? DifferenceNote(string? recorded, string current)
    {
        if (recorded == null) return null;
        var c = Normalize(current);
        if (recorded == c) return null;
        return $"recordZoom: the test was recorded with \"{recorded}\" and is replayed with \"{c}\"; the view moves at different moments, and an UNDO after a script or LISP chain can undo the zoom first";
    }

    /// <summary>The highest handle at or above <paramref name="seed"/> (the object made last), or -1 when none.</summary>
    public static long Newest(long seed, IEnumerable<long> handles)
    {
        long best = -1;
        if (seed <= 0) return best;
        foreach (var h in handles) if (h >= seed && h > best) best = h;
        return best;
    }
}

/// <summary>Which drawings have a command (or LISP, or a chain of them) running or just ended, and from which handle seed. A command start opens an entry (or, when the drawing's chain
/// has not been dealt with yet, joins it and keeps its first seed); an end closes one level (a transparent command nests); an entry whose depth is back at zero is "ready": the caller
/// zooms there when the drawing is quiet, once, for the whole chain. Pure bookkeeping, no AutoCAD: <typeparamref name="TKey"/> is the document.</summary>
public sealed class ZoomTracker<TKey> where TKey : notnull
{
    sealed class Entry { public long Seed; public int Depth; public long LastMs, CreatedMs, QuietSince = -1; }
    readonly Dictionary<TKey, Entry> _e = new();

    public int Count => _e.Count;

    public void Clear() => _e.Clear();

    /// <summary>A command or LISP started in <paramref name="key"/>. The seed is read (by <paramref name="seed"/>) only for the first start of a chain; a seed that is zero or less (unreadable) tracks nothing.</summary>
    public void Start(TKey key, Func<long> seed, long nowMs)
    {
        if (_e.TryGetValue(key, out var e)) { e.Depth++; e.LastMs = nowMs; return; }
        var s = seed();
        if (s <= 0) return;
        _e[key] = new Entry { Seed = s, Depth = 1, LastMs = nowMs, CreatedMs = nowMs };
    }

    /// <summary>A command or LISP ended (or was cancelled or failed) in <paramref name="key"/>; an end with no start is ignored.</summary>
    public void End(TKey key, long nowMs)
    {
        if (!_e.TryGetValue(key, out var e)) return;
        if (e.Depth > 0) e.Depth--;
        e.LastMs = nowMs;
    }

    /// <summary>The entries whose commands have all ended, with their seeds.</summary>
    public List<(TKey Key, long Seed)> Ready()
    {
        var l = new List<(TKey, long)>();
        foreach (var (k, e) in _e) if (e.Depth == 0) l.Add((k, e.Seed));
        return l;
    }

    public void Remove(TKey key) => _e.Remove(key);

    public bool Has(TKey key) => _e.ContainsKey(key);

    public List<TKey> Keys() => _e.Keys.ToList();

    /// <summary>Recovery from a missed end event (a drawing replaced under the same document, an unhook in the middle of a command): an entry that still counts a running command while the drawing
    /// has been quiet for <paramref name="graceMs"/> is taken as ended. True when it was reset.</summary>
    public bool NoteQuiet(TKey key, bool quiet, long nowMs, long graceMs)
    {
        if (!_e.TryGetValue(key, out var e) || e.Depth == 0) return false;
        if (!quiet) { e.QuietSince = -1; return false; }
        if (e.QuietSince < 0) e.QuietSince = nowMs;
        if (nowMs - e.QuietSince < graceMs) return false;
        e.Depth = 0; e.LastMs = nowMs; e.QuietSince = -1;
        return true;
    }

    /// <summary>True when a ready entry ended less than <paramref name="holdMs"/> ago: its zoom is about to happen, so a picture of the result may wait for it a moment.</summary>
    public bool HasFreshReady(long nowMs, long holdMs)
    {
        foreach (var e in _e.Values) if (e.Depth == 0 && nowMs - e.LastMs < holdMs) return true;
        return false;
    }

    /// <summary>Removes and returns the entries that waited too long: a ready one (the drawing never became quiet, or another drawing stayed in front) after <paramref name="readyMs"/>, one still
    /// "running" (an end was missed) after <paramref name="runningMs"/> from its first start.</summary>
    public List<TKey> Expire(long nowMs, long readyMs, long runningMs)
    {
        var gone = new List<TKey>();
        foreach (var (k, e) in _e)
            if (e.Depth == 0 ? nowMs - e.LastMs > readyMs : nowMs - e.CreatedMs > runningMs) gone.Add(k);
        foreach (var k in gone) _e.Remove(k);
        return gone;
    }
}
