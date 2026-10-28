using System.Text.Json;

namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project).

/// <summary>A folder in the Map window's list: <paramref name="Checked"/> = ticked, i.e. part of the next map.</summary>
public sealed record MapFolder(string Path, bool Checked);

/// <summary>
/// Every folder ever given to the Map window, newest first, and which of them are ticked, in <c>map-folders.json</c> under the root
/// (<see cref="Paths.MapFoldersFile"/>). No limit: the list keeps every folder until the user removes it; folders that no longer exist stay
/// (a network drive may come back) and the window shows them as not found. Also the window's two options. A missing or broken file is an
/// empty list with the defaults. Paths are kept as given (trimmed, quotes and a trailing separator removed); two paths that differ only in
/// case are one folder.
/// </summary>
public sealed class MapFolders
{
    readonly string _file;
    readonly List<MapFolder> _folders = new();

    MapFolders(string file) { _file = file; }

    /// <summary>Never throws.</summary>
    public static MapFolders Load(string root = Paths.Root)
    {
        var list = new MapFolders(Paths.MapFoldersFile(root));
        try
        {
            if (File.Exists(list._file)) list.Read(File.ReadAllText(list._file));
        }
        catch (Exception) { }   // an unreadable file is an empty list: it only holds a convenience
        return list;
    }

    // A hand-edited file may hold anything: what cannot be read is left out, the rest is kept.
    void Read(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            var root = doc.RootElement;
            if (root.TryGetProperty("liveRibbon", out var live) && live.ValueKind is JsonValueKind.True or JsonValueKind.False) LiveRibbon = live.GetBoolean();
            if (root.TryGetProperty("allTabs", out var all) && all.ValueKind is JsonValueKind.True or JsonValueKind.False) AllTabs = all.GetBoolean();
            if (!root.TryGetProperty("folders", out var folders) || folders.ValueKind != JsonValueKind.Array) return;
            foreach (var entry in folders.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("path", out var p) || p.ValueKind != JsonValueKind.String) continue;
                var path = Normalize(p.GetString() ?? "");
                if (path.Length == 0 || IndexOf(path) >= 0) continue;   // the first of two paths that differ only in case wins
                var ticked = entry.TryGetProperty("checked", out var c) && c.ValueKind == JsonValueKind.True;
                _folders.Add(new MapFolder(path, ticked));
            }
        }
        catch (JsonException) { _folders.Clear(); LiveRibbon = true; AllTabs = false; }
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<MapFolder> Folders => _folders.ToArray();
    /// <summary>The ticked folders, newest first.</summary>
    public IReadOnlyList<string> Checked => _folders.Where(f => f.Checked).Select(f => f.Path).ToArray();
    /// <summary>Also map the ribbon AutoCAD shows now. Default on.</summary>
    public bool LiveRibbon { get; set; } = true;
    /// <summary>List every live tab, also AutoCAD's own. Default off.</summary>
    public bool AllTabs { get; set; }

    /// <summary>The user chose <paramref name="path"/> just now: it goes to the top (moved if it was listed), ticked.</summary>
    public void Add(string path)
    {
        path = Normalize(path);
        if (path.Length == 0) return;
        RemoveAt(IndexOf(path));
        _folders.Insert(0, new MapFolder(path, true));
    }

    /// <summary>A map ran on <paramref name="paths"/>: they go to the top in the given order (the first given ends first) and are ticked;
    /// every other folder is unticked. Folders not listed yet are added.</summary>
    public void Use(IEnumerable<string> paths)
    {
        var used = new List<MapFolder>();
        foreach (var raw in paths)
        {
            var path = Normalize(raw);
            if (path.Length == 0 || used.Any(u => Same(u.Path, path))) continue;
            used.Add(new MapFolder(path, true));
        }
        var rest = _folders.Where(f => !used.Any(u => Same(u.Path, f.Path))).Select(f => f with { Checked = false });
        var all = used.Concat(rest).ToList();
        _folders.Clear();
        _folders.AddRange(all);
    }

    public void SetChecked(string path, bool on)
    {
        int i = IndexOf(Normalize(path));
        if (i >= 0) _folders[i] = _folders[i] with { Checked = on };
    }

    /// <summary>Takes <paramref name="path"/> off the list; false when it was not listed.</summary>
    public bool Remove(string path)
    {
        int i = IndexOf(Normalize(path));
        RemoveAt(i);
        return i >= 0;
    }

    /// <summary>Writes the file (temp file, then moved over the old one). False with the reason when it cannot.</summary>
    public bool Save(out string error)
    {
        error = "";
        var tmp = _file + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var doc = new
            {
                folders = _folders.Select(f => new { path = f.Path, @checked = f.Checked }).ToArray(),
                liveRibbon = LiveRibbon,
                allTabs = AllTabs,
            };
            File.WriteAllText(tmp, JsonSerializer.Serialize(doc, WriteOptions));
            File.Move(tmp, _file, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tmp); } catch (Exception) { }
            error = ex.Message;
            return false;
        }
    }

    // The paths are written as they are (a ü stays a ü), so the file can be read and edited by hand.
    static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Trimmed, without surrounding quotes and without a trailing separator (a drive root keeps its: "C:\").</summary>
    public static string Normalize(string path)
    {
        var p = (path ?? "").Trim().Trim('"').Trim();
        var t = p.TrimEnd('\\', '/');
        if (t.Length == p.Length) return p;
        if (t.Length == 0) return p[..1];                                        // "/" or "\": the root of a Unix path
        if (t.Length == 2 && t[1] == ':' && char.IsLetter(t[0])) return p[..3];   // "C:\" stays a root: "C:" would mean the current folder of the drive
        return t;
    }

    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    int IndexOf(string normalized) => _folders.FindIndex(f => Same(f.Path, normalized));

    void RemoveAt(int i)
    {
        if (i >= 0) _folders.RemoveAt(i);
    }
}
