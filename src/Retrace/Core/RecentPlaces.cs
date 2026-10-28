using System.Text.Json;

namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project).

/// <summary>The values of <see cref="RetraceSettings.PromptStyle"/>.</summary>
public static class PromptStyles
{
    public const string CommandLine = "commandLine";
    public const string Ui = "ui";
}

/// <summary>
/// Files and folders recently chosen in Retrace's prompt window, per kind of prompt ("folder", "recording", ...), newest first, at most
/// <see cref="Max"/> each, in `recent.json` under the root. Shown as "Recent places" the next time; places that no longer exist are left out
/// when listed. A broken or missing file is an empty list; a failed write is ignored (it is a convenience).
/// </summary>
public sealed class RecentPlaces
{
    public const int Max = 10;
    readonly string _file;
    readonly Dictionary<string, List<string>> _byKind;

    RecentPlaces(string file, Dictionary<string, List<string>> byKind) { _file = file; _byKind = byKind; }

    public static RecentPlaces Load(string root = Paths.Root)
    {
        var file = Paths.RecentFile(root);
        try
        {
            if (File.Exists(file) && JsonSerializer.Deserialize<Dictionary<string, List<string?>?>>(File.ReadAllText(file)) is { } d)
                // a hand-edited file may hold null lists or entries: left out
                return new RecentPlaces(file, d.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => kv.Value!.OfType<string>().Where(p => p.Length > 0).ToList(), StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new RecentPlaces(file, new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The recent places of <paramref name="kind"/>, newest first, that pass <paramref name="exists"/> (default: the file or folder exists).</summary>
    public IReadOnlyList<string> List(string kind, Func<string, bool>? exists = null)
    {
        exists ??= p => File.Exists(p) || Directory.Exists(p);
        return _byKind.TryGetValue(kind, out var l) ? l.Where(exists).ToList() : [];
    }

    /// <summary>Puts <paramref name="place"/> first in <paramref name="kind"/> (once, case-insensitive), keeps <see cref="Max"/>, and saves.</summary>
    public void Add(string kind, string place)
    {
        place = place.Trim().Trim('"');
        if (place.Length == 0) return;
        if (!_byKind.TryGetValue(kind, out var l)) _byKind[kind] = l = new List<string>();
        l.RemoveAll(p => string.Equals(p, place, StringComparison.OrdinalIgnoreCase));
        l.Insert(0, place);
        if (l.Count > Max) l.RemoveRange(Max, l.Count - Max);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_byKind, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
