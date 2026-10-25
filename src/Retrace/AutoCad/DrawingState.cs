using Autodesk.AutoCAD.ApplicationServices;
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
    public static DrawingDigest? Read(Document doc)
    {
        try
        {
            DocumentLock? dl = string.IsNullOrEmpty(doc.CommandInProgress) ? doc.LockDocument() : null;
            try { return ReadLocked(doc.Database); }
            finally { dl?.Dispose(); }
        }
        catch (Exception ex) { Log.Error("DrawingState.Read", ex); return null; }
    }

    static DrawingDigest ReadLocked(Database db)
    {
        var ents = new List<(string, string, string)>();
        var layers = new List<string>();
        using var tr = db.TransactionManager.StartOpenCloseTransaction();

        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        foreach (ObjectId id in lt)
            if (tr.GetObject(id, OpenMode.ForRead) is LayerTableRecord ltr) layers.Add(ltr.Name);

        var keys = new Keys(tr);
        var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
        foreach (ObjectId id in ms)
            if (tr.GetObject(id, OpenMode.ForRead) is Entity ent) ents.Add(keys.Of(ent));
        tr.Commit();
        return DrawingDigest.Build(ents, layers);
    }

    static string P(Point3d p) => DrawingDigest.Pt(p.X, p.Y, p.Z);
    static string N(double d) => DrawingDigest.Num(d);

    /// <summary>Entity keys inside one transaction. Caches the content hash of each block definition.</summary>
    sealed class Keys(Transaction tr)
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

        /// <summary>Block name (dynamic blocks: the definition's), a hash of what the definition (for a dynamic block, its current
        /// anonymous one) contains, dynamic property values, and each attribute's value, placement and visibility.</summary>
        string InsertContent(BlockReference b)
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
        string Def(ObjectId btrId)
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
