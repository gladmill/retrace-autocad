using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Retrace.Core;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Retrace.AutoCad;

/// <summary>Setting <c>postTestZoom</c>: after the last step of a replayed test, the model-space view is set through the API (Editor.SetCurrentView), not with a ZOOM command, so nothing
/// shows up as a step, in the command history the test compares, or in the recorder. The view is not part of the state hash (that reads entities and layers only).
/// "last created" = the newest live entity whose handle is at or above the database's handle seed read when the run's first step started (handles only grow while objects are made, so
/// the highest one is the one made last, and an object that was there before the test is never taken for the test's work), resolved upward to what sits in model space: see <see cref="ZoomParent"/>
/// (an attribute to its block reference, a piece of a new block definition to the reference made by the same command, a member of a group made by the command to the whole group); an object that was there before the run is never the target, even when the run added a vertex or an attribute to it.</summary>
static class PostZoom
{
    public const double Margin = 0.15;           // lastEntity: 15 % of the object's larger side on every side
    public const double ExtentsMargin = 0.02;    // extents: a small border, as ZOOM Extents leaves
    public const double MaxSpan = 1e15;          // a box wider than this (an Xline or Ray reports an enormous one) is no usable extents
    public const double PointFraction = 0.05;    // a point-like object: at least 5 % of the current view's height

