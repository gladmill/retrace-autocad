namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project).

/// <summary>The drawing files this AutoCAD session created for tests under SDI (copies of the template and of recorded drawings, a suite's copies), and what may be done
/// to them. A path counts as the test's own only when this process created it and it lies below the real temp folder: a folder name alone never qualifies (a person's
/// drawing in a folder called <c>retrace-sdi</c> is the person's). Paths are compared case-insensitively with <c>\</c> and <c>/</c> alike.</summary>
public sealed class SdiOwnFiles
{
    readonly string _root;
    readonly Func<string, string> _canon;
    readonly HashSet<string> _created = new(StringComparer.Ordinal);
    readonly Dictionary<string, DateTime> _fresh = new(StringComparer.Ordinal);   // own drawings Retrace opened for a run that has not begun in them yet (valid for <see cref="FreshWindow"/>): start-up code (acaddoc.lsp, S::STARTUP) may change them
    bool _suiteOver;   // the last suite / Tdoc run has ended: a late failed save of one of its drawings is a person's from now on
    int _runs;   // runs (replays) going on, or ended but not yet through their end-of-run save: a late save of one must not end another's
    readonly HashSet<string> _suiteUnsaved = new(StringComparer.Ordinal);   // own drawings of a suite run whose end-of-run save did not happen: the suite's next test saves them

    /// <param name="tempRoot">the folder the files must lie in (<c>Path.GetTempPath()</c> in the plugin)</param>
    /// <param name="canonical">turns a path into its long form (the plugin passes GetLongPathName: a user name of more than 8 characters shows as <c>CLAUDE~1</c> in the temp path
    /// AutoCAD reports, and as the long name in the one Retrace made); null: the path as it is. Both sides of every comparison go through it.</param>
    public SdiOwnFiles(string tempRoot, Func<string, string>? canonical = null)
    {
        _canon = canonical ?? (p => p);
        _root = NormCreated(tempRoot).TrimEnd('/') + "/";
    }

    static string Plain(string p) => p.Replace('\\', '/').Trim().ToLowerInvariant();

    /// <summary>A name that comes in (a drawing's name from AutoCAD, a path from a step): turned into the canonical long form only when it has a short-name part (<c>~</c>).</summary>
    string Norm(string p) { p = p.Trim(); return Plain(p.Contains('~') ? _canon(p) : p); }

    /// <summary>A path Retrace created: canonicalized once, here, and kept in that form.</summary>
    string NormCreated(string p) => Plain(_canon(p.Trim()));

    /// <summary>The same file, by canonical long path, ignoring case and the kind of separator.</summary>
    public bool SamePath(string? a, string? b) => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && Norm(a) == Norm(b);

    bool UnderRoot(string norm) => norm.StartsWith(_root, StringComparison.Ordinal);

    /// <summary>This process created <paramref name="path"/>. Ignored when it is not below the temp folder.</summary>
    public void Created(string path) { var n = NormCreated(path); if (UnderRoot(n)) { _created.Add(n); _fresh[n] = DateTime.UtcNow; } }

    /// <summary>Whether <paramref name="file"/> lies below <paramref name="folder"/> (canonical paths).</summary>
    public bool IsUnder(string? file, string? folder) => !string.IsNullOrWhiteSpace(file) && !string.IsNullOrWhiteSpace(folder) && Norm(file).StartsWith(Norm(folder).TrimEnd('/') + "/", StringComparison.Ordinal);

    /// <summary>A file this process created below the temp folder.</summary>
    public bool IsOwn(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var n = Norm(path);
        return UnderRoot(n) && _created.Contains(n);
    }

    /// <summary>How long after its creation a copy counts as fresh: the first run to begin in it within this time may find it changed by start-up code. A replay that never began
    /// (<see cref="ClearFresh"/>) or began late finds it a leftover.</summary>
    public static readonly TimeSpan FreshWindow = TimeSpan.FromSeconds(120);

    /// <summary>The run that was to begin in <paramref name="path"/> did not (timeout, wrong drawing, start failure): it is no longer fresh.</summary>
    public void ClearFresh(string? path) { if (!string.IsNullOrWhiteSpace(path)) _fresh.Remove(Norm(path)); }

    /// <summary>The copy was opened for a run that never began and is not open (or could not be saved): it is nobody's work to protect, and not Retrace's to replace: given up.</summary>
    public void Abandon(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var n = Norm(path);
        _fresh.Remove(n); _created.Remove(n); _suiteUnsaved.Remove(n);
    }

