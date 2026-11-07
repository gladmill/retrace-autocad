namespace Retrace.Core;

// NO Win32 and NO AutoCAD references in this file (linked into the engine test project).
// The bookkeeping of the dialogs the recorder tracks: a stack, one entry per window.

/// <summary>An entry of the stack: a dialog the recorder follows. <see cref="Window"/> is its window (0 = not found yet).</summary>
public interface IStackEntry
{
    long Window { get; }
    bool Resolved => Window != 0;
    /// <summary>Found by its window, not announced by <c>EnterModal</c>: AutoCAD raises that only for the first modal window, not for a dialog a button of another one opens.</summary>
    bool Synthetic { get; }
    /// <summary>A late <c>EnterModal</c> announced this window: it is already tracked, so the <c>LeaveModal</c> that follows closes it.</summary>
    bool Adopted { get; set; }
}

/// <summary>The tracked dialogs, outermost first. Dialogs announced by <c>EnterModal</c> are pushed before their window is known; nested ones found by their
/// window are added when they are seen and leave when their window is gone. No window is tracked twice.</summary>
public sealed class DialogStack<T> where T : class, IStackEntry
{
    readonly List<T> _items = new();

    public int Count => _items.Count;
    public IReadOnlyList<T> Items => _items;
    /// <summary>The last entry, found or not.</summary>
    public T? Top => _items.Count > 0 ? _items[^1] : null;

    public void Push(T e) => _items.Add(e);
    public bool Contains(T e) => _items.Contains(e);
    public bool Remove(T e) => _items.Remove(e);
    public void Clear() => _items.Clear();

    /// <summary>The innermost entry whose window is known.</summary>
    public T? Innermost()
    {
        for (int i = _items.Count - 1; i >= 0; i--)
            if (_items[i].Resolved) return _items[i];
        return null;
    }

    /// <summary>Is a dialog still being looked for (the last entry has no window yet)? Then a window that shows up is that one.</summary>
    public bool Looking => _items.Count > 0 && !_items[^1].Resolved;

    /// <summary>Is <paramref name="window"/> tracked already?</summary>
    public bool Has(long window) => window != 0 && _items.Any(x => x.Window == window);

    public T? SyntheticFor(long window) => window == 0 ? null : _items.LastOrDefault(x => x.Synthetic && x.Window == window);

    /// <summary>The announced dialog <paramref name="e"/> (pushed at <c>EnterModal</c>, its window not found yet) has been found, and its window is <paramref name="window"/>. When a nested dialog
    /// found by its window already tracks that window, the announcement was late: <paramref name="e"/> is taken off the stack (it must not block the search for the next nested dialog), that
    /// entry takes the announcement over (<see cref="IStackEntry.Adopted"/>: the <c>LeaveModal</c> that follows closes it) and is returned. Null: another window; <paramref name="e"/> stays.</summary>
    public T? MergeInto(T e, long window)
    {
        var s = SyntheticFor(window);
        if (s == null || ReferenceEquals(s, e) || !_items.Remove(e)) return null;
        s.Adopted = true;
        return s;
    }

    /// <summary>Nested dialogs whose window is gone, innermost first, taken off the stack.</summary>
    public List<T> ReapGone(Func<long, bool> isWindow)
    {
        var gone = new List<T>();
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            var e = _items[i];
            if (!e.Synthetic || !e.Resolved || isWindow(e.Window)) continue;
            _items.RemoveAt(i);
            gone.Add(e);
        }
        return gone;
    }

    /// <summary>The entry a <c>LeaveModal</c> closes: the innermost announced one whose window is gone, else the innermost announced one (its window may be gone already
    /// or not found yet), else the last. Never a nested dialog found by its window: those leave by themselves.</summary>
    public T? PickForLeave(Func<long, bool> isWindow)
    {
        for (int i = _items.Count - 1; i >= 0; i--)
            if (_items[i] is { Resolved: true, Synthetic: false } e && !isWindow(e.Window)) return e;
        for (int i = _items.Count - 1; i >= 0; i--)
            if (!_items[i].Synthetic) return _items[i];
        return _items.Count > 0 ? _items[^1] : null;
    }

    /// <summary>The innermost nested dialog an announcement was taken over for (see <see cref="MergeInto"/>).</summary>
    public T? InnermostAdopted()
    {
        for (int i = _items.Count - 1; i >= 0; i--)
            if (_items[i] is { Synthetic: true, Adopted: true } e) return e;
        return null;
    }
}

/// <summary>Pairs each <c>EnterModal</c> with its <c>LeaveModal</c> (last in, first out), remembering which announcements turned out to be for a dialog already tracked by its window
/// (found out when the announced window is looked for, a moment after <c>EnterModal</c>).</summary>
public sealed class ModalPairs
{
    readonly List<(int Id, bool Adopted)> _open = new();
    int _next;
    public int Open => _open.Count;
    /// <summary>An <c>EnterModal</c>: its token.</summary>
    public int Enter() { _open.Add((++_next, false)); return _next; }
    /// <summary>The announcement <paramref name="token"/> was for a dialog tracked already: its <c>LeaveModal</c> closes that dialog's entry, not the announced one.</summary>
    public void Adopt(int token)
    {
        var i = _open.FindIndex(x => x.Id == token);
        if (i >= 0) _open[i] = (token, true);
    }
    /// <summary>A <c>LeaveModal</c>: true when the dialog that is leaving was taken over by an entry found by its window.</summary>
    public bool Leave()
    {
        if (_open.Count == 0) return false;
        var last = _open[^1];
        _open.RemoveAt(_open.Count - 1);
        return last.Adopted;
    }
    public void Clear() => _open.Clear();
}