    /// <summary>The window height the minimum-size (point-like object) rule made last: a zoom to a point-like object keeps it instead of taking 5 % of a view that rule made itself.</summary>
    static readonly PointZoomMemory PointMemory = new();

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
            using var cur = ed.GetCurrentView();
            // the object size under which the minimum-size rule decides the window (a box that small gets its extents looked at again)
            var smallSide = (PointMemory.ViewMatches(cur.Height) ? Math.Max(cur.Height * PointFraction, PointMemory.Minimum) : cur.Height * PointFraction) / (1 + 2 * Margin);
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
                if (!LastCreated(db, seed, smallSide, out lo, out hi, out what)) return null;
            }
            if (hi.X < lo.X || hi.Y < lo.Y || hi.Z < lo.Z) return null;
            if (hi.X - lo.X > MaxSpan || hi.Y - lo.Y > MaxSpan || hi.Z - lo.Z > MaxSpan) { Log.Info("post-test zoom: the extents are absurd (an infinite line?); the view is left as it is"); return null; }   // an empty drawing reports a reversed box; checked here, before the corners are turned into the view's coordinates
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
            if (ZoomWindow.FitStable(x0, y0, x1, y1, cur.Width, cur.Height, margin, PointFraction, PointMemory, m => Log.Info("post-test zoom: " + m)) is not { } w) return null;
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

    public const int BudgetMs = 100;             // lastEntity: the walk over the new handles may take this long, then the plain model-space rule is used

    /// <summary>The most recently created thing, resolved to what sits in model space (<see cref="ZoomResolver{T}"/>): the object with the highest handle at or above <paramref name="seed"/> that is a live
    /// entity and can be resolved upward to a new object (attribute to its block reference, a piece inside a block definition to the reference made by the same command, a group made by the command
    /// to the whole group), its extents (all members' for a group) in WCS. The walk has a time budget (<see cref="BudgetMs"/>); past it, or when nothing resolves, the newest model-space object is used.
    /// False when the run made none or nothing has usable extents.</summary>
    static bool LastCreated(Database db, long seed, double smallSide, out Point3d lo, out Point3d hi, out string what)
    {
        lo = hi = Point3d.Origin; what = "";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool Expired() => sw.ElapsedMilliseconds > BudgetMs;
        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        var owners = new AcOwners(db, tr);
        ZoomTarget<AcOwners.Ob>? target = null;
        bool timedOut = false;
        try { target = ZoomParent.Newest(seed, owners.Candidates(seed, Expired), owners, out timedOut, Expired, (c, ex) => Log.Info("post-test zoom: object " + c.Id.Handle + " skipped (" + ex.Message + ")")); }
        catch (Exception ex) { Log.Info("post-test zoom: resolving the newest object failed (" + ex.Message + "); the newest model-space object is used"); }
        if (timedOut) Log.Info("post-test zoom: resolving the newest object took more than " + BudgetMs + " ms (" + owners.Probes + " handles looked at); the newest model-space object is used");
        var walkMs = sw.ElapsedMilliseconds;
        target ??= NewestInModelSpace(db, seed, tr);
        if (target == null) return false;
        var boxes = new List<ZoomBox>();
        // Bounds / Explode fall-backs and their log lines only for a single object or a group of at most MaxFallbackMembers: a group of thousands gets none (one summary line), so the work and the log stay bounded
        bool fallbacks = target.Objects.Count <= MaxFallbackMembers;
        int logged = 0, pointLike = 0;   // per-object lines: the first 5, then only members that used a fall-back (at most MaxFallbackMembers of them)
        foreach (var o in target.Objects)
        {
            try
            {
                var ent = tr.GetObject(o.Id, OpenMode.ForRead) as Entity;
                if (ent == null || ent.BlockId != SymbolUtilityServices.GetBlockModelSpaceId(db)) continue;   // a group member in another space
                var pick = ExtentsOf(ent, smallSide, fallbacks);
                if (pick.Rule == ZoomExtentsRule.Point && !fallbacks) pointLike++;
                if (logged < 5 || (fallbacks && pick.Rule != ZoomExtentsRule.Object))
                {
                    logged++;
                    var cls = ent.GetRXClass();
                    Log.Info($"post-test zoom: object {o.Id.Handle} class {cls.Name} (DXF {cls.DxfName}), proxy {ent.IsAProxy}: {pick.Seen}; window from: {RuleText(pick.Rule)}");
                }
                if (pick.Box is { } pb) boxes.Add(pb);
            }
            catch (Exception ex) { Log.Info("post-test zoom: object " + o.Id.Handle + " has no extents (" + ex.Message + "); left out"); }
        }
        if (pointLike > 0) Log.Info("post-test zoom: " + pointLike + " member(s) point-like, fall-back not tried (" + target.Objects.Count + " members)");
        if (ZoomBox.Union(boxes, MaxSpan, out var skippedBoxes) is not { } u) { Log.Info("post-test zoom: the last created object has no usable extents (an infinite line?); the view is left as it is"); return false; }
        if (skippedBoxes > 0) Log.Info("post-test zoom: " + skippedBoxes + " member(s) with no usable extents (an infinite line?) left out");
        lo = new Point3d(u.MinX, u.MinY, u.MinZ); hi = new Point3d(u.MaxX, u.MaxY, u.MaxZ); what = "last created " + target.Why + " [" + walkMs + " ms, " + owners.Probes + " handles]";
        return true;
    }

    static string RuleText(ZoomExtentsRule r) => r switch
    {
        ZoomExtentsRule.Object => "object extents",
        ZoomExtentsRule.Bounds => "Entity.Bounds",
        ZoomExtentsRule.Exploded => "exploded pieces",
        ZoomExtentsRule.Point => "point rule (minimum window)",
        _ => "none",
    };

    static ZoomBox ToBox(Extents3d e) => new(e.MinPoint.X, e.MinPoint.Y, e.MinPoint.Z, e.MaxPoint.X, e.MaxPoint.Y, e.MaxPoint.Z);

    public const int MaxFallbackMembers = 20;

    /// <summary>The extents of an entity in WCS (<see cref="ZoomExtentsPick"/>). GeometricExtents first; when that is point-like, degenerate, reversed, throws or is so small that the minimum-size rule would
    /// decide the window (<paramref name="smallSide"/>), and <paramref name="fallbacks"/> is set, Entity.Bounds, then the union of the pieces of a temporary Explode; used only when clearly larger. Entity.Bounds
    /// is not used for an MInsert (it covers the first cell only; GeometricExtents gets the array's other cells added, Bounds would not). Nothing is opened for write or added to the database.</summary>
    static ZoomExtentsPick.Result ExtentsOf(Entity ent, double smallSide, bool fallbacks) =>
        ZoomExtentsPick.Pick(() => ToBox(GeometricOf(ent)), () => ent is MInsertBlock ? null : ent.Bounds is { } b ? ToBox(b) : null, () => ExplodedExtents(ent), MaxSpan, smallSide, fallbacks);

    public const int ExplodeBudgetMs = 100;

    /// <summary>A temporary explode, one level. The time budget is checked between pieces only: it cannot bound <c>Entity.Explode</c> itself. A cut-short walk is no result (never zoom to part of an object).
    /// Only pieces with a null ObjectId are disposed; a piece that lives in a database (a broken custom entity might return one, or itself) is never disposed and the whole result is discarded.</summary>
    static ExplodeResult ExplodedExtents(Entity ent)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var pieces = new DBObjectCollection();
        try
        {
            ent.Explode(pieces);
            int total = pieces.Count;
            foreach (DBObject o in pieces)
                if (ReferenceEquals(o, ent) || !o.ObjectId.IsNull) return new ExplodeResult(null, 0, total, "returned a piece that belongs to the database: result discarded");
            var boxes = new List<ZoomBox>();
            int used = 0;
            foreach (DBObject o in pieces)
            {
                if (sw.ElapsedMilliseconds > ExplodeBudgetMs) return new ExplodeResult(null, used, total, $"stopped at the time budget after {used} of {total} pieces");
                used++;
                try { if (o is Entity pe) boxes.Add(ToBox(pe.GeometricExtents)); } catch { }
            }
            return new ExplodeResult(ZoomBox.Union(boxes, MaxSpan, out _), used, total, null);
        }
        finally
        {
            foreach (DBObject o in pieces)
                try { if (!ReferenceEquals(o, ent) && o.ObjectId.IsNull) o.Dispose(); } catch { }
        }
    }

    /// <summary>GeometricExtents; an MInsert reports only its first cell, so the whole array is that cell plus the cells at the other three corners.</summary>
    static Extents3d GeometricOf(Entity ent)
    {
        var cell = ent.GeometricExtents;
        var e = cell;
        if (ent is MInsertBlock mi && (mi.Columns > 1 || mi.Rows > 1))
        {
            var ocs = Matrix3d.PlaneToWorld(mi.Normal);
            foreach (var (c, r) in new[] { (mi.Columns - 1, 0), (0, mi.Rows - 1), (mi.Columns - 1, mi.Rows - 1) })
            {
                var off = new Vector3d(c * mi.ColumnSpacing, r * mi.RowSpacing, 0).RotateBy(mi.Rotation, Vector3d.ZAxis).TransformBy(ocs);
                var moved = new Extents3d(cell.MinPoint, cell.MaxPoint);
                moved.TransformBy(Matrix3d.Displacement(off));
                e.AddExtents(moved);
            }
            Log.Info($"post-test zoom: MInsert {mi.ObjectId.Handle} {mi.Columns} x {mi.Rows}: GeometricExtents (first cell) {cell.MinPoint.X:0.###},{cell.MinPoint.Y:0.###} to {cell.MaxPoint.X:0.###},{cell.MaxPoint.Y:0.###}; whole array {e.MinPoint.X:0.###},{e.MinPoint.Y:0.###} to {e.MaxPoint.X:0.###},{e.MaxPoint.Y:0.###}");
        }
        return e;
    }

    /// <summary>The old rule, when the walk found nothing it could resolve (or ran out of time): the model-space object with the highest handle at or above the seed.</summary>
    static ZoomTarget<AcOwners.Ob>? NewestInModelSpace(Database db, long seed, Transaction tr)
    {
        if (tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead) is not BlockTableRecord ms) return null;
        var ids = ms.Cast<ObjectId>().ToList();
        var bestH = RecordZoomRules.Newest(seed, ids.Select(id => id.Handle.Value));
        if (bestH < 0) return null;
        var id = ids.First(i => i.Handle.Value == bestH);
        return new ZoomTarget<AcOwners.Ob>(new[] { new AcOwners.Ob(id) }, "object " + id.Handle + " (model-space rule)");
    }

    /// <summary>The drawing's owner relations for <see cref="ZoomResolver{T}"/>. Nothing here throws on purpose: the resolver's caller skips a candidate that does.</summary>
    sealed class AcOwners : IZoomOwners<AcOwners.Ob>
    {
        public const long MaxProbes = 1_000_000;   // handles looked at from the top down before the walk gives up
        public sealed class Ob { public readonly ObjectId Id; public Ob(ObjectId id) { Id = id; } }

        readonly Database _db; readonly Transaction _tr; readonly ObjectId _ms;
        readonly Autodesk.AutoCAD.Runtime.RXClass _entity = Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(Entity));
        readonly Autodesk.AutoCAD.Runtime.RXClass _btr = Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(BlockTableRecord));
        readonly Autodesk.AutoCAD.Runtime.RXClass _group = Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(Group));
        public long Probes;
        public AcOwners(Database db, Transaction tr) { _db = db; _tr = tr; _ms = SymbolUtilityServices.GetBlockModelSpaceId(db); }

        /// <summary>Live entities with a handle at or above the seed, the highest handle first. Walks the handle range down; the class test needs no open, so a handle that is no entity (a dictionary,
        /// an xrecord, a group) or no object, and an erased one, costs almost nothing. Stops when <paramref name="expired"/> says so.</summary>
        public IEnumerable<Ob> Candidates(long seed, Func<bool> expired)
        {
            long top;
            try { top = _db.Handseed.Value - 1; } catch { yield break; }
            for (long h = top; h >= seed && h > 0; h--)
            {
                if (++Probes > MaxProbes) { Log.Info("post-test zoom: more than " + MaxProbes + " handles since the seed"); yield break; }
                if ((Probes & 63) == 0 && expired()) yield break;
                if (!_db.TryGetObjectId(new Autodesk.AutoCAD.DatabaseServices.Handle(h), out var id) || id.IsNull || id.IsErased) continue;
                if (!id.ObjectClass.IsDerivedFrom(_entity)) continue;
                yield return new Ob(id);
            }
        }

        public long Handle(Ob obj) => obj.Id.Handle.Value;

        public ZoomOwner OwnerOf(Ob obj, out long ownerHandle)
        {
            ownerHandle = 0;
            var owner = (_tr.GetObject(obj.Id, OpenMode.ForRead) as DBObject)?.OwnerId ?? ObjectId.Null;
            if (owner.IsNull || owner.IsErased) return ZoomOwner.None;
            ownerHandle = owner.Handle.Value;
            if (owner == _ms) return ZoomOwner.Space;
            var cls = owner.ObjectClass;
            if (cls.IsDerivedFrom(_entity)) return ZoomOwner.Entity;
            if (cls.IsDerivedFrom(_btr)) return ZoomOwner.Definition;
            return ZoomOwner.None;
        }

        public Ob? ParentEntity(Ob obj)
        {
            var owner = (_tr.GetObject(obj.Id, OpenMode.ForRead) as DBObject)?.OwnerId ?? ObjectId.Null;
            return owner.IsNull || owner.IsErased ? null : new Ob(owner);
        }

        public IEnumerable<Ob> ReferencesOfDefinition(Ob obj)
        {
            var btrId = ((_tr.GetObject(obj.Id, OpenMode.ForRead) as Entity)?.BlockId) ?? ObjectId.Null;
            if (btrId.IsNull || _tr.GetObject(btrId, OpenMode.ForRead) as BlockTableRecord is not { } btr) yield break;
            if (btr.IsLayout || btr.IsFromExternalReference || btr.IsFromOverlayReference) yield break;
            // a dimension's anonymous block ("*D..."): the dimension is no block reference, so asking would find nothing; the walk reaches the dimension itself
            if (btr.IsAnonymous && !btr.IsDynamicBlock && btr.Name.StartsWith("*D", StringComparison.OrdinalIgnoreCase)) yield break;
            var ids = new List<ObjectId>();
            ids.AddRange(btr.GetBlockReferenceIds(true, false).Cast<ObjectId>());
            if (btr.IsDynamicBlock) foreach (ObjectId anon in btr.GetAnonymousBlockIds()) if (_tr.GetObject(anon, OpenMode.ForRead) is BlockTableRecord ab) ids.AddRange(ab.GetBlockReferenceIds(true, false).Cast<ObjectId>());
            foreach (var id in ids) if (!id.IsErased) yield return new Ob(id);
        }

        /// <summary>The groups an entity belongs to, from its persistent reactors (a group puts itself there): no scan of the group dictionary.</summary>
        public IReadOnlyList<long> GroupsOf(Ob obj)
        {
            var res = new List<long>();
            if (_tr.GetObject(obj.Id, OpenMode.ForRead) is not DBObject o) return res;
            foreach (ObjectId r in o.GetPersistentReactorIds())
                if (!r.IsNull && !r.IsErased && r.ObjectClass.IsDerivedFrom(_group)) res.Add(r.Handle.Value);
            return res;
        }

        public IReadOnlyList<Ob> GroupMembers(long groupHandle)
        {
            var members = new List<Ob>();
            if (!_db.TryGetObjectId(new Autodesk.AutoCAD.DatabaseServices.Handle(groupHandle), out var gid) || _tr.GetObject(gid, OpenMode.ForRead) as Group is not { } g) return members;
            foreach (ObjectId id in g.GetAllEntityIds()) if (!id.IsNull && !id.IsErased) members.Add(new Ob(id));
            return members;
        }
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
