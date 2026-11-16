using Autodesk.AutoCAD.ApplicationServices;
using Retrace.Core;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Retrace.AutoCad;

/// <summary>Setting <c>recordZoom</c> ("Test record execution"), used by the recorder and the replayer with one rule. Commands and LISP report their start and end; once every one in a drawing has
/// ended (a script or LISP chain is one) and a command made at least one model-space object since the first start, the view is set through <see cref="PostZoom"/> (Editor.SetCurrentView, never a
/// ZOOM command, no recorder event, no step).
/// <para>Where: in the command-ended event of the last command (<see cref="EndNow"/>), but only when nothing else runs: no other command or LISP in the drawing, no script / macro / LISP chain
/// (CMDACTIVE, less what ran when the session began) and, in a replay, no more text of the step waiting to be sent. The view change is then part of that command's undo group, so a typed UNDO still
/// takes back the command's work and the view with it. Everything else (a script chain, a dialog, another drawing in front) is retried when the drawing is quiet (<see cref="Pump"/>) and dropped
/// after <see cref="ReadyExpireMs"/>; that zoom is an undo step of its own.</para>
/// Never in the middle of a command, in a layout, with another drawing in front or at shutdown. A missed end event cannot keep a drawing "busy": an entry that counts a running command while the
/// drawing has been quiet for a while is taken as ended, and one whose drawing was replaced is reset.</summary>
sealed class RecordZoomDriver
{
    public const int ReadyExpireMs = 15000;
    public const int RunningExpireMs = 600000;
    public const int HoldMs = 1000;
    public const int QuietGraceMs = 2000;

    readonly ZoomTracker<Document> _t = new();
    readonly Dictionary<Document, IntPtr> _db = new();
    string _mode = PostTestZoomModes.None;
    int _baseline;   // the script / LISP bits that were set when the session began (a replay started from a script)

    public bool Active => _mode != PostTestZoomModes.None;

    public void Begin(string mode, int baselineBits = 0) { _mode = RecordZoomRules.Normalize(mode); _baseline = baselineBits; _t.Clear(); _db.Clear(); }
    public void Clear() { _t.Clear(); _db.Clear(); }

    static long Now => Environment.TickCount64;

    /// <summary>A script, macro or LISP chain runs (CMDACTIVE bit 4 or 32 beyond what ran at the start of the session).</summary>
    public bool ChainBusy() => ReplayRules.NewScriptBits(Ask.ActiveBits(), _baseline) != 0;

    /// <summary>A command or LISP started in <paramref name="doc"/> (not one of Retrace's own).</summary>
    public void Start(Document? doc)
    {
        if (!Active || doc == null) return;
        try
        {
            if (_db.TryGetValue(doc, out var old) && !SdiDocs.SameDb(SdiDocs.DbId(doc), old)) Drop(doc);   // another drawing in the same document (SDI): its count is no longer true
            _t.Start(doc, () => PostZoom.Seed(doc), Now);
            if (_t.Has(doc) && !_db.ContainsKey(doc)) _db[doc] = SdiDocs.DbId(doc);
        }
        catch (Exception ex) { Log.Error("record zoom start", ex); }
    }

    /// <summary>A command or LISP ended, was cancelled or failed.</summary>
    public void End(Document? doc)
    {
        if (!Active || doc == null) return;
        _t.End(doc, Now);
    }

    /// <summary>A command just ended and its zoom has not been decided: a picture of the result may wait for it (at most <see cref="HoldMs"/>, and not while a script or LISP chain runs: it
    /// would be held for the length of the script).</summary>
    public bool Waiting => Active && _t.Count > 0 && _t.HasFreshReady(Now, HoldMs) && !ChainBusy();

