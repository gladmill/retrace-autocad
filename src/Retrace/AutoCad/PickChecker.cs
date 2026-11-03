using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Retrace.Core;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Retrace.AutoCad;

/// <summary>Before a replay types a recorded pick point: does it still pick the object it was picked on, and nothing else? Read-only, command or timer context.
/// One instance per replayed step: the current view, the UCS and the pickbox are read once, and the objects of the space are listed once. The checks look at the drawing as it is
/// before the step, so <see cref="PickReplay"/> allows only the first pick of a step that can change the drawing (its edit rule).
/// A point is allowed only when every check ran and passed; a check that cannot be made is a reason to use the handle (<see cref="Problem"/> returns it).
/// The pick box is PICKBOX pixels; in the shown view (<see cref="ViewSpec.Fit"/> for this window) one pixel is the fitted view's height / the viewport's height in pixels.</summary>
sealed class PickChecker
{
    const int MaxScan = 50000, MaxBlockScan = 5000;
    readonly Document _doc;
    bool _read;
    ViewSpec? _cur;
    Matrix3d _ucs;
    double _px;
    int _box = 3;

    public PickChecker(Document doc) { _doc = doc; }

    void Read()
    {
        if (_read) return;
        _read = true;
        _cur = ViewControl.Read(_doc);
        try { _ucs = _doc.Editor.CurrentUserCoordinateSystem; } catch { _ucs = Matrix3d.Identity; _cur = null; }
        try { _px = AcApp.GetSystemVariable("SCREENSIZE") is Point2d p ? p.Y : 0; } catch { _px = 0; }
        try { _box = Math.Max(1, Convert.ToInt32(AcApp.GetSystemVariable("PICKBOX"))); } catch { }
    }

    /// <summary>Why this view cannot be shown now, or null (the current view is read once).</summary>
    public string? ViewProblem(ViewSpec v) { Read(); return ViewControl.Problem(_cur, v); }

    /// <summary>The picked point and the view it is checked in, as one bundle for the helpers.</summary>
    sealed record Ctx(Point3d P, double[] Pw, Vector3d Dir, double[] DirA, (double[] X, double[] Y) Basis, double Tol);

