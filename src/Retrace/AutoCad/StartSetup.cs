using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;

namespace Retrace.AutoCad;

/// <summary>What <see cref="StartSetup.Apply"/> did to the test drawing.</summary>
sealed class StartSetupResult
{
    public List<string> Created { get; } = new();
    /// <summary>Layers the template had that the recording's start state did not, removed (unused).</summary>
    public List<string> Removed { get; } = new();
    /// <summary>Such layers a purge kept (in use), left as they are.</summary>
    public List<string> Kept { get; } = new();
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// Makes a test's own start drawing match the recording's start state (see <see cref="StartLayers"/>): creates the layers it lacks, makes the start CLAYER current, and removes
/// unused layers that the start state did not have (the template may hold more than the recording started with). Only for a drawing Retrace made for the test
/// (RETRACERUN / suite / Tdoc: a new drawing from the template, or under SDI = 1 Retrace's own temp copy); the caller checks that. The work is not on the drawing's undo
/// stack, so a recorded UNDO cannot undo it.
/// </summary>
static class StartSetup
{
    public static StartSetupResult Apply(Document doc, DrawingDigest initial, string sessionPath)
    {
        var res = new StartSetupResult();
        var db = doc.Database;
        var undoOff = false;
        try
        {
            using var dl = string.IsNullOrEmpty(doc.CommandInProgress) ? doc.LockDocument() : null;
            // look first: names from the digest decide whether start.json is read at all
            var present = new List<string>();
            string current;
            using (var rt = db.TransactionManager.StartTransaction())
            {
                var rlt = (LayerTable)rt.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId id in rlt) present.Add(((LayerTableRecord)rt.GetObject(id, OpenMode.ForRead)).Name);
                current = ((LayerTableRecord)rt.GetObject(db.Clayer, OpenMode.ForRead)).Name;
            }
            var snapFolder = BehaviorSnapshot.StartPath(Path.GetDirectoryName(sessionPath) ?? "");
            var needSnap = StartLayers.AnyMissing(initial, present) || string.IsNullOrWhiteSpace(initial.CurrentLayer);
            var snap = needSnap ? BehaviorSnapshot.Read(snapFolder) : null;
            var extras = StartLayers.ExtraLayers(initial, present).Where(n => !n.Equals(current, StringComparison.OrdinalIgnoreCase)).ToList();
            StartLayerPlan plan;
            using (var rt = db.TransactionManager.StartTransaction())
            {
                var ltt = (LinetypeTable)rt.GetObject(db.LinetypeTableId, OpenMode.ForRead);
                plan = StartLayers.Plan(initial, snap, present, ltt.Has);
            }
            res.Warnings.AddRange(plan.Warnings);
            var wantCurrent = plan.CurrentLayer is { } w && !w.Equals(current, StringComparison.OrdinalIgnoreCase) ? w : null;
            if (plan.IsEmpty && extras.Count == 0 && wantCurrent == null) return res;

            db.DisableUndoRecording(true); undoOff = true;
            using var tr = db.TransactionManager.StartTransaction();
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            var ltt2 = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
            if (!plan.IsEmpty) lt.UpgradeOpen();
            foreach (var l in plan.Create) CreateLayer(tr, lt, ltt2, l, res);
            if (wantCurrent != null)
            {
                try
                {
                    if (lt.Has(wantCurrent)) db.Clayer = lt[wantCurrent];
                    else res.Warnings.Add($"the start layer {wantCurrent} is not in the drawing and could not be made current");
                }
                catch (Exception ex) { res.Warnings.Add($"layer {wantCurrent} could not be made current: {ex.Message}"); }
            }
            RemoveExtras(db, tr, lt, extras, res);
            tr.Commit();
        }
        catch (Exception ex)
        {
            Log.Error("StartSetup", ex);
            res.Warnings.Add("the start drawing's layers could not be set up: " + ex.Message);
            res.Created.Clear(); res.Removed.Clear();   // the transaction was not committed
        }
        finally
        {
            if (undoOff) { try { db.DisableUndoRecording(false); } catch (Exception ex) { Log.Error("StartSetup undo", ex); } }
        }
        return res;
    }

