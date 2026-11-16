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

/// <summary>A box in WCS (the extents of an object).</summary>
public readonly record struct ZoomBox(double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ)
{
    /// <summary>True for a box of finite numbers that is not wider than <paramref name="maxSpan"/> (an Xline or Ray reports an enormous one) and not reversed.</summary>
    public bool Usable(double maxSpan)
    {
        foreach (var d in new[] { MinX, MinY, MinZ, MaxX, MaxY, MaxZ }) if (double.IsNaN(d) || double.IsInfinity(d) || Math.Abs(d) > 1e99) return false;
        return MaxX >= MinX && MaxY >= MinY && MaxZ >= MinZ && MaxX - MinX <= maxSpan && MaxY - MinY <= maxSpan && MaxZ - MinZ <= maxSpan;
    }

    /// <summary>The box around all usable boxes; the unusable ones are left out one by one (<paramref name="skipped"/> counts them). Null when none is usable.</summary>
    public static ZoomBox? Union(IEnumerable<ZoomBox> boxes, double maxSpan, out int skipped)
    {
        skipped = 0; ZoomBox? u = null;
        foreach (var b in boxes)
        {
            if (!b.Usable(maxSpan)) { skipped++; continue; }
            u = u is { } c ? new ZoomBox(Math.Min(c.MinX, b.MinX), Math.Min(c.MinY, b.MinY), Math.Min(c.MinZ, b.MinZ), Math.Max(c.MaxX, b.MaxX), Math.Max(c.MaxY, b.MaxY), Math.Max(c.MaxZ, b.MaxZ)) : b;
        }
        return u;
    }
}

/// <summary>Where an object sits, for <see cref="ZoomResolver{T}"/>.</summary>
public enum ZoomOwner
{
    /// <summary>Directly in the space being zoomed (model space).</summary>
    Space,
    /// <summary>Inside a block definition (anything but a layout): the owner's handle names the definition.</summary>
    Definition,
    /// <summary>Owned by an entity (an attribute reference's block reference, a polyline's vertex): the owner is the parent.</summary>
    Entity,
    /// <summary>Anything else (a layout's paper space, an unreadable object): not a candidate.</summary>
    None,
}

/// <summary>What the resolution of "the newest object" needs to know about a drawing (<typeparamref name="T"/> is an object: a wrapper of an ObjectId in AutoCAD, a fake in the tests).
/// The members should be cheap: <see cref="OwnerOf"/> in particular is called for every candidate and should not open the object.</summary>
public interface IZoomOwners<T>
{
    /// <summary>The object's handle.</summary>
    long Handle(T obj);
    /// <summary>Where the object sits. <paramref name="ownerHandle"/>: the definition's handle for <see cref="ZoomOwner.Definition"/> (the cache key), the parent's for <see cref="ZoomOwner.Entity"/>.</summary>
    ZoomOwner OwnerOf(T obj, out long ownerHandle);
    /// <summary>The parent entity of an object that <see cref="OwnerOf"/> reported as <see cref="ZoomOwner.Entity"/>.</summary>
    T? ParentEntity(T obj);
    /// <summary>The block references of the block definition that <paramref name="obj"/> (reported as <see cref="ZoomOwner.Definition"/>) lives in, wherever they are. Empty for a definition that
    /// has no block references by nature (a dimension's anonymous block: the dimension itself is not a block reference).</summary>
    IEnumerable<T> ReferencesOfDefinition(T obj);
    /// <summary>The handles of the groups that contain <paramref name="obj"/> (a space-level object).</summary>
    IReadOnlyList<long> GroupsOf(T obj);
    /// <summary>All members of a group (any space); members that cannot be read are left out.</summary>
    IReadOnlyList<T> GroupMembers(long groupHandle);
}

/// <summary>What the zoom looks at after the newest object was resolved: one object, or all members of a group.</summary>
public sealed record ZoomTarget<T>(IReadOnlyList<T> Objects, string Why);