    /// <summary>null when typing <paramref name="ptUcs"/> (as recorded: current UCS) in <paramref name="view"/> picks the object with <paramref name="handle"/> and no other; else why not.
    /// <paramref name="view"/> is never a floating-viewport view (<see cref="PickReplay"/> does not claim one).</summary>
    public string? Problem(ViewSpec view, string handle, string? dxf, double[] ptUcs, bool nested)
    {
        try
        {
            Read();
            if (_cur == null) return "the current view cannot be read";
            var z = _ucs.CoordinateSystem3d.Zaxis;
            if (!view.IsPlanOf(new[] { z.X, z.Y, z.Z })) return "the recorded view is not the plan view of the current UCS";
            double aspect = _cur.Height > 0 ? _cur.Width / _cur.Height : 0;
            if (!(aspect > 0) || !(_px > 0)) return "the size of the window is unknown";
            double tol = _box * view.Fit(aspect).Height / _px;
            var p = new Point3d(ptUcs[0], ptUcs[1], ptUcs.Length > 2 ? ptUcs[2] : 0).TransformBy(_ucs);
            var pw = new[] { p.X, p.Y, p.Z };
            if (!ViewMath.InView(view, aspect, pw)) return "the point is outside the shown view";
            var dir = new Vector3d(view.Direction[0], view.Direction[1], view.Direction[2]);
            var ctx = new Ctx(p, pw, dir, view.Direction, ViewMath.PlaneBasis(view.Direction), tol);

            var db = _doc.Database;
            if (!HandleIds.TryGet(db, handle, out var id)) return "the object is not in this drawing";
            ObjectId space;
            if (view.Space == "paper")
            {
                var lid = LayoutManager.Current.GetLayoutId(view.Layout);
                if (lid.IsNull) return "the layout is not in this drawing";
                using var tl = db.TransactionManager.StartOpenCloseTransaction();
                space = ((Layout)tl.GetObject(lid, OpenMode.ForRead)).BlockTableRecordId;
            }
            else space = SymbolUtilityServices.GetBlockModelSpaceId(db);

            using var tr = db.TransactionManager.StartOpenCloseTransaction();
            if (tr.GetObject(id, OpenMode.ForRead) is not Entity target) return "the object is not an entity";
            if (dxf != null && dxf != "?" && !string.Equals(target.GetRXClass().DxfName, dxf, StringComparison.OrdinalIgnoreCase)) return "the object is another kind than recorded";
            if (nested && target.OwnerId == space) nested = false;   // it sits directly in the space: a direct pick
            if (!nested && PickableProblem(tr, target) is { } tp) return tp;

            var excluded = new HashSet<ObjectId>();
            if (!nested)
            {
                if (target.OwnerId != space) return "the object is not in the space shown";
                var d = Distance(target, ctx);
                if (double.IsNaN(d)) return $"a {target.GetRXClass().DxfName} cannot be checked (not a curve)";
                if (!(d <= tol)) return Far(d, tol);
                excluded.Add(id);
                return OtherWithin(tr, space, ctx, excluded);
            }

            // inside a block: every chain of block references that ends in the space shown
            double best = double.MaxValue;
            var hits = new List<Chain>();
            foreach (var chain in Chains(tr, target.OwnerId, space, 0, new[] { 0 }))
            {
                Entity? copy = null;
                try
                {
                    copy = target.GetTransformedCopy(chain.ToWorld(tr, 0));
                    var dd = Distance(copy, ctx);
                    if (double.IsNaN(dd)) return $"a {target.GetRXClass().DxfName} inside a block cannot be checked (not a curve)";
                    best = Math.Min(best, dd);
                    if (dd <= tol) hits.Add(chain);
                }
                catch (Exception) { /* not transformable: this chain says nothing */ }
                finally { copy?.Dispose(); }
            }
            if (best == double.MaxValue) return "the object is not inside a block of the space shown";
            if (hits.Count == 0) return Far(best, tol);
            if (hits.Count > 1) return "more than one chain of block references brings the object under the point: which one it picks cannot be told";
            foreach (var chain in hits)
            {
                // the references that hold it must be pickable, and nothing else inside the blocks may be picked as well
                // the object and the references that hold it must be pickable (layer 0 takes the layer of the insert that holds the block, at every level)
                if (PickableProblem(tr, target, chain, 0) is { } tp2) return tp2;
                for (int i = 0; i < chain.Refs.Count; i++)
                    if (tr.GetObject(chain.Refs[i], OpenMode.ForRead) is Entity r && PickableProblem(tr, r, chain, i + 1) is { } rp) return "a block that holds it: " + rp;
                if (SiblingsProblem(tr, chain, target, ctx) is { } sp) return sp;
                excluded.Add(chain.Refs[^1]);   // the outermost reference is in the space: it is the holder, not "another object"
            }
            return OtherWithin(tr, space, ctx, excluded);
        }
        catch (Exception ex)
        {
            Log.Error("pick check", ex);
            return "the check failed: " + ex.Message;
        }
    }

    static string Far(double d, double tol) =>
        $"the point is {d.ToString("0.###", CultureInfo.InvariantCulture)} from the object, more than the pick box ({tol.ToString("0.###", CultureInfo.InvariantCulture)})";

    /// <summary>Why this object cannot be picked in the view shown (invisible; its layer off, frozen or locked), or null.</summary>
    static string? PickableProblem(Transaction tr, Entity e, Chain? chain = null, int level = 0)
    {
        if (!e.Visible) return "the object is invisible";
        var layer = chain == null ? e.LayerId : EffectiveLayer(tr, e.LayerId, chain, level);
        if (tr.GetObject(layer, OpenMode.ForRead) is not LayerTableRecord l) return "the layer cannot be read";
        if (l.IsOff) return $"layer {l.Name} is off";
        if (l.IsFrozen) return $"layer {l.Name} is frozen";
        if (l.IsLocked) return $"layer {l.Name} is locked";
        return null;
    }

    // ---- block chains ----