    /// <summary>The command <paramref name="ending"/> (null for a LISP) of <paramref name="doc"/> has just ended; call from its CommandEnded / Cancelled / Failed / LispEnded event, after
    /// <see cref="End"/>. When it was the last of its chain and nothing else runs, the zoom is done here. <paramref name="moreInput"/>: the replayer still has text of this step to send.</summary>
    public bool EndNow(Document? doc, string? ending, Func<bool>? moreInput = null, Action? beforeApply = null)
    {
        if (!Active || doc == null) return false;
        try
        {
            foreach (var (d, seed) in _t.Ready())
            {
                if (!ReferenceEquals(d, doc)) continue;
                if (ChainBusy() || (moreInput?.Invoke() ?? false) || !OnlyThisCommand(doc, ending)) return false;   // Pump takes it when the drawing is quiet
                return TryZoom(doc, seed, beforeApply, atCommandEnd: true);
            }
        }
        catch (Exception ex) { Log.Error("record zoom (command end)", ex); }
        return false;
    }

    /// <summary>Nothing runs in the drawing but the command that is ending (AutoCAD still reports it as in progress inside its end event).</summary>
    static bool OnlyThisCommand(Document doc, string? ending)
    {
        string running;
        try { running = doc.CommandInProgress ?? ""; } catch (Exception) { return false; }
        if (running.Length == 0) return true;
        return ending != null && string.Equals(Plain(running), Plain(ending), StringComparison.OrdinalIgnoreCase);
        static string Plain(string c) => c.TrimStart('_', '.', '\'', '-');
    }

    /// <summary>Zooms the drawings that are ready and quiet. <paramref name="beforeApply"/> runs just before a zoom is tried. True when a view was set.</summary>
    public bool Pump(Action? beforeApply = null)
    {
        if (!Active || _t.Count == 0) return false;
        bool applied = false;
        try
        {
            var docs = AcApp.DocumentManager.Cast<Document>().ToList();
            foreach (var d in _t.Keys()) if (!docs.Contains(d)) Drop(d);   // a closed document
            foreach (var d in _t.Expire(Now, ReadyExpireMs, RunningExpireMs)) { _db.Remove(d); Log.Info("record zoom: gave up waiting for a quiet drawing; the view is left as it is"); }
            foreach (var d in _t.Keys())
            {
                if (_db.TryGetValue(d, out var db) && !SdiDocs.SameDb(SdiDocs.DbId(d), db)) { Drop(d); continue; }
                var quiet = ViewRestorer.Quiet(d) && !SdiDocs.IsModalOpen() && !ChainBusy();
                if (_t.NoteQuiet(d, quiet, Now, QuietGraceMs)) Log.Info("record zoom: a command start had no end; counted as ended");
            }
            foreach (var (doc, seed) in _t.Ready())
            {
                if (ViewRestorer.IsShutdown) { _t.Clear(); _db.Clear(); return false; }
                if (ChainBusy()) continue;
                if (TryZoom(doc, seed, beforeApply, atCommandEnd: false)) applied = true;
            }
        }
        catch (Exception ex) { Log.Error("record zoom", ex); }
        return applied;
    }

    bool TryZoom(Document doc, long seed, Action? beforeApply, bool atCommandEnd)
    {
        if (ViewRestorer.IsShutdown) return false;
        var docs = AcApp.DocumentManager.Cast<Document>();
        if (!docs.Contains(doc) || !_db.TryGetValue(doc, out var db) || !SdiDocs.SameDb(SdiDocs.DbId(doc), db)) { Drop(doc); return false; }
        if (!ReferenceEquals(doc, AcApp.DocumentManager.MdiActiveDocument)) return false;
        if (!atCommandEnd && (!ViewRestorer.Quiet(doc) || SdiDocs.IsModalOpen())) return false;
        beforeApply?.Invoke();
        var done = PostZoom.Run(doc, _mode, seed, out var blocked, requireCreated: true, atCommandEnd: atCommandEnd);
        if (blocked) return false;
        Drop(doc);
        if (done == null) return false;
        Log.Info("record zoom (" + _mode + (atCommandEnd ? ", at the command's end" : "") + "): " + done);
        return true;
    }

    void Drop(Document doc) { _t.Remove(doc); _db.Remove(doc); }
}
