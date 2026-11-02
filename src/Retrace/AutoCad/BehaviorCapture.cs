using System.Diagnostics;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Retrace.AutoCad;

/// <summary>
/// Reads a <see cref="BehaviorSnapshot"/> of the active drawing (docs/recording-format.md, "Behavior checks"), read-only.
/// Call it only from command context or a timer/idle callback, NEVER from an event handler.
/// Each behavior has its own try/catch: one that throws is left out of the snapshot ("could not be read") and logged once per session of
/// AutoCAD; it never fails the whole read. A behavior that takes more than 200 ms is logged with its time.
/// Everything that can be tested without AutoCAD is in <see cref="BehaviorKeys"/>.
/// </summary>
static partial class BehaviorCapture
{
    static readonly HashSet<string> Logged = new(StringComparer.Ordinal);

    static string N(double d) => DrawingDigest.Num(d);
    static string P(Point3d p) => DrawingState.P(p);
    static string P(Vector3d v) => DrawingDigest.Pt(v.X, v.Y, v.Z);
    static string B(bool b) => BehaviorKeys.Bool(b);

    /// <summary>The snapshot of the document; null only when the document cannot be locked / opened at all.
    /// Takes a DocumentLock unless a command is running (a command already holds one).</summary>
    /// <param name="only">behavior ids to read (replay: the ones the checks need); null = all</param>
    public static BehaviorSnapshot? Read(Document doc, RetraceSettings settings, IReadOnlyCollection<string>? only = null)
    {
        try
        {
            DocumentLock? dl = string.IsNullOrEmpty(doc.CommandInProgress) ? doc.LockDocument() : null;
            try { return ReadLocked(doc.Database, settings, only); }
            finally { dl?.Dispose(); }
        }
        catch (Exception ex) { Log.Error("BehaviorCapture.Read", ex); return null; }
    }

    static BehaviorSnapshot ReadLocked(Database db, RetraceSettings settings, IReadOnlyCollection<string>? only)
    {
        var snap = new BehaviorSnapshot();
        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        var keys = new DrawingState.Keys(tr);
        bool Want(string id) => only == null || only.Contains(id);
        // layouts, viewports, psattributes, xdata and xdicts share ONE pass over model space and each paper-space layout
        var scanTimer = Stopwatch.StartNew();
        var scan = new Lazy<SpaceScan>(() =>
        {
            var sc = ScanSpaces(db, tr, keys, Want(BehaviorCatalog.Layouts), Want(BehaviorCatalog.Viewports), Want(BehaviorCatalog.PsAttributes),
                Want(BehaviorCatalog.XData), Want(BehaviorCatalog.XDicts));
            if (scanTimer.ElapsedMilliseconds > 200) Log.Info($"BehaviorCapture entity pass (layouts, viewports, psattributes, xdata, xdicts): {scanTimer.ElapsedMilliseconds} ms");
            return sc;
        });

        void Run(string id, Func<IEnumerable<KeyValuePair<string, string>>> read)
        {
            if (!Want(id)) return;
            var sw = Stopwatch.StartNew();
            try
            {
                var items = read().ToList();
                if (scan.IsValueCreated && scan.Value.Failed.TryGetValue(id, out var failure)) throw failure;   // a part of the shared pass threw
                snap.Set(id, items);
            }
            catch (Exception ex)
            {
                if (Logged.Add(id)) Log.Error($"BehaviorCapture {id} (left out of the snapshot; logged once)", ex);
            }
            if (id is not (BehaviorCatalog.Layouts or BehaviorCatalog.Viewports or BehaviorCatalog.PsAttributes or BehaviorCatalog.XData or BehaviorCatalog.XDicts)
                && sw.ElapsedMilliseconds > 200) Log.Info($"BehaviorCapture {id}: {sw.ElapsedMilliseconds} ms");
        }

        Run(BehaviorCatalog.Layers, () => Layers(db, tr));
        Run(BehaviorCatalog.Layouts, () => scan.Value.LayoutItems());
        Run(BehaviorCatalog.Viewports, () => scan.Value.ViewportItems(tr));
        Run(BehaviorCatalog.XData, () => scan.Value.XData);
        Run(BehaviorCatalog.XDicts, () => XDicts(db, tr, scan.Value));
        Run(BehaviorCatalog.Symbols, () => Symbols(db, tr, keys));
        if (Want(BehaviorCatalog.SysVars)) Run(BehaviorCatalog.SysVars, () => SysVars(settings.EffectiveSysvars()));
        Run(BehaviorCatalog.Views, () => Views(db, tr));
        Run(BehaviorCatalog.PsAttributes, () => scan.Value.PsAttributeItems());
        tr.Commit();
        return snap;
    }