/// <summary>The rule for the `lastEntity` value of <c>recordZoom</c> and <c>postTestZoom</c>: the newest object a command created is resolved upward to the object that sits directly in model space,
/// so a smart block's last piece does not make the view zoom onto that piece. Pure; no AutoCAD. A resolver keeps what it learned (per block definition: the newest new reference or "none"; per group:
/// its members), so a candidate costs a dictionary lookup after the first of its definition.
/// <list type="bullet">
/// <item>an attribute reference: its block reference (likewise a polyline's vertex: the polyline);</item>
/// <item>an object inside a block definition: the newest block reference of that definition made by the same command (handle at or above the seed), and so on through nested definitions up to the
/// reference in model space; a definition with no such reference (an old definition was changed, a definition without an insert, a dimension's block) makes the object no candidate;</item>
/// <item>the object found that way must itself be at or above the seed: a new vertex or attribute on an object that was there before the command is no creation of the command's (the next newest
/// candidate is tried);</item>
/// <item>a member of a group: all members of the group, but only a group whose every member is at or above the seed (made by the command); in several such groups, the smallest (fewest members, then
/// the newest group). An object that only joined an older group is its own target;</item>
/// <item>an ordinary object that sits in model space is its own target; model space itself is never a target.</item>
/// </list></summary>
public sealed class ZoomResolver<T> where T : class
{
    public const int MaxDepth = 32;
    readonly long _seed;
    readonly IZoomOwners<T> _g;
    readonly Dictionary<long, T?> _defs = new();                       // definition handle -> newest reference at or above the seed (null: none)
    readonly Dictionary<long, IReadOnlyList<T>> _groups = new();        // group handle -> its members

    public ZoomResolver(long seed, IZoomOwners<T> owners) { _seed = seed; _g = owners; }

    /// <summary>The number of block definitions the resolver has looked at (for the log).</summary>
    public int DefinitionsSeen => _defs.Count;

    /// <summary>The target for <paramref name="start"/>, or null when it cannot be resolved to a new object of the space (then the caller tries the next newest object).</summary>
    public ZoomTarget<T>? Resolve(T start)
    {
        var cur = start;
        var path = new List<string>();
        for (int i = 0; ; i++)
        {
            if (i > MaxDepth) return null;
            var kind = _g.OwnerOf(cur, out var ownerHandle);
            if (kind == ZoomOwner.Space) break;
            if (kind == ZoomOwner.Entity)
            {
                if (_g.ParentEntity(cur) is not { } parent) return null;
                path.Add("part of object " + _g.Handle(parent).ToString("X")); cur = parent; continue;
            }
            if (kind != ZoomOwner.Definition) return null;
            if (!_defs.TryGetValue(ownerHandle, out var best))
            {
                long bestH = -1;
                foreach (var r in _g.ReferencesOfDefinition(cur))
                {
                    var h = _g.Handle(r);
                    if (h >= _seed && h > bestH) { best = r; bestH = h; }
                }
                _defs[ownerHandle] = best;
            }
            if (best == null) return null;
            path.Add("inside a block -> reference " + _g.Handle(best).ToString("X")); cur = best;
        }
        // the object found must be new: a vertex or attribute added to an old object is not a creation
        if (_g.Handle(cur) < _seed) return null;
        IReadOnlyList<T>? members = null; long gh = 0;
        foreach (var h in _g.GroupsOf(cur))
        {
            if (!_groups.TryGetValue(h, out var m)) _groups[h] = m = _g.GroupMembers(h);
            if (m.Count == 0 || !AllNew(m)) continue;
            if (members == null || m.Count < members.Count || (m.Count == members.Count && h > gh)) { members = m; gh = h; }
        }
        if (members != null) { path.Add("member of group " + gh.ToString("X") + " (" + members.Count + " objects)"); return new ZoomTarget<T>(members, string.Join(", ", path)); }
        path.Add("object " + _g.Handle(cur).ToString("X"));
        return new ZoomTarget<T>(new[] { cur }, string.Join(", ", path));
    }

    bool AllNew(IReadOnlyList<T> members)
    {
        foreach (var m in members) if (_g.Handle(m) < _seed) return false;
        return true;
    }
}

public static class ZoomParent
{
    /// <summary>The target of the newest created object that can be resolved: <paramref name="candidates"/> are tried from the highest handle down (the caller supplies them lazily), the first that
    /// resolves wins. A candidate that throws is skipped (<paramref name="skipped"/> is told). Null when none resolves, or when <paramref name="expired"/> says the time is up before one did
    /// (<paramref name="timedOut"/> is then true: the caller falls back to the plain model-space rule).</summary>
    public static ZoomTarget<T>? Newest<T>(long seed, IEnumerable<T> candidates, IZoomOwners<T> g, out bool timedOut, Func<bool>? expired = null, Action<T, Exception>? skipped = null) where T : class
    {
        timedOut = false;
        var resolver = new ZoomResolver<T>(seed, g);
        foreach (var c in candidates)
        {
            if (expired?.Invoke() == true) { timedOut = true; return null; }
            try { if (resolver.Resolve(c) is { } t) return t; }
            catch (Exception ex) { skipped?.Invoke(c, ex); }
        }
        if (expired?.Invoke() == true) timedOut = true;
        return null;
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
