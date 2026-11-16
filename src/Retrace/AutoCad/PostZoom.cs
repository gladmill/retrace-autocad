using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Retrace.Core;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Retrace.AutoCad;

/// <summary>Setting <c>postTestZoom</c>: after the last step of a replayed test, the model-space view is set through the API (Editor.SetCurrentView), not with a ZOOM command, so nothing
/// shows up as a step, in the command history the test compares, or in the recorder. The view is not part of the state hash (that reads entities and layers only).
/// "last created" = the newest model-space object that is not erased and whose handle is at or above the database's handle seed read when the run's first step started: handles
/// only grow while objects are made, so the highest one is the one made last, and an object that was there before the test is never taken for the test's work.</summary>
static class PostZoom
{
    public const double Margin = 0.15;           // lastEntity: 15 % of the object's larger side on every side
    public const double ExtentsMargin = 0.02;    // extents: a small border, as ZOOM Extents leaves
    public const double MaxSpan = 1e15;          // a box wider than this (an Xline or Ray reports an enormous one) is no usable extents
    public const double PointFraction = 0.05;    // a point-like object: at least 5 % of the current view's height

    /// <summary>The handle seed of the drawing now (objects made after this have handles at or above it); 0 when it cannot be read.</summary>
    public static long Seed(Document doc)
    {
        try { return doc.Database.Handseed.Value; } catch (Exception ex) { Log.Error("post-test zoom: handle seed", ex); return 0; }
    }

    /// <summary>Zooms as the setting says. Returns a short text of what was done (for the log), or null when nothing was changed. Never throws.</summary>
    /// <param name="blocked">true when the zoom could not be tried now (the drawing is not the active one, a command or a modal window is open): a later try may work.
    /// False with a null result means the zoom does not apply (layout, nothing created, no usable extents, perspective view).</param>
    /// <param name="atCommandEnd">called from the command-ended event of the command that made the objects: the command is still "in progress" for AutoCAD then, so that check is skipped (a dialog still blocks).</param>
    /// <param name="requireCreated">setting recordZoom: zoom (extents too) only when a live model-space object has a handle at or above <paramref name="seed"/>; the post-test zoom leaves it off.</param>
    public static string? Run(Document doc, string mode, long seed, out bool blocked, bool requireCreated = false, bool atCommandEnd = false)
    {
        blocked = false;
        try
        {
            if (mode == PostTestZoomModes.None) return null;
            if (!AcApp.DocumentManager.Cast<Document>().Contains(doc) || !ReferenceEquals(doc, AcApp.DocumentManager.MdiActiveDocument)) { blocked = true; return null; }
            if ((!atCommandEnd && !ViewRestorer.Quiet(doc)) || SdiDocs.IsModalOpen()) { blocked = true; return null; }
            var db = doc.Database;
            if (!db.TileMode) return null;   // a layout is current: the view is left alone
            if (requireCreated && !AnyCreated(db, seed)) return null;
            using var locked = doc.LockDocument();
            var ed = doc.Editor;
            Point3d lo, hi;
            string what;
            if (mode == PostTestZoomModes.Extents)
            {
                db.UpdateExt(true);
                lo = db.Extmin; hi = db.Extmax;
                what = "extents";
            }
            else
            {
                if (seed <= 0) return null;
                if (!LastCreated(db, seed, out lo, out hi, out var handle)) return null;
                what = "last created object " + handle;
            }
            if (hi.X < lo.X || hi.Y < lo.Y || hi.Z < lo.Z) return null;
            if (hi.X - lo.X > MaxSpan || hi.Y - lo.Y > MaxSpan || hi.Z - lo.Z > MaxSpan) { Log.Info("post-test zoom: the extents are absurd (an infinite line?); the view is left as it is"); return null; }   // an empty drawing reports a reversed box; checked here, before the corners are turned into the view's coordinates
            using var cur = ed.GetCurrentView();
            if (cur.Height <= 0 || cur.Width <= 0) return null;
            if (cur.PerspectiveEnabled) { Log.Info("post-test zoom: the view is a perspective view; the view is left as it is"); return null; }
            var toDcs = Dcs(cur).Inverse();
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            for (int i = 0; i < 8; i++)
            {
                var p = new Point3d((i & 1) == 0 ? lo.X : hi.X, (i & 2) == 0 ? lo.Y : hi.Y, (i & 4) == 0 ? lo.Z : hi.Z).TransformBy(toDcs);
                x0 = Math.Min(x0, p.X); x1 = Math.Max(x1, p.X); y0 = Math.Min(y0, p.Y); y1 = Math.Max(y1, p.Y);
            }
            var margin = mode == PostTestZoomModes.Extents ? ExtentsMargin : Margin;
            if (ZoomWindow.Fit(x0, y0, x1, y1, cur.Width / cur.Height, margin, cur.Height * PointFraction) is not { } w) return null;
            using var nv = ed.GetCurrentView();   // a copy of the view: every property the zoom does not set stays
            nv.CenterPoint = new Point2d(w.CenterX, w.CenterY);
            nv.Width = w.Width; nv.Height = w.Height;
            ed.SetCurrentView(nv);
            return $"{what}, window {w.Width:0.###} x {w.Height:0.###} at {w.CenterX:0.###},{w.CenterY:0.###}";
        }
        catch (Exception ex) { Log.Error(requireCreated ? "record zoom" : "post-test zoom", ex); return null; }
    }

    /// <summary>True when a live model-space object has a handle at or above <paramref name="seed"/>. The handle seed is compared first: unchanged means nothing at all was made.</summary>
    static bool AnyCreated(Database db, long seed)
    {
        if (seed <= 0) return false;
        try { if (db.Handseed.Value == seed) return false; } catch { }
        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
        return RecordZoomRules.Created(seed, ms.Cast<ObjectId>().Select(id => id.Handle.Value));
    }

    /// <summary>The model-space object with the highest handle at or above <paramref name="seed"/> (erased ones are not listed), its extents in WCS. False when the test made
    /// none or the object has no extents.</summary>
    static bool LastCreated(Database db, long seed, out Point3d lo, out Point3d hi, out string handle)
    {
        lo = hi = Point3d.Origin; handle = "";
        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
        var ids = ms.Cast<ObjectId>().ToList();
        var bestH = RecordZoomRules.Newest(seed, ids.Select(id => id.Handle.Value));
        var best = bestH < 0 ? ObjectId.Null : ids.First(id => id.Handle.Value == bestH);
        if (best.IsNull) return false;
        try
        {
            var ext = ((Entity)tr.GetObject(best, OpenMode.ForRead)).GeometricExtents;
            lo = ext.MinPoint; hi = ext.MaxPoint; handle = best.Handle.ToString();
            return true;
        }
        catch (Exception ex) { Log.Info("post-test zoom: the last created object has no extents (" + ex.Message + "); the view is left as it is"); return false; }
    }

    /// <summary>The matrix from the view's own coordinates (DCS) to WCS.</summary>
    static Matrix3d Dcs(ViewTableRecord v)
    {
        var m = Matrix3d.PlaneToWorld(v.ViewDirection);
        m = Matrix3d.Displacement(v.Target - Point3d.Origin) * m;
        m = Matrix3d.Rotation(-v.ViewTwist, v.ViewDirection, v.Target) * m;
        return m;
    }
}