    static T Open<T>(Transaction tr, ObjectId id) where T : DBObject => (T)tr.GetObject(id, OpenMode.ForRead);

    static string NameOf<T>(Transaction tr, ObjectId id, Func<T, string> name) where T : DBObject =>
        id.IsNull ? "" : name(Open<T>(tr, id));

    // ---- layers ----

    static IEnumerable<KeyValuePair<string, string>> Layers(Database db, Transaction tr)
    {
        foreach (ObjectId id in Open<LayerTable>(tr, db.LayerTableId))
        {
            var l = Open<LayerTableRecord>(tr, id);
            if (l.IsDependent) continue;   // xref layers change outside the test
            var tp = l.Transparency;
            yield return new(l.Name, CanonicalValue.Join(
                ("on", B(!l.IsOff)), ("frozen", B(l.IsFrozen)), ("locked", B(l.IsLocked)), ("plot", B(l.IsPlottable)),
                ("color", DrawingState.ColorText(l.Color)),
                ("linetype", NameOf<LinetypeTableRecord>(tr, l.LinetypeObjectId, t => t.Name)),
                ("lineweight", DrawingDigest.LineweightText((int)l.LineWeight)),
                ("transparency", tp.IsByAlpha ? tp.Alpha.ToString(System.Globalization.CultureInfo.InvariantCulture) : tp.IsByBlock ? "ByBlock" : "ByLayer"),
                ("description", l.Description)));
        }
    }

    // ---- layouts (the data is read in the shared pass, BehaviorCapture.Entities.cs) ----

    /// <summary>(name, Layout, tab order) of the paper-space layouts (not Model), in tab order.</summary>
    static List<Layout> PaperLayouts(Database db, Transaction tr)
    {
        var list = new List<Layout>();
        foreach (DBDictionaryEntry e in Open<DBDictionary>(tr, db.LayoutDictionaryId))
        {
            var lay = Open<Layout>(tr, e.Value);
            if (!lay.ModelType) list.Add(lay);
        }
        return list.OrderBy(l => l.TabOrder).ThenBy(l => l.LayoutName, StringComparer.Ordinal).ToList();
    }

    // ---- symbols ----

