using Autodesk.AutoCAD.DatabaseServices;

namespace Retrace.AutoCad;

/// <summary>The layer names of a template file, read through a side database (read-only; the file is not opened as a document). Cached for the session per path and last write time.</summary>
static class TemplateLayers
{
    static readonly Dictionary<(string Path, long Ticks), List<string>?> Cache = new();

    /// <summary>The layer names, or null when the file cannot be read.</summary>
    public static IReadOnlyCollection<string>? Read(string path)
    {
        try
        {
            var key = (path.ToLowerInvariant(), File.GetLastWriteTimeUtc(path).Ticks);
            if (Cache.TryGetValue(key, out var hit)) return hit;
            List<string>? names = null;
            try
            {
                using var side = new Database(false, true);
                side.ReadDwgFile(path, FileShare.Read, true, "");
                side.CloseInput(true);
                using var tr = side.TransactionManager.StartTransaction();
                var lt = (LayerTable)tr.GetObject(side.LayerTableId, OpenMode.ForRead);
                names = new List<string>();
                foreach (ObjectId id in lt) names.Add(((LayerTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name);
            }
            catch (Exception ex) { Log.Error("TemplateLayers " + path, ex); names = null; }
            Cache[key] = names;
            return names;
        }
        catch (Exception ex) { Log.Error("TemplateLayers", ex); return null; }
    }
}