    /// <summary>A run (a replay) begins. <paramref name="path"/> is the drawing it runs in, <paramref name="modified"/> whether that drawing already holds unsaved changes. A drawing Retrace
    /// opened for this run (<see cref="Created"/>, the first run that begins in it) stays its own even when modified: start-up code of the drawing (acaddoc.lsp, S::STARTUP, AutoCAD
    /// Electrical's setup) changes it as it opens. An own drawing that is modified when a later run begins in it (a leftover of an earlier run) holds changes that are not this run's, a
    /// person's: it stops counting as Retrace's own, so a test refuses to replace it.</summary>
    public void BeginRun(string? path = null, bool modified = false, DateTime? nowUtc = null)
    {
        _runs++;
        if (path == null) return;
        var n = Norm(path);
        var wasFresh = _fresh.Remove(n, out var madeAt) && (nowUtc ?? DateTime.UtcNow) - madeAt <= FreshWindow;
        if (modified && !wasFresh && _created.Contains(n)) { _created.Remove(n); _suiteUnsaved.Remove(n); }
    }

    /// <summary>The run ended and its end-of-run save was tried. <paramref name="path"/> is the own drawing it ran in (null: it ran elsewhere); <paramref name="saved"/> says the
    /// run's changes were saved. When the save failed (or could not happen) the drawing of a RETRACERUN stops counting as Retrace's own: its unsaved changes are then a person's
    /// and the next test refuses to replace it ("has unsaved changes"; the person saves or discards). In a suite run (<paramref name="suiteRun"/>) nobody touches the drawing between
    /// two tests, so it stays own and the suite's next test may save it: one failed save does not end the suite.</summary>
    public void EndRun(string? path, bool saved, bool suiteRun = false)
    {
        if (_runs > 0) _runs--;
        if (path == null || !IsOwn(path)) return;
        var n = Norm(path);
        _fresh.Remove(n);
        if (saved) { _suiteUnsaved.Remove(n); return; }
        if (suiteRun && !_suiteOver) _suiteUnsaved.Add(n); else _created.Remove(n);
    }

    /// <summary>A suite or Tdoc run begins: its runs may leave an unsaved drawing to the next run again (undoes <see cref="SuiteEnded"/>).</summary>
    public void SuiteStarted() => _suiteOver = false;

    /// <summary>A suite or Tdoc run has ended: the own drawings whose end-of-run save never happened are given up, and their unsaved changes are no longer the suite's: from now on they count as a person's.
    /// An end-of-run save that fails after this (the last test's, the restored blank copy's) disowns its drawing at once.</summary>
    public void SuiteEnded()
    {
        _suiteOver = true;   // an end-of-run save that fails after this (the last test's, the restored blank copy's) disowns the drawing
        foreach (var n in _suiteUnsaved) _created.Remove(n);
        _suiteUnsaved.Clear();
    }

    /// <summary>True when unsaved changes in <paramref name="path"/> are the test's own and may be saved and replaced: an own drawing while a run is going on (or its end-of-run save is
    /// still to come), or one of a suite run that could not be saved at its end. Changes made after a RETRACERUN ended (the person worked in it, or the end-of-run save failed) are not.</summary>
    public bool UnsavedIsOurs(string? path) => IsOwn(path) && (_runs > 0 || _suiteUnsaved.Contains(Norm(path!)));

    /// <summary>Whether <paramref name="file"/> (a temp file, found when cleaning up) belongs to a drawing that is open now: the drawing itself or what AutoCAD keeps
    /// beside it (same folder and file name stem: .bak, .dwl, .dwl2, .sv$).</summary>
    public bool BelongsToOpenDrawing(string file, IEnumerable<string> openDrawings)
    {
        var f = Norm(file);
        var fDir = f[..(f.LastIndexOf('/') + 1)];
        var fName = f[fDir.Length..];
        foreach (var d in openDrawings)
        {
            if (string.IsNullOrWhiteSpace(d)) continue;
            var o = Norm(d);
            var oDir = o[..(o.LastIndexOf('/') + 1)];
            if (oDir != fDir) continue;
            var stem = o[oDir.Length..];
            var dot = stem.LastIndexOf('.'); if (dot > 0) stem = stem[..dot];
            if (fName.StartsWith(stem + ".", StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