    /// <summary>One way an object of a block comes into the space shown: the block references from the innermost to the outermost (the one in the space).</summary>
    sealed record Chain(List<ObjectId> Refs)
    {
        /// <summary>The transform from the contents of the block that is <paramref name="level"/> references up (0 = the block that holds the object) to world space.</summary>
        public Matrix3d ToWorld(Transaction tr, int level)
        {
            var m = Matrix3d.Identity;
            for (int i = Refs.Count - 1; i >= level; i--) m = m * ((BlockReference)tr.GetObject(Refs[i], OpenMode.ForRead)).BlockTransform;
            return m;
        }
    }

    /// <summary>The ways an object of block <paramref name="owner"/> comes into the world space <paramref name="space"/>: only chains of block references that end in that space. Bounded.</summary>
    static IEnumerable<Chain> Chains(Transaction tr, ObjectId owner, ObjectId space, int depth, int[] budget)
    {
        if (owner == space) { yield return new Chain(new List<ObjectId>()); yield break; }
        if (tr.GetObject(owner, OpenMode.ForRead) is not BlockTableRecord btr || btr.IsLayout || depth > 4) yield break;
        foreach (ObjectId rid in btr.GetBlockReferenceIds(true, false))
        {
            if (++budget[0] > 200) yield break;
            if (tr.GetObject(rid, OpenMode.ForRead) is not BlockReference br) continue;
            foreach (var up in Chains(tr, br.OwnerId, space, depth + 1, budget))
            {
                var refs = new List<ObjectId> { rid };
                refs.AddRange(up.Refs);
                yield return new Chain(refs);
            }
        }
    }

    /// <summary>Other objects inside the blocks of the chain, and the attributes of its block references, seen through it, that the point could pick as well (or that cannot be told); null when none.
    /// The pick point and the view direction go into block space once per level, and objects that are out of reach there by their extents are rejected without being copied; only the near
    /// ones (and those without extents) are moved through the chain and measured.</summary>
    string? SiblingsProblem(Transaction tr, Chain chain, Entity target, Ctx c)
    {
        for (int level = 0; level < chain.Refs.Count; level++)
        {
            // level 0: the block that holds the object; level i: the block that holds reference i-1
            var btrId = level == 0 ? target.OwnerId : ((BlockReference)tr.GetObject(chain.Refs[level - 1], OpenMode.ForRead)).OwnerId;
            var skip = level == 0 ? target.ObjectId : chain.Refs[level - 1];
            var m = chain.ToWorld(tr, level);
            bool similar = TrySimilarity(m, out var scale);
            Point3d pB = default; double[] pwB = Array.Empty<double>(), dirB = Array.Empty<double>(); (double[] X, double[] Y) basisB = default; double tolB = 0;
            if (similar)
            {
                var inv = m.Inverse();
                pB = c.P.TransformBy(inv);
                var d = c.Dir.TransformBy(inv);
                pwB = new[] { pB.X, pB.Y, pB.Z }; dirB = new[] { d.X, d.Y, d.Z };
                basisB = ViewMath.PlaneBasis(dirB); tolB = c.Tol / scale;
            }
            var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
            int n = 0;
            foreach (ObjectId oid in btr)
            {
                if (++n > MaxBlockScan) return $"more than {MaxBlockScan} objects in a block: what the point picks cannot be checked";
                if (oid == skip || oid.IsErased) continue;
                if (tr.GetObject(oid, OpenMode.ForRead) is not Entity e) continue;
                if (!e.Visible || LayerOffOrFrozen(tr, EffectiveLayer(tr, e.LayerId, chain, level))) continue;   // layer 0 takes the layer of the insert that holds the block
                var bx = similar ? KnownExtents(e) : null;   // (null for an XLINE or RAY, which have none)
                if (bx is { } b && ViewMath.BoxDistance(b.Min, b.Max, pwB, pwB, basisB) > tolB) continue;   // out of reach in block space
                if (Near(e, m, c, boxKnownNear: bx != null) is { } why) return "inside the block: " + why;
            }
        }
        // the attributes of every block reference of the chain: they are objects of their own that a click can pick (they sit in the space of the reference's owner)
        for (int k = 0; k < chain.Refs.Count; k++)
        {
            var br = (BlockReference)tr.GetObject(chain.Refs[k], OpenMode.ForRead);
            Matrix3d? m = k + 1 >= chain.Refs.Count ? null : chain.ToWorld(tr, k + 1);
            foreach (ObjectId aid in br.AttributeCollection)
            {
                if (aid.IsErased || tr.GetObject(aid, OpenMode.ForRead) is not AttributeReference a || a.Invisible || !a.Visible) continue;
                if (LayerOffOrFrozen(tr, EffectiveLayer(tr, a.LayerId, chain, k + 1))) continue;
                if (Near(a, m, c) is { } why) return "an attribute of a block that holds it: " + why;
            }
        }
        return null;
    }

