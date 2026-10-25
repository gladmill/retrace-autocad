using System.Text.Json;

namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project). Decides where the Retrace ribbon tab goes.
// ReadSettingFromDisk reads the one settings key by hand (the ribbon is installed before the settings object is needed).

/// <summary>Where the Retrace tab goes: setting `ribbonTabPosition` = "afterHome" (default), "first", "last" or "after:&lt;tab id or title&gt;".</summary>
public static class RibbonTabPosition
{
    public const string AfterHome = "afterHome", First = "first", Last = "last", AfterPrefix = "after:";
    public const string HomeId = "ID_TabHome";

    /// <summary>Canonical form of a stored value: "" / unknown -> "afterHome"; "after:" with no name -> "afterHome".</summary>
    public static string Normalize(string? setting)
    {
        var t = (setting ?? "").Trim();
        if (t.Length == 0) return AfterHome;
        if (t.Equals(AfterHome, StringComparison.OrdinalIgnoreCase)) return AfterHome;
        if (t.Equals(First, StringComparison.OrdinalIgnoreCase)) return First;
        if (t.Equals(Last, StringComparison.OrdinalIgnoreCase)) return Last;
        if (t.StartsWith(AfterPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var name = t.Substring(AfterPrefix.Length).Trim();
            return name.Length == 0 ? AfterHome : AfterPrefix + name;
        }
        return AfterHome;
    }

    /// <summary>
    /// The index to insert the tab at, in the list WITHOUT the Retrace tab (remove it first, then Insert at this index).
    /// A tab with id <paramref name="ownTabId"/> in <paramref name="tabs"/> is ignored. Anchor missing -> right after Home -> last.
    /// </summary>
    public static int ChooseIndex(string? setting, IReadOnlyList<(string Id, string Title)> tabs, string ownTabId)
    {
        var others = tabs.Where(t => !string.Equals(t.Id, ownTabId, StringComparison.OrdinalIgnoreCase)).ToList();
        var s = Normalize(setting);
        if (s == First) return 0;
        if (s == Last) return others.Count;
        if (s.StartsWith(AfterPrefix, StringComparison.Ordinal))
        {
            var i = FindTab(others, s.Substring(AfterPrefix.Length));
            if (i >= 0) return i + 1;
        }
        var h = FindHome(others);
        return h >= 0 ? h + 1 : others.Count;
    }

    static int FindHome(List<(string Id, string Title)> tabs)
    {
        var i = tabs.FindIndex(t => string.Equals(t.Id, HomeId, StringComparison.OrdinalIgnoreCase));
        return i >= 0 ? i : tabs.FindIndex(t => string.Equals(t.Title?.Trim(), "Home", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>By id first (case-insensitive), then by title.</summary>
    static int FindTab(List<(string Id, string Title)> tabs, string name)
    {
        name = name.Trim();
        var i = tabs.FindIndex(t => string.Equals(t.Id, name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 ? i : tabs.FindIndex(t => string.Equals(t.Title?.Trim(), name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The stored `ribbonTabPosition` of `settings.json` text; "" when missing or the text is not valid JSON. Never throws.</summary>
    public static string ReadSetting(string? settingsJson)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(settingsJson)) return "";
            using var doc = JsonDocument.Parse(settingsJson, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("ribbonTabPosition", out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? "";
        }
        catch (Exception) { }
        return "";
    }

    /// <summary>Reads settings.json in the root folder. Never throws.</summary>
    public static string ReadSettingFromDisk(string root = Paths.Root)
    {
        try
        {
            var f = Paths.SettingsFile(root);
            return File.Exists(f) ? ReadSetting(File.ReadAllText(f)) : "";
        }
        catch (Exception) { return ""; }
    }
}
