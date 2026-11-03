using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Retrace.AutoCad;

// The behaviors that look at individual entities (layouts, viewports, psattributes, xdata, xdicts): one pass per space.

static partial class BehaviorCapture
{
    /// <summary>One entity that has xdata and / or an extension dictionary, with what it holds, before ids are numbered.</summary>
    sealed class Owner
    {
        public string Key = "";
        public SortedDictionary<string, string> XData = new(StringComparer.Ordinal);   // regapp -> canonical groups
        public List<(string[] Path, string Value)> XDict = new();                      // entry path -> canonical entry
        public string Id = "";
    }

    /// <summary>What the shared pass collected. A part that throws is recorded in <see cref="Failed"/> by behavior id (that behavior is left out)
    /// and the others carry on.</summary>
    sealed class SpaceScan
    {
        public readonly Dictionary<string, Exception> Failed = new(StringComparer.Ordinal);
        public readonly List<Owner> Owners = new();
        public readonly List<LayoutData> Layouts = new();

        public IEnumerable<KeyValuePair<string, string>> XData =>
            Owners.SelectMany(o => o.XData.Select(x => new KeyValuePair<string, string>(BehaviorKeys.XDataKey(o.Id, x.Key), x.Value)));

        public IEnumerable<KeyValuePair<string, string>> LayoutItems()
        {
            // *Paper_Space is the block of the paper layout that is current, or was the last one before Model became current
            var paperSpace = Layouts.Count > 0 ? SymbolUtilityServices.GetBlockPaperSpaceId(Layouts[0].Layout.Database) : ObjectId.Null;
            foreach (var l in Layouts)
            {
                var lay = l.Layout;
                string scale;
                try { scale = lay.UseStandardScale ? lay.StdScaleType.ToString() : N(lay.CustomPrintScale.Numerator) + ":" + N(lay.CustomPrintScale.Denominator); }
                catch { scale = ""; }
                // The layout's own PSLTSCALE, from its saved flag: the system variable holds it only while the layout is current.
                // AutoCAD writes the variable into the flag when you move to another paper layout (or save), so the flag of the layout that
                // owns the live variable can lag behind a change made in it: that layout (the one whose block is the database's *Paper_Space)
                // reads the variable instead. Left out when entget is not available.
                var flags = DxfRead.ReadFlag(lay.ObjectId, DxfGroups.LayoutFlags);
                string? psltscale = flags is { } f0 ? B(lay.BlockTableRecordId == paperSpace ? lay.Database.Psltscale : DxfGroups.LayoutPsltscale(f0)) : null;
                var fields = new List<(string, string?)>
                {
                    ("tab", lay.TabOrder.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("count", l.Types.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("types", BehaviorKeys.TypeCounts(l.Types)),
                    ("hash", DrawingDigest.ShortHash(l.HashKeys)),
                    ("device", lay.PlotConfigurationName), ("media", lay.CanonicalMediaName), ("scale", scale),
                };
                if (psltscale != null) fields.Add(("psltscale", psltscale));
                yield return new(lay.LayoutName, CanonicalValue.Join(fields));
            }
        }

        public IEnumerable<KeyValuePair<string, string>> ViewportItems(Transaction tr)
        {
            foreach (var l in Layouts)
            {
                var n = 0;
                foreach (var vp in BehaviorKeys.ViewportOrder(l.Viewports))
                {
                    var frozen = new List<string>();
                    foreach (ObjectId lid in vp.GetFrozenLayers()) frozen.Add(Open<LayerTableRecord>(tr, lid).Name);
                    // off from the saved flags (group 90, bit "turn viewport off"): Viewport.On reads false until the layout has been current in
                    // this session. Left out when entget is not available.
                    var flags = DxfRead.ReadFlag(vp.ObjectId, DxfGroups.ViewportFlags);
                    var fields = new List<(string, string?)>
                    {
                        ("center", P(vp.CenterPoint)), ("width", N(vp.Width)), ("height", N(vp.Height)),
                        ("viewcenter", BehaviorKeys.Pt2(vp.ViewCenter.X, vp.ViewCenter.Y)), ("viewtarget", P(vp.ViewTarget)),
                        ("viewdirection", P(vp.ViewDirection)), ("customscale", N(vp.CustomScale)),
                    };
                    if (flags is { } f) fields.Add(("off", B(DxfGroups.ViewportOff(f))));
                    fields.Add(("locked", B(vp.Locked)));
                    fields.Add(("frozenlayers", BehaviorKeys.SortedList(frozen)));
                    yield return new(BehaviorKeys.ViewportKey(l.Layout.LayoutName, ++n), CanonicalValue.Join(fields));
                }
            }
        }

        public IEnumerable<KeyValuePair<string, string>> PsAttributeItems()
        {
            foreach (var l in Layouts)
            {
                var ids = BehaviorKeys.EntityIds(BehaviorKeys.PaperPrefix(l.Layout.LayoutName), l.Inserts.Select(i => i.Key).ToList());
                for (var i = 0; i < l.Inserts.Count; i++)
                {
                    var seen = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (var (tag, text) in l.Inserts[i].Attrs)
                    {
                        var n = seen[tag] = seen.GetValueOrDefault(tag) + 1;
                        yield return new(BehaviorKeys.AttributeKey(l.Layout.LayoutName, ids[i], tag, n), text);
                    }
                }
            }
        }
    }

    sealed class LayoutData(Layout layout)
    {
        public readonly Layout Layout = layout;
        public readonly List<string> Types = new();
        public readonly List<string> HashKeys = new();
        public readonly List<(double, double, Viewport)> Viewports = new();
        public readonly List<(string Key, List<(string Tag, string Text)> Attrs)> Inserts = new();
    }

    /// <summary>One pass over model space (xdata, xdicts) and each paper-space layout (everything), opening every entity once. Only entities that have
    /// xdata or an extension dictionary are keyed for xdata / xdicts, and only inserts with attributes for psattributes (ids are numbered among those,
    /// in the order of the block table record = creation order). The layout's overall viewport (the first id of
    /// `Layout.GetViewports()`, none on a layout never made current) is skipped by every behavior; if it cannot be found, layouts, viewports, xdata and xdicts fail.</summary>
    static SpaceScan ScanSpaces(Database db, Transaction tr, DrawingState.Keys keys, bool layouts, bool viewports, bool psAttributes, bool xdata, bool xdicts)
    {
        var scan = new SpaceScan();
        void Guard(string behavior, Action a)
        {
            try { a(); }
            catch (Exception ex) { scan.Failed.TryAdd(behavior, ex); }
        }

        if (xdata || xdicts)
            ScanOwners(tr, keys, SymbolUtilityServices.GetBlockModelSpaceId(db), BehaviorKeys.ModelPrefix, xdata, xdicts, scan);
        var wantOwners = xdata || xdicts;
        if (!(layouts || viewports || psAttributes || wantOwners)) return scan;

        foreach (var lay in PaperLayouts(db, tr))
        {
            var data = new LayoutData(lay);
            scan.Layouts.Add(data);
            var owners = new List<Owner>();
            // The overall viewport is AutoCAD's own (resized and given records when the layout is first made current): in no behavior.
            // Layout.GetViewports() lists it first; the list is empty on a layout that was never made current (no overall viewport yet).
            var overallId = ObjectId.Null;
            try
            {
                var vpIds = lay.GetViewports();
                if (vpIds.Count > 0) overallId = vpIds[0];
            }
            catch (Exception ex)
            {
                // without it the overall viewport would count as a user item: nothing these behaviors read can be trusted
                foreach (var failed in new[] { BehaviorCatalog.Layouts, BehaviorCatalog.Viewports, BehaviorCatalog.XData, BehaviorCatalog.XDicts }) scan.Failed.TryAdd(failed, ex);
            }
            foreach (ObjectId id in Open<BlockTableRecord>(tr, lay.BlockTableRecordId))
            {
                if (id == overallId) continue;
                if (tr.GetObject(id, OpenMode.ForRead) is not Entity ent) continue;
                if (layouts) Guard(BehaviorCatalog.Layouts, () =>
                {
                    var (dxf, _, key) = keys.Of(ent);
                    data.Types.Add(dxf);
                    data.HashKeys.Add(key);
                });
                if (viewports && ent is Viewport vp) Guard(BehaviorCatalog.Viewports, () => data.Viewports.Add((vp.CenterPoint.X, vp.CenterPoint.Y, vp)));
                if (psAttributes && ent is BlockReference br && br is not Table) Guard(BehaviorCatalog.PsAttributes, () =>
                {
                    var attrs = new List<(string, string)>();
                    foreach (ObjectId aid in br.AttributeCollection)
                    {
                        if (tr.GetObject(aid, OpenMode.ForRead) is not AttributeReference ar) continue;
                        string text;
                        if (ar.IsMTextAttribute) { using var mt = ar.MTextAttribute; text = mt.Contents; }
                        else text = ar.TextString;
                        attrs.Add((ar.Tag, text));
                    }
                    if (attrs.Count > 0) data.Inserts.Add((keys.IdKey(br), attrs));
                });
                if (wantOwners) CollectOwner(tr, keys, ent, xdata, xdicts, owners, scan);
            }
            NumberOwners(BehaviorKeys.PaperPrefix(lay.LayoutName), owners, scan);
        }
        return scan;
    }

    static void ScanOwners(Transaction tr, DrawingState.Keys keys, ObjectId space, string prefix, bool xdata, bool xdicts, SpaceScan scan)
    {
        var owners = new List<Owner>();
        foreach (ObjectId id in Open<BlockTableRecord>(tr, space))
            if (tr.GetObject(id, OpenMode.ForRead) is Entity ent) CollectOwner(tr, keys, ent, xdata, xdicts, owners, scan);
        NumberOwners(prefix, owners, scan);
    }

    static void CollectOwner(Transaction tr, DrawingState.Keys keys, Entity ent, bool xdata, bool xdicts, List<Owner> owners, SpaceScan scan)
    {
        try
        {
            var o = new Owner();
            if (xdata)
                using (var rb = ent.XData)
                    if (rb != null)
                        foreach (var kv in BehaviorKeys.SplitXData(rb.AsArray().Select(tv => ((int)tv.TypeCode, TypedText(tv)))))
                            o.XData[kv.Key] = BehaviorKeys.GroupsValue(kv.Value);
            if (xdicts && !ent.ExtensionDictionary.IsNull) ReadDictionary(tr, ent.ExtensionDictionary, new List<string>(), new HashSet<ObjectId>(), o.XDict);
            if (o.XData.Count == 0 && o.XDict.Count == 0) return;
            o.Key = keys.IdKey(ent);
            owners.Add(o);
        }
        catch (Exception ex)
        {
            if (Logged.Add("entity-scan")) Log.Error("BehaviorCapture xdata/xdicts: an entity was skipped (logged once)", ex);
        }
    }

    static void NumberOwners(string prefix, List<Owner> owners, SpaceScan scan)
    {
        var ids = BehaviorKeys.EntityIds(prefix, owners.Select(o => o.Key).ToList());
        for (var i = 0; i < owners.Count; i++) owners[i].Id = ids[i];
        scan.Owners.AddRange(owners);
    }

    /// <summary>A typed value as text. Handles and object ids differ between a recording and a replay: written as `*`. Binary chunks: `bin:` + length.</summary>
    static string TypedText(TypedValue tv)
    {
        if (tv.TypeCode == 1005) return "*";
        return tv.Value switch
        {
            string s => s,
            double d => N(d),
            short or int or long or ushort or uint or ulong or byte => Convert.ToString(tv.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
            bool b => B(b),
            Point3d p => P(p),
            Point2d p => BehaviorKeys.Pt2(p.X, p.Y),
            Vector3d v => P(v),
            ObjectId => "*",
            byte[] bytes => "bin:" + bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            null => "",
            var o => Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture) ?? "",
        };
    }

    /// <summary>Entries of a dictionary, recursing into nested dictionaries. Every entry is an item (path = entry names from the top): a dictionary has
    /// `type=DICTIONARY`, an Xrecord its type and data groups, any other object its DXF name. <paramref name="skip"/> filters the top level (the NOD).
    /// Depth is limited to 8 and a dictionary reached twice is read once, in case of a loop.</summary>
    static void ReadDictionary(Transaction tr, ObjectId dictId, List<string> path, HashSet<ObjectId> seen, List<(string[] Path, string Value)> output,
        Func<string, bool>? skip = null)
    {
        if (path.Count >= 8 || !seen.Add(dictId)) return;
        var dict = Open<DBDictionary>(tr, dictId);
        var entries = new List<(string Name, ObjectId Id)>();
        foreach (DBDictionaryEntry e in dict) entries.Add((e.Key, e.Value));
        entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        foreach (var (name, id) in entries)
        {
            if (skip != null && skip(name)) continue;
            path.Add(name);
            var obj = tr.GetObject(id, OpenMode.ForRead);
            var groups = new List<string>();
            string type;
            if (obj is DBDictionary) type = "DICTIONARY";
            else if (obj is Xrecord xr)
            {
                type = "XRECORD";
                using var rb = xr.Data;
                if (rb != null) foreach (var tv in rb) groups.Add(BehaviorKeys.Group(tv.TypeCode, TypedText(tv)));
            }
            else type = obj.GetRXClass().DxfName ?? obj.GetType().Name;
            output.Add((path.ToArray(), BehaviorKeys.EntryValue(type, groups)));
            if (obj is DBDictionary) ReadDictionary(tr, id, path, seen, output);
            path.RemoveAt(path.Count - 1);
        }
    }

    static IEnumerable<KeyValuePair<string, string>> XDicts(Database db, Transaction tr, SpaceScan scan)
    {
        var items = new List<KeyValuePair<string, string>>();
        foreach (var o in scan.Owners)
            foreach (var e in o.XDict) items.Add(new(BehaviorKeys.XDictKey(o.Id, e.Path), e.Value));
        // layers and block definitions
        foreach (ObjectId id in Open<LayerTable>(tr, db.LayerTableId))
        {
            var l = Open<LayerTableRecord>(tr, id);
            if (l.IsDependent || l.ExtensionDictionary.IsNull) continue;
            Owned("LAYER:" + l.Name, l.ExtensionDictionary);
        }
        foreach (ObjectId id in Open<BlockTable>(tr, db.BlockTableId))
        {
            var b = Open<BlockTableRecord>(tr, id);
            if (b.IsLayout || b.IsAnonymous || b.IsDependent || b.Name.StartsWith('*') || b.ExtensionDictionary.IsNull) continue;
            Owned("BLOCK:" + b.Name, b.ExtensionDictionary);
        }
        // the named object dictionary, minus AutoCAD's own entries (BehaviorKeys.IsBuiltInNodEntry)
        Owned("NOD", db.NamedObjectsDictionaryId, BehaviorKeys.IsBuiltInNodEntry);
        return items;

        void Owned(string owner, ObjectId dict, Func<string, bool>? skip = null)
        {
            var entries = new List<(string[] Path, string Value)>();
            ReadDictionary(tr, dict, new List<string>(), new HashSet<ObjectId>(), entries, skip);
            foreach (var e in entries) items.Add(new(BehaviorKeys.XDictKey(owner, e.Path), e.Value));
        }
    }
}
