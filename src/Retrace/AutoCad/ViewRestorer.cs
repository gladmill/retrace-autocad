using System.Windows.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Retrace.AutoCad;

/// <summary>Puts a person's view back after a replay that ended while AutoCAD was busy (Stop in the middle of a command, an error, a dialog or a command a person has to finish): waits, from the
/// application's own timer, until the drawing has no command running, no input queued and no dialog (quiet for three ticks in a row), then restores the view.
/// A pending restore is dropped, and its view disposed, when: the drawing is gone, the space or layout is no longer the one it was handed over in, the view changed since the hand-over
/// (the person zoomed or panned: their own view wins), the drawing went quiet but the view could not be set for ten seconds, or, as a hard cap so nothing stays pending, ten minutes
/// have passed. The person may take as long as they need to finish what they are doing: the ten seconds count only from the first moment the drawing is quiet. The timer runs only while
/// something is pending, and <see cref="Shutdown"/> (the plugin's Terminate) stops it for good.</summary>
static class ViewRestorer
{
    const int QuietExpireSeconds = 10, HardCapMinutes = 10;

    sealed class Pending
    {
        public Document Doc = null!;
        public IntPtr DbId;   // the drawing the view was saved in (SDI replaces the drawing inside the same Document)
        public ViewTableRecord View = null!;
        public int Quiet, Tries;
        public DateTime Since;
        public DateTime? QuietSince;   // the first moment the drawing was quiet
        public string? Space, Layout, Key;
    }

    static readonly List<Pending> Items = new();
    static DispatcherTimer? _timer;
    static bool _shutdown;

    /// <summary>true once the plugin is unloading: nothing may be sent to AutoCAD and no timer started.</summary>
    public static bool IsShutdown => _shutdown;

    /// <summary>Takes over the saved view of <paramref name="doc"/>: it is set once the drawing is quiet, if nothing has changed meanwhile.</summary>
    public static void Hand(Document doc, ViewTableRecord saved)
    {
        if (_shutdown) { try { saved.Dispose(); } catch { } return; }
        var now = ViewControl.Read(doc);
        Items.Add(new Pending { Doc = doc, DbId = SdiDocs.DbId(doc), View = saved, Since = DateTime.Now, Space = now?.Space, Layout = now?.Layout, Key = ViewControl.ViewKey() });
        if (_timer != null) return;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => Log.Safe("view restorer", Tick);
        _timer.Start();
    }

    /// <summary>The plugin unloads: stops the timer and drops everything pending (disposed, not restored).</summary>
    public static void Shutdown()
    {
        _shutdown = true;
        _timer?.Stop(); _timer = null;
        foreach (var p in Items) try { p.View.Dispose(); } catch { }
        Items.Clear();
    }

    /// <summary>true when the drawing is idle: no command, no LISP, no queued input, no dialog.</summary>
    public static bool Quiet(Document doc)
    {
        try { return string.IsNullOrEmpty(doc.CommandInProgress) && doc.Editor.IsQuiescent; }
        catch (Exception) { return false; }
    }

    static void Tick()
    {
        foreach (var p in Items.ToList())
        {
            if ((DateTime.Now - p.Since).TotalMinutes > HardCapMinutes) { Drop(p, "expired after " + HardCapMinutes + " minutes"); continue; }
            if (p.QuietSince is { } q && (DateTime.Now - q).TotalSeconds > QuietExpireSeconds) { Drop(p, "the drawing was quiet but the view could not be set"); continue; }
            if (!AcApp.DocumentManager.Cast<Document>().Contains(p.Doc)) { Drop(p, "its drawing is gone"); continue; }
            if (!SdiDocs.SameDb(SdiDocs.DbId(p.Doc), p.DbId)) { Drop(p, "another drawing was opened in its place (SDI)"); continue; }
            if (!ReferenceEquals(p.Doc, AcApp.DocumentManager.MdiActiveDocument) || !Quiet(p.Doc)) { p.Quiet = 0; p.QuietSince = null; continue; }
            p.QuietSince ??= DateTime.Now;
            if (++p.Quiet < 3) continue;
            var now = ViewControl.Read(p.Doc);
            if (now == null || now.Space != p.Space || now.Layout != p.Layout) { Drop(p, "another space or layout is current"); continue; }
            if (ViewControl.ViewKey() != p.Key) { Drop(p, "the view changed since"); continue; }   // the person zoomed or panned: that view stays
            if (ViewControl.Restore(p.Doc, p.View)) { Log.Info("replay view restored"); Items.Remove(p); }
            else if (++p.Tries >= 3) Drop(p, "it could not be set");
            else p.Quiet = 0;
        }
        if (Items.Count == 0) { _timer?.Stop(); _timer = null; }
    }

    static void Drop(Pending p, string why)
    {
        Items.Remove(p);
        Log.Info("the view the person had was not put back: " + why);
        try { p.View.Dispose(); } catch { }
    }
}