    static IEnumerable<KeyValuePair<string, string>> Symbols(Database db, Transaction tr, DrawingState.Keys keys)
    {
        foreach (ObjectId id in Open<LinetypeTable>(tr, db.LinetypeTableId))
        {
            var r = Open<LinetypeTableRecord>(tr, id);
            if (r.IsDependent) continue;
            yield return new("LTYPE:" + r.Name, CanonicalValue.Join(("description", r.Comments), ("length", N(r.PatternLength)),
                ("dashes", r.NumDashes.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        }
        foreach (ObjectId id in Open<TextStyleTable>(tr, db.TextStyleTableId))
        {
            var r = Open<TextStyleTableRecord>(tr, id);
            if (r.IsDependent) continue;
            string face;
            try { face = r.Font.TypeFace; } catch { face = ""; }
            yield return new("STYLE:" + r.Name, CanonicalValue.Join(("font", r.FileName), ("bigfont", r.BigFontFileName), ("typeface", face),
                ("height", N(r.TextSize)), ("width", N(r.XScale)), ("oblique", N(r.ObliquingAngle))));
        }
        foreach (ObjectId id in Open<DimStyleTable>(tr, db.DimStyleTableId))
        {
            var r = Open<DimStyleTableRecord>(tr, id);
            if (r.IsDependent) continue;
            yield return new("DIMSTYLE:" + r.Name, CanonicalValue.Join(
                ("dimscale", N(r.Dimscale)), ("dimlfac", N(r.Dimlfac)), ("dimtxt", N(r.Dimtxt)), ("dimasz", N(r.Dimasz)),
                ("dimexe", N(r.Dimexe)), ("dimexo", N(r.Dimexo)), ("dimgap", N(r.Dimgap)), ("dimdec", r.Dimdec.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("dimlunit", r.Dimlunit.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("dimaunit", r.Dimaunit.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("dimtad", r.Dimtad.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("dimclrd", DrawingState.ColorText(r.Dimclrd)), ("dimclre", DrawingState.ColorText(r.Dimclre)), ("dimclrt", DrawingState.ColorText(r.Dimclrt)),
                ("dimtxsty", NameOf<TextStyleTableRecord>(tr, r.Dimtxsty, t => t.Name))));
        }
        foreach (ObjectId id in Open<BlockTable>(tr, db.BlockTableId))
        {
            var r = Open<BlockTableRecord>(tr, id);
            if (r.IsLayout || r.IsAnonymous || r.IsDependent || r.Name.StartsWith('*')) continue;
            var xref = r.IsFromExternalReference;
            var count = 0;
            if (!xref) foreach (ObjectId _ in r) count++;   // ids only: no entity is opened for the count
            yield return new("BLOCK:" + r.Name, CanonicalValue.Join(("origin", P(r.Origin)),
                ("count", xref ? "" : count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("hash", xref ? "" : keys.Def(id)),
                ("xref", B(xref)), ("path", xref ? r.PathName : "")));
        }
        foreach (ObjectId id in Open<UcsTable>(tr, db.UcsTableId))
        {
            var r = Open<UcsTableRecord>(tr, id);
            yield return new("UCS:" + r.Name, CanonicalValue.Join(("origin", P(r.Origin)), ("xaxis", P(r.XAxis)), ("yaxis", P(r.YAxis))));
        }
        foreach (ObjectId id in Open<RegAppTable>(tr, db.RegAppTableId))
        {
            var r = Open<RegAppTableRecord>(tr, id);
            if (r.IsDependent) continue;
            yield return new("APPID:" + r.Name, "");
        }
    }

    // ---- sysvars ----

    static readonly HashSet<string> UnknownVars = new(StringComparer.OrdinalIgnoreCase);

    static IEnumerable<KeyValuePair<string, string>> SysVars(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            object? v;
            try { v = AcApp.GetSystemVariable(name); }
            catch (Exception ex)
            {
                if (UnknownVars.Add(name)) Log.Info($"BehaviorCapture sysvars: {name} skipped ({ex.GetType().Name}: not a variable of this AutoCAD)");
                continue;
            }
            yield return new(name.ToUpperInvariant(), v switch
            {
                Point3d p => P(p),
                Point2d p => BehaviorKeys.Pt2(p.X, p.Y),
                double d => N(d),
                null => "",
                var o => Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture) ?? "",
            });
        }
    }

    // ---- views ----

    static IEnumerable<KeyValuePair<string, string>> Views(Database db, Transaction tr)
    {
        foreach (ObjectId id in Open<ViewTable>(tr, db.ViewTableId))
        {
            var v = Open<ViewTableRecord>(tr, id);
            if (v.IsDependent) continue;
            yield return new(v.Name, CanonicalValue.Join(
                ("center", BehaviorKeys.Pt2(v.CenterPoint.X, v.CenterPoint.Y)), ("height", N(v.Height)), ("width", N(v.Width)),
                ("direction", P(v.ViewDirection)), ("target", P(v.Target)), ("ps", B(v.IsPaperspaceView)),
                ("layout", v.IsPaperspaceView ? NameOf<Layout>(tr, v.Layout, l => l.LayoutName) : ""), ("category", v.CategoryName)));
        }
    }
}