    static void CreateLayer(Transaction tr, LayerTable lt, LinetypeTable ltt, LayerToCreate l, StartSetupResult res)
    {
        var rec = new LayerTableRecord { Name = l.Name };
        var added = false;
        try
        {
            // an unusable property keeps AutoCAD's default and the layer is still created
            if (l.Color != null)
            {
                if (ToColor(l.Color) is { } c) rec.Color = c; else res.Warnings.Add($"layer {l.Name}: color {l.Color} not usable, default used");
            }
            if (l.Linetype != null) rec.LinetypeObjectId = ltt[l.Linetype];
            if (l.Lineweight is { } w) { try { rec.LineWeight = (LineWeight)w; } catch (Exception) { res.Warnings.Add($"layer {l.Name}: lineweight {w} not usable, default used"); } }
            if (l.Plot is { } p) rec.IsPlottable = p;
            if (l.On is false) rec.IsOff = true;
            if (l.Locked is true) rec.IsLocked = true;
            lt.Add(rec);
            added = true;
        }
        catch (Exception ex)
        {
            Log.Error("StartSetup layer " + l.Name, ex);
            res.Warnings.Add($"layer {l.Name} could not be created: {ex.Message}");
            if (!added) rec.Dispose();   // never added to the table: nothing owns it
            return;
        }
        // added to the table: it must join the transaction at once (an error here aborts the whole setup, in Apply's catch)
        tr.AddNewlyCreatedDBObject(rec, true);
        res.Created.Add(l.Name);
        // these need the database (extension data, frozen): after Add; this layer is never the current one
        void Set(string what, Action a)
        {
            try { a(); }
            catch (Exception ex) { Log.Error($"StartSetup layer {l.Name} {what}", ex); res.Warnings.Add($"layer {l.Name} created, but its {what} could not be set: {ex.Message}"); }
        }
        if (l.Frozen is true) Set("frozen state", () => rec.IsFrozen = true);
        if (l.Transparency is { } a) Set("transparency", () => rec.Transparency = new Transparency((byte)a));
        if (l.Description != null) Set("description", () => rec.Description = l.Description);
    }

    /// <summary>Erases the layers the start state did not have, those a purge says nothing uses (and not the current one). The rest are reported.</summary>
    static void RemoveExtras(Database db, Transaction tr, LayerTable lt, List<string> extras, StartSetupResult res)
    {
        if (extras.Count == 0) return;
        var ids = new ObjectIdCollection();
        var byId = new Dictionary<ObjectId, string>();
        foreach (var n in extras)
        {
            try { if (lt.Has(n)) { var id = lt[n]; ids.Add(id); byId[id] = n; } }
            catch (Exception) { /* not there */ }
        }
        try { db.Purge(ids); }   // leaves in the collection only what nothing uses
        catch (Exception ex)
        {
            Log.Error("StartSetup purge", ex);
            res.Warnings.Add("layers the recording's start state did not have could not be checked for use: " + ex.Message);
            res.Kept.AddRange(extras);
            return;
        }
        var erased = new HashSet<ObjectId>();
        foreach (ObjectId id in ids)
        {
            try { ((LayerTableRecord)tr.GetObject(id, OpenMode.ForWrite)).Erase(); erased.Add(id); res.Removed.Add(byId[id]); }
            catch (Exception ex) { Log.Error("StartSetup erase " + byId[id], ex); }
        }
        foreach (var (id, n) in byId) if (!erased.Contains(id)) res.Kept.Add(n);
        if (res.Kept.Count > 0)
            res.Warnings.Add($"layer{(res.Kept.Count == 1 ? "" : "s")} {string.Join(", ", res.Kept.Take(8))}{(res.Kept.Count > 8 ? ", ..." : "")} {(res.Kept.Count == 1 ? "is" : "are")} not in the recording's start state but could not be removed (in use); kept");
    }

    static Color? ToColor(string text)
    {
        var s = StartLayers.ParseColor(text);
        if (s == null) return null;
        try
        {
            return s.Kind switch
            {
                "aci" => Color.FromColorIndex(ColorMethod.ByAci, (short)s.Index),
                "rgb" => Color.FromRgb(s.R, s.G, s.B),
                _ => Color.FromNames(s.Name, s.Book),
            };
        }
        catch (Exception) { return null; }
    }
}
