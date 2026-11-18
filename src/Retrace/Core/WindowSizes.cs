using System.Text.Json;

namespace Retrace.Core;

// NO AutoCAD or WPF references in this file (linked into the engine test project). The remembered size of the windows the user can resize (Checkpoints, Map, Open a test,
// Training doc, the test script window): `window-sizes.json` in the Retrace root folder, name -> [width, height] in device-independent units. Position is never kept:
// every window opens centred on AutoCAD (AutoCad/WindowPlacement.cs). (AutoCAD's own persist option keeps the position too and restores it after our placement, so it is not used.)
public static class WindowSizes
{
    public const string FileName = "window-sizes.json";
    public const double MinSize = 200, MaxSize = 20000;

    public static string FilePath(string root = Paths.Root) => Path.Combine(root, FileName);

    /// <summary>The sizes in the file text. Entries that are not two sane numbers are left out; a broken file is an empty list. Never throws.</summary>
    public static Dictionary<string, (double Width, double Height)> Parse(string? json)
    {
        var d = new Dictionary<string, (double, double)>(StringComparer.Ordinal);
        try
        {
            if (string.IsNullOrWhiteSpace(json)) return d;
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return d;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Array || p.Value.GetArrayLength() != 2) continue;
                var a = p.Value[0]; var b = p.Value[1];
                if (a.ValueKind != JsonValueKind.Number || b.ValueKind != JsonValueKind.Number) continue;
                double w = a.GetDouble(), h = b.GetDouble();
                if (Sane(w) && Sane(h)) d[p.Name] = (w, h);
            }
        }
        catch (Exception) { d.Clear(); }
        return d;
    }

    static bool Sane(double v) => !double.IsNaN(v) && v >= MinSize && v <= MaxSize;

    /// <summary>The file text for <paramref name="sizes"/>.</summary>
    public static string ToJson(IReadOnlyDictionary<string, (double Width, double Height)> sizes)
    {
        var o = new SortedDictionary<string, double[]>(StringComparer.Ordinal);
        foreach (var (k, v) in sizes) if (Sane(v.Width) && Sane(v.Height)) o[k] = new[] { Math.Round(v.Width), Math.Round(v.Height) };
        return JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = true });
    }

    public static (double Width, double Height)? Read(string name, string root = Paths.Root)
    {
        try
        {
            var f = FilePath(root);
            if (!File.Exists(f)) return null;
            return Parse(File.ReadAllText(f)).TryGetValue(name, out var v) ? v : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Stores one window's size (the other windows' sizes are kept). Never throws; false when the file could not be written.</summary>
    public static bool Write(string name, double width, double height, string root = Paths.Root)
    {
        try
        {
            if (!Sane(width) || !Sane(height)) return false;
            var f = FilePath(root);
            var all = File.Exists(f) ? Parse(File.ReadAllText(f)) : new();
            all[name] = (width, height);
            Directory.CreateDirectory(root);
            AtomicFile.WriteText(f, ToJson(all));
            return true;
        }
        catch (Exception) { return false; }
    }
}
