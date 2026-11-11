using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Retrace.AutoCad;

/// <summary>
/// Reads the active drawing into a <see cref="DrawingDigest"/> (model space + layer table), read-only.
/// Call it only from command context or a timer/idle callback, NEVER from a prompt or other event handler.
/// </summary>
static class DrawingState
{
    /// <summary>Digest of the document, or null if it cannot be read (the reason goes to retrace.log).
    /// Takes a DocumentLock unless a command is running (a command already holds one).</summary>
    /// <param name="withLayerState">false (recording, and replaying a mark with checks): no layer state or current layer in the digest, the `layers`
    /// and `sysvars` behaviors own them. true only to replay an older mark that has no checks but recorded layer state.</param>
    public static DrawingDigest? Read(Document doc, bool withLayerState = false)
    {
        try
        {
            DocumentLock? dl = string.IsNullOrEmpty(doc.CommandInProgress) ? doc.LockDocument() : null;
            try { return ReadLocked(doc.Database, withLayerState); }
            finally { dl?.Dispose(); }
        }
        catch (Exception ex) { Log.Error("DrawingState.Read", ex); return null; }
    }

    /// <summary>The basic check's counts of the document (see <see cref="BasicCheckRules"/>): model-space entities, layers (not the xref-dependent ones, "X|Y"),
    /// paper-space layouts (layout tabs, not Model). A count that cannot be read is null. Same locking rules as <see cref="Read"/>. Null when nothing could be read.</summary>
    public static BasicCheckRules.Counts? ReadCounts(Document doc)
    {
        try
        {
            DocumentLock? dl = string.IsNullOrEmpty(doc.CommandInProgress) ? doc.LockDocument() : null;
            try
            {
                var db = doc.Database;
                int? ms = null, layers = null, layouts = null;
                using var tr = db.TransactionManager.StartOpenCloseTransaction();
                try
                {
                    var btr = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                    int n = 0;
                    foreach (ObjectId _ in btr) n++;   // ids only: no object is opened
                    ms = n;
                }
                catch (Exception ex) { Log.Error("DrawingState.ReadCounts model space", ex); }
                try
                {
                    var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    int n = 0;
                    foreach (ObjectId id in lt) if (tr.GetObject(id, OpenMode.ForRead) is LayerTableRecord ltr && !ltr.IsDependent && !BasicCheckRules.IsXrefLayer(ltr.Name)) n++;
                    layers = n;
                }
                catch (Exception ex) { Log.Error("DrawingState.ReadCounts layers", ex); }
                try
                {
                    var ld = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                    int n = 0;
                    foreach (DBDictionaryEntry e in ld) if (!string.Equals(e.Key, "Model", StringComparison.OrdinalIgnoreCase)) n++;
                    layouts = n;
                }
                catch (Exception ex) { Log.Error("DrawingState.ReadCounts layouts", ex); }
                tr.Commit();
                return ms == null && layers == null && layouts == null ? null : new BasicCheckRules.Counts(ms, layers, layouts);
            }
            finally { dl?.Dispose(); }
        }
        catch (Exception ex) { Log.Error("DrawingState.ReadCounts", ex); return null; }
    }

    static DrawingDigest ReadLocked(Database db, bool withLayerState)
    {
        var ents = new List<(string, string, string)>();
        var layers = new List<string>();
        var layerState = new Dictionary<string, LayerProps>(StringComparer.Ordinal);
        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        var layerReader = new LayerReader(tr);

        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        foreach (ObjectId id in lt)
            if (tr.GetObject(id, OpenMode.ForRead) is LayerTableRecord ltr)
            {
                layers.Add(ltr.Name);
                if (withLayerState && !ltr.IsDependent) layerState[ltr.Name] = layerReader.Of(ltr);   // xref layers change outside the test: not in layerState
            }

        var keys = new Keys(tr);
        var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
        foreach (ObjectId id in ms)
            if (tr.GetObject(id, OpenMode.ForRead) is Entity ent) ents.Add(keys.Of(ent));
        string? current = null;
        if (withLayerState) try
        {
            if (tr.GetObject(db.Clayer, OpenMode.ForRead) is LayerTableRecord cur) current = cur.Name;
        }
        catch { /* leave currentLayer out */ }
        tr.Commit();
        return withLayerState ? DrawingDigest.Build(ents, layers, layerState, current) : DrawingDigest.Build(ents, layers);
    }