    /// <summary>The layer that decides whether an object is shown: its own, but layer 0 takes the layer of the block reference that holds the block (and so on up the chain).
    /// <paramref name="level"/> is the level of the block the object sits in (the reference that holds that block is <c>Refs[level]</c>).</summary>
    static ObjectId EffectiveLayer(Transaction tr, ObjectId layer, Chain chain, int level)
    {
        for (int j = level; j < chain.Refs.Count && tr.GetObject(layer, OpenMode.ForRead) is LayerTableRecord l && l.Name == "0"; j++)
            layer = ((BlockReference)tr.GetObject(chain.Refs[j], OpenMode.ForRead)).LayerId;
        return layer;
    }

    /// <summary>true when <paramref name="m"/> is a rotation, a mirror and a uniform scale (distances keep their proportions: <paramref name="scale"/> is the factor).</summary>
    static bool TrySimilarity(Matrix3d m, out double scale)
    {
        scale = 1;
        try
        {
            var cs = m.CoordinateSystem3d;
            double lx = cs.Xaxis.Length, ly = cs.Yaxis.Length, lz = cs.Zaxis.Length;
            if (!(lx > 1e-12) || Math.Abs(lx - ly) > 1e-9 * lx || Math.Abs(lx - lz) > 1e-9 * lx) return false;
            double tol = 1e-9 * lx * lx;
            if (Math.Abs(cs.Xaxis.DotProduct(cs.Yaxis)) > tol || Math.Abs(cs.Xaxis.DotProduct(cs.Zaxis)) > tol || Math.Abs(cs.Yaxis.DotProduct(cs.Zaxis)) > tol) return false;
            scale = lx;
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>The extents of an entity in its own coordinates, or null (an XLINE or RAY has none: they are not asked, so nothing is thrown per pick).</summary>
    static (double[] Min, double[] Max)? KnownExtents(Entity e)
    {
        if (e is Xline or Ray) return null;
        try { var x = e.GeometricExtents; return (new[] { x.MinPoint.X, x.MinPoint.Y, x.MinPoint.Z }, new[] { x.MaxPoint.X, x.MaxPoint.Y, x.MaxPoint.Z }); }
        catch (Exception) { return null; }
    }

    static bool LayerOffOrFrozen(Transaction tr, ObjectId layer) => tr.GetObject(layer, OpenMode.ForRead) is LayerTableRecord l && (l.IsOff || l.IsFrozen);

    // ---- distances ----

    /// <summary>Distance, measured in the screen plane (along the view direction the geometry is flattened), from the point to a curve; NaN for anything that is not a curve:
    /// the extents of an INSERT, HATCH or MTEXT say nothing about where a click would hit it.</summary>
    static double Distance(Entity e, Ctx c)
    {
        if (e is not Curve cv) return double.NaN;
        try
        {
            var q = cv.GetClosestPointTo(c.P, c.Dir, false);
            var v = q - c.P;
            return ViewMath.PlaneLength(new[] { v.X, v.Y, v.Z }, c.DirA);
        }
        catch (Exception) { return double.NaN; }
    }

    /// <summary>A reason when <paramref name="e"/> (moved by <paramref name="m"/> when not null) may be picked by the point as well as the target, or cannot be told; null when it is clearly out of reach
    /// or cannot be picked at all. Curves (an XLINE or RAY too, which have no extents) are measured; anything else by its extents, conservatively; an empty text and the overall paper-space
    /// viewport (number 1) are never picked; any other object without extents is named.</summary>
    static string? Near(Entity e, Matrix3d? m, Ctx c, bool boxKnownNear = false)
    {
        Entity? copy = null;
        try
        {
            var ent = e;
            if (m != null) { copy = e.GetTransformedCopy(m.Value); ent = copy; }
            string who = $"{e.GetRXClass().DxfName} {e.Handle}";
            if (ent is Curve)
            {
                if (!boxKnownNear && TryBoxDistance(ent, c) is { } bd && bd > c.Tol) return null;   // cheap rejection first (the caller may have done it; an XLINE or RAY has no extents: measured)
                var d = Distance(ent, c);
                return double.IsNaN(d) ? $"{who} cannot be measured" : d <= c.Tol ? $"another object ({who}) may be picked by the point too" : null;
            }
            if (ent is Viewport vp && vp.Number == 1) return null;
            if ((ent is DBText t && string.IsNullOrWhiteSpace(t.TextString)) || (ent is MText mt && string.IsNullOrWhiteSpace(mt.Contents))) return null;
            var box = boxKnownNear ? 0 : TryBoxDistance(ent, c);   // (known near: the caller measured the extents already)
            if (box == null) return $"{who} has no extents, so what the point picks cannot be told";
            return box <= c.Tol ? $"another object ({who}) may be picked by the point too" : null;
        }
        catch (Exception ex) { return $"{e.GetRXClass().DxfName} {e.Handle} could not be checked ({ex.Message})"; }
        finally { copy?.Dispose(); }
    }

    static double? TryBoxDistance(Entity e, Ctx c) =>
        KnownExtents(e) is { } x ? ViewMath.BoxDistance(x.Min, x.Max, c.Pw, c.Pw, c.Basis) : null;

    // ---- the other objects of the space, listed once per step ----

    sealed record Item(ObjectId Id, bool Visible, double[]? Min, double[]? Max);
    ObjectId _listed;
    List<Item>? _items;
    string? _listProblem;

    /// <summary>A reason when another object of the space could be picked by the point as well, or when that cannot be told (too many objects, an object that cannot be measured);
    /// null when none can. The objects in <paramref name="exclude"/> (the target, or the blocks that hold a nested target) do not count.</summary>
    string? OtherWithin(Transaction tr, ObjectId space, Ctx c, HashSet<ObjectId> exclude)
    {
        if (_items == null || _listed != space) List(tr, space);
        if (_items == null) return _listProblem ?? "the objects of the space could not be listed";
        foreach (var it in _items)
        {
            if (!it.Visible || exclude.Contains(it.Id)) continue;
            if (it.Min != null && it.Max != null && ViewMath.BoxDistance(it.Min, it.Max, c.Pw, c.Pw, c.Basis) > c.Tol) continue;   // cheap rejection first
            if (tr.GetObject(it.Id, OpenMode.ForRead) is not Entity e) continue;
            if (Near(e, null, c, boxKnownNear: it.Min != null) is { } why) return why;
        }
        return null;
    }

    void List(Transaction tr, ObjectId space)
    {
        _items = null; _listProblem = null; _listed = space;
        var btr = (BlockTableRecord)tr.GetObject(space, OpenMode.ForRead);
        var layers = new Dictionary<ObjectId, bool>();
        var list = new List<Item>();
        foreach (ObjectId oid in btr)
        {
            if (list.Count >= MaxScan) { _listProblem = $"more than {MaxScan} objects in the space: what the point picks cannot be checked"; return; }
            if (oid.IsErased) continue;
            if (tr.GetObject(oid, OpenMode.ForRead) is not Entity e) continue;
            if (!layers.TryGetValue(e.LayerId, out var on))
            {
                on = !LayerOffOrFrozen(tr, e.LayerId);
                layers[e.LayerId] = on;
            }
            var ext = KnownExtents(e);
            list.Add(new Item(oid, on && e.Visible, ext?.Min, ext?.Max));
        }
        _items = list;
    }
}