    /// <summary>Reads layer properties inside one transaction. A property that throws is left out (null) and never fails the digest;
    /// the first failure of a digest read goes to retrace.log. Linetype names are cached by id (one open per linetype).</summary>
    internal sealed class LayerReader(Transaction tr)
    {
        readonly Dictionary<ObjectId, string> linetypes = new();
        bool logged;

        /// <summary>A linetype's name, cached by id (one open per linetype). A null id (a proxy entity, an entity with no linetype record) is "",
        /// as <c>BehaviorCapture.NameOf</c> does, so one such entity cannot make the whole `appearance` behavior unreadable.
        /// (AutoCAD objects: not reachable from the unit tests.)</summary>
        public string LinetypeName(ObjectId id)
        {
            if (id.IsNull) return "";
            if (!linetypes.TryGetValue(id, out var name))
                linetypes[id] = name = ((LinetypeTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name;
            return name;
        }

        void Fail(string what, Exception ex)
        {
            if (logged) return;
            logged = true;
            Log.Error("DrawingState layer " + what + " (first failure of this read; layer state may be incomplete)", ex);
        }

        public LayerProps Of(LayerTableRecord ltr)
        {
            var p = new LayerProps();
            try { p.On = !ltr.IsOff; } catch (Exception ex) { Fail("on", ex); }
            try { p.Frozen = ltr.IsFrozen; } catch (Exception ex) { Fail("frozen", ex); }
            try { p.Locked = ltr.IsLocked; } catch (Exception ex) { Fail("locked", ex); }
            try { p.Plot = ltr.IsPlottable; } catch (Exception ex) { Fail("plot", ex); }
            try
            {
                p.Color = ColorText(ltr.Color);
            }
            catch (Exception ex) { Fail("color", ex); }
            try
            {
                p.Linetype = LinetypeName(ltr.LinetypeObjectId);
            }
            catch (Exception ex) { Fail("linetype", ex); }
            try { p.Lineweight = DrawingDigest.LineweightText((int)ltr.LineWeight); } catch (Exception ex) { Fail("lineweight", ex); }
            return p;
        }
    }

    internal static string ColorText(Color c) => c.ColorMethod switch
    {
        ColorMethod.ByColor when !string.IsNullOrEmpty(c.BookName) => $"{c.BookName}${c.ColorName}",
        ColorMethod.ByColor => $"rgb:{c.Red},{c.Green},{c.Blue}",
        _ => c.ColorIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    internal static string P(Point3d p) => DrawingDigest.Pt(p.X, p.Y, p.Z);
    internal static string N(double d) => DrawingDigest.Num(d);

    /// <summary>Entity keys inside one transaction. Caches the content hash of each block definition.</summary>
    internal sealed class Keys(Transaction tr)
    {
        readonly Dictionary<ObjectId, string> defs = new();

        public (string Dxf, string Layer, string Key) Of(Entity ent)
        {
            var dxf = ent.GetRXClass().DxfName ?? ent.GetType().Name;
            var layer = ent.Layer;
            if (Placed(ent, dxf, layer) is { } placed) return (dxf, layer, placed);
            (double, double, double, double, double, double)? ext = null;
            try
            {
                var e = ent.GeometricExtents;
                ext = (e.MinPoint.X, e.MinPoint.Y, e.MinPoint.Z, e.MaxPoint.X, e.MaxPoint.Y, e.MaxPoint.Z);
            }
            catch { /* no extents (xline, ...) */ }
            return (dxf, layer, DrawingDigest.Key(dxf, layer, ext, Extra(ent)));
        }

        /// <summary>Key of an entity whose extents include rendered text (TEXT, ATTDEF, MTEXT, INSERT, DIMENSION and its subtypes) from
        /// what defines it instead (see docs/recording-format.md). Extents depend on how AutoCAD renders the font, and the same TEXT measured
        /// differently from one day to the next on the same PC. Hashing the entity's saved data (DwgOutFields) would not help: it holds
        /// font-derived values too (a justified TEXT's Position, MTEXT's actual size, a dimension's text position and block).
        /// Null for other entities and if a property throws (the entity then falls back to its extents).
        /// Not covered (still extents): tables, multileaders and other text-bearing entities.</summary>
        string? Placed(Entity ent, string dxf, string layer)
        {
            try
            {
                switch (ent)
                {
                    case AttributeDefinition a when a.IsMTextAttributeDefinition:
                    {
                        using var mt = a.MTextAttributeDefinition;   // a new MText each read: dispose it on this thread
                        return DrawingDigest.PlacedKey(dxf, layer, MTextPlacement(mt) + Inv(a.Invisible), $"tag={a.Tag} prompt={a.Prompt} default={mt.Contents}");
                    }
                    case AttributeDefinition a:
                        // an ATTDEF shows its tag in model space; the default value and prompt only matter on insert
                        return DrawingDigest.PlacedKey(dxf, layer, TextPlacement(a) + Inv(a.Invisible), $"tag={a.Tag} prompt={a.Prompt} default={a.TextString}");
                    case DBText t:
                        return DrawingDigest.PlacedKey(dxf, layer, TextPlacement(t), "text=" + t.TextString);
                    case MText m:
                        return DrawingDigest.PlacedKey(dxf, layer, MTextPlacement(m), "text=" + m.Contents);
                    case Table:
                        return null;   // a BlockReference too, but its extents are its cells
                    case BlockReference b:
                        return DrawingDigest.PlacedKey(dxf, layer, InsertPlacement(b), InsertContent(b));
                    case Dimension d:
                        return DrawingDigest.PlacedKey(dxf, layer, DimPlacement(d), "text=" + d.DimensionText);
                }
            }
            catch { /* a property that throws (proxy, degenerate): use the extents */ }
            return null;
        }

        /// <summary>Entity id key for behavior items: the digest key, but an INSERT without its attribute values (so editing an attribute
        /// does not rename the insert).</summary>
        public string IdKey(Entity ent)
        {
            if (ent is BlockReference b && ent is not Table)
            {
                try { return DrawingDigest.PlacedKey("INSERT", ent.Layer, InsertPlacement(b), InsertContent(b, false)); }
                catch { /* fall back to the digest key */ }
            }
            return Of(ent).Key;
        }

        /// <summary>Block name (dynamic blocks: the definition's), a hash of what the definition (for a dynamic block, its current
        /// anonymous one) contains, dynamic property values, and (when <paramref name="withAttributes"/>) each attribute's value, placement and visibility.</summary>
        string InsertContent(BlockReference b, bool withAttributes = true)
        {
            var named = (BlockTableRecord)tr.GetObject(b.IsDynamicBlock ? b.DynamicBlockTableRecord : b.BlockTableRecord, OpenMode.ForRead);
            var s = $"block={named.Name} def={Def(b.BlockTableRecord)}";
            if (b.IsDynamicBlock)
            {
                var props = new List<string>();
                foreach (DynamicBlockReferenceProperty p in b.DynamicBlockReferencePropertyCollection)
                    props.Add($"{p.PropertyName}={p.Value switch { double d => N(d), Point3d pt => P(pt), var v => v?.ToString() }}");
                props.Sort(StringComparer.Ordinal);
                s += " dyn=" + string.Join(";", props);
            }
            if (!withAttributes) return s;
            var attrs = new List<string>();
            foreach (ObjectId aid in b.AttributeCollection)
            {
                if (tr.GetObject(aid, OpenMode.ForRead) is not AttributeReference ar) continue;
                if (ar.IsMTextAttribute)
                {
                    using var mt = ar.MTextAttribute;   // a new MText each read: dispose it on this thread
                    attrs.Add($"{ar.Tag}={mt.Contents}@{MTextPlacement(mt)}{Inv(ar.Invisible)}");
                }
                else attrs.Add($"{ar.Tag}={ar.TextString}@{TextPlacement(ar)}{Inv(ar.Invisible)}");
            }
            attrs.Sort(StringComparer.Ordinal);
            return attrs.Count > 0 ? s + " attrs=" + string.Join(";", attrs) : s;
        }

        /// <summary>Short hash of the keys of a block definition's entities (same rules, recursive, once per definition);
        /// "xref" for external references, which can be large and change outside the test.</summary>
        public string Def(ObjectId btrId)
        {
            if (defs.TryGetValue(btrId, out var known)) return known;
            defs[btrId] = "cycle";   // a block that (wrongly) contains itself
            var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
            string hash;
            if (btr.IsFromExternalReference || btr.IsDependent) hash = "xref";
            else
            {
                var keys = new List<string>();
                foreach (ObjectId id in btr)
                    if (tr.GetObject(id, OpenMode.ForRead) is Entity e) keys.Add(Of(e).Key);
                hash = DrawingDigest.ShortHash(keys);
            }
            return defs[btrId] = hash;
        }
    }

    /// <summary>Left text is placed by Position; Aligned and Fit by both baseline points (Aligned's height and Fit's width factor come
    /// from the font, so they are left out); every other justification by AlignmentPoint (Position then comes from the font).</summary>
    static string TextPlacement(DBText t)
    {
        var place = t.Justify switch
        {
            AttachmentPoint.BaseLeft => $"at={P(t.Position)} h={N(t.Height)} wf={N(t.WidthFactor)}",
            AttachmentPoint.BaseAlign => $"at={P(t.Position)} to={P(t.AlignmentPoint)} wf={N(t.WidthFactor)}",
            AttachmentPoint.BaseFit => $"at={P(t.Position)} to={P(t.AlignmentPoint)} h={N(t.Height)}",
            _ => $"at={P(t.AlignmentPoint)} h={N(t.Height)} wf={N(t.WidthFactor)}",
        };
        return place + $" rot={N(t.Rotation)} just={t.Justify} obl={N(t.Oblique)} mirror={(t.IsMirroredInX ? "x" : "")}{(t.IsMirroredInY ? "y" : "")} style={t.TextStyleName}";
    }

    static string MTextPlacement(MText m) =>
        $"at={P(m.Location)} h={N(m.TextHeight)} rot={N(m.Rotation)} attach={m.Attachment} w={N(m.Width)} style={m.TextStyleName}";

    static string Inv(bool invisible) => invisible ? " invisible" : "";

    static string InsertPlacement(BlockReference b)
    {
        var s = b.ScaleFactors;
        var place = $"at={P(b.Position)} scale={N(s.X)},{N(s.Y)},{N(s.Z)} rot={N(b.Rotation)}";
        if (b is MInsertBlock mi) place += $" array={mi.Rows}x{mi.Columns} spacing={N(mi.RowSpacing)},{N(mi.ColumnSpacing)}";
        return place;
    }

    /// <summary>The points that define the dimension, the text position only when it was placed by hand (otherwise it comes from the
    /// font), the measurement and the style.</summary>
    static string DimPlacement(Dimension d)
    {
        var pts = d switch
        {
            RotatedDimension r => $"p1={P(r.XLine1Point)} p2={P(r.XLine2Point)} line={P(r.DimLinePoint)} rot={N(r.Rotation)} ",
            AlignedDimension al => $"p1={P(al.XLine1Point)} p2={P(al.XLine2Point)} line={P(al.DimLinePoint)} ",
            RadialDimensionLarge rl => $"center={P(rl.Center)} chord={P(rl.ChordPoint)} override={P(rl.OverrideCenter)} jog={P(rl.JogPoint)} ",
            RadialDimension rd => $"center={P(rd.Center)} chord={P(rd.ChordPoint)} ",
            DiametricDimension dd => $"chord={P(dd.ChordPoint)} far={P(dd.FarChordPoint)} ",
            LineAngularDimension2 la => $"l1={P(la.XLine1Start)}~{P(la.XLine1End)} l2={P(la.XLine2Start)}~{P(la.XLine2End)} arc={P(la.ArcPoint)} ",
            Point3AngularDimension pa => $"center={P(pa.CenterPoint)} p1={P(pa.XLine1Point)} p2={P(pa.XLine2Point)} arc={P(pa.ArcPoint)} ",
            ArcDimension ad => $"center={P(ad.CenterPoint)} p1={P(ad.XLine1Point)} p2={P(ad.XLine2Point)} arc={P(ad.ArcPoint)} ",
            OrdinateDimension od => $"origin={P(od.Origin)} def={P(od.DefiningPoint)} leader={P(od.LeaderEndPoint)} x={od.UsingXAxis} ",
            _ => "",
        };
        if (!d.UsingDefaultTextPosition) pts += $"textat={P(d.TextPosition)} ";
        return $"{pts}measure={N(d.Measurement)} style={d.DimensionStyleName}";
    }

    /// <summary>Length of a curve, or the block name of a table, where cheap.</summary>
    static string? Extra(Entity ent)
    {
        try
        {
            switch (ent)
            {
                case BlockReference b: return "block=" + b.Name;
                case Curve c:
                    return "len=" + DrawingDigest.Num(c.GetDistanceAtParameter(c.EndParam) - c.GetDistanceAtParameter(c.StartParam));
            }
        }
        catch { /* infinite or degenerate curve */ }
        return null;
    }
}
