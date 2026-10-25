// Recordings ingest, part 2: join session steps to the engine's data/sourcemap.json.
using System.Text.Json;
using Retrace.Engine.Emit;

namespace Retrace.Engine.Recordings;

public sealed record JoinResult(SourceMapEntry Entry, string MatchedBy);
public sealed record FormMatch(SourceMapEntry Entry);

public sealed class SourceMapIndex
{
    public string MapDir { get; }
    public List<SourceMapEntry> Entries { get; }
    public List<SourceMapEntry> Tabs { get; }
    public List<SourceMapEntry> Items { get; }
    public List<SourceMapEntry> Forms { get; }

    readonly ILookup<string, SourceMapEntry> _itemsBySegment;
    readonly ILookup<string, SourceMapEntry> _itemsByCommand;

    public SourceMapIndex(string mapDir, IEnumerable<SourceMapEntry> entries)
    {
        MapDir = mapDir;
        Entries = entries.ToList();
        Tabs = Entries.Where(e => e.Kind == "ribbon-tab").ToList();
        Items = Entries.Where(e => e.Kind == "ribbon-item").ToList();
        Forms = Entries.Where(e => e.Kind == "form").ToList();
        _itemsBySegment = Items.ToLookup(e => LastSegment(e.Id), StringComparer.OrdinalIgnoreCase);
        _itemsByCommand = Items.Where(e => !string.IsNullOrEmpty(e.Command)).ToLookup(e => e.Command!, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Accepts the docs folder or the sourcemap.json itself (`data\sourcemap.json`). MapDir is the docs folder: the entries' pages and images are relative to it.</summary>
    public static SourceMapIndex Load(string path)
    {
        var file = Directory.Exists(path) ? Path.Combine(path, DocsLayout.SourceMapFile.Replace('/', Path.DirectorySeparatorChar)) : path;
        if (!File.Exists(file)) throw new FileNotFoundException($"sourcemap.json not found: {file}");
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var entries = doc.RootElement.GetProperty("entries").Deserialize<List<SourceMapEntry>>(SourceMapWriter.Json) ?? new();
        var dir = Path.GetDirectoryName(Path.GetFullPath(file))!;
        if (string.Equals(Path.GetFileName(dir), DocsLayout.Data, StringComparison.OrdinalIgnoreCase)) dir = Path.GetDirectoryName(dir)!;
        return new SourceMapIndex(dir, entries);
    }

    /// <summary>"ribbon:cuix:acad.cuix:ID_TabHome/ID_PnlDraw/ID_Line" -> "ID_Line".</summary>
    public static string LastSegment(string id)
    {
        var i = id.LastIndexOf('/');
        return i >= 0 ? id[(i + 1)..] : id;
    }

    /// <summary>Total order of preference among candidates: same tab title, then same panel title.</summary>
    static SourceMapEntry? Best(IEnumerable<SourceMapEntry> candidates, string? tab, string? panel, string? text, string? tabId = null)
    {
        SourceMapEntry? best = null; int bestScore = -1;
        foreach (var c in candidates)
        {
            int score = 0;
            if (TabIdMatches(c.Id, tabId)) score += 8;
            if (tab != null && Eq(c.Capture.GetValueOrDefault("tab"), tab)) score += 4;
            if (panel != null && Eq(c.Capture.GetValueOrDefault("panel"), panel)) score += 2;
            if (text != null && Eq(c.Title, text)) score += 1;
            if (score > bestScore) { best = c; bestScore = score; }
        }
        return best;
    }

    /// <summary>The tab id inside an item id: "ribbon:cuix:acad.CUIX:ID_TabHome/ID_PanelDraw/ID_Line" -> "ID_TabHome".</summary>
    public static string TabIdOf(string itemId)
    {
        var first = itemId.Split('/')[0];
        var i = first.LastIndexOf(':');
        return i >= 0 ? first[(i + 1)..] : first;
    }

    /// <summary>The recorded tab id ("ACAD.ID_TabHome", or "cuix name.ID_...") equals the item's tab id, with or without its prefix.</summary>
    static bool TabIdMatches(string itemId, string? recorded)
    {
        if (string.IsNullOrWhiteSpace(recorded)) return false;
        var want = TabIdOf(itemId);
        var r = recorded.Trim();
        if (string.Equals(r, want, StringComparison.OrdinalIgnoreCase)) return true;
        var dot = r.LastIndexOf('.');
        return dot > 0 && string.Equals(r[(dot + 1)..], want, StringComparison.OrdinalIgnoreCase);
    }

    static bool Eq(string? a, string? b) => a != null && b != null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Joins a ribbon step to its ribbon-item entry: by item Id (the CUIX ElementID), else by the macro's command.</summary>
    public JoinResult? JoinRibbon(RecordingStep step)
    {
        if (!string.IsNullOrEmpty(step.ItemId))
        {
            var byId = _itemsBySegment[step.ItemId].ToList();
            if (byId.Count > 0) return new JoinResult(Best(byId, step.Tab, step.Panel, step.Text, step.TabId)!, "id");
        }
        var cmd = RecordingReader.MacroCommand(step.Macro) ?? step.Command?.ToUpperInvariant();
        if (cmd != null)
        {
            var byCmd = _itemsByCommand[cmd].ToList();
            if (byCmd.Count > 0) return new JoinResult(Best(byCmd, step.Tab, step.Panel, step.Text, step.TabId)!, "macro");
        }
        return null;
    }

    /// <summary>A form whose title equals the dialog's window title (case-insensitive; also "Title | Variant" forms).</summary>
    public FormMatch? MatchForm(string? dialogTitle)
    {
        if (string.IsNullOrWhiteSpace(dialogTitle)) return null;
        var t = dialogTitle.Trim();
        var exact = Forms.FirstOrDefault(f => Eq(f.Title, t) || Eq(f.Capture.GetValueOrDefault("windowTitle"), t));
        if (exact != null) return new FormMatch(exact);
        var prefix = Forms.FirstOrDefault(f => f.Title.Split('|')[0].Trim().Length > 2 && Eq(f.Title.Split('|')[0], t));
        return prefix != null ? new FormMatch(prefix) : null;
    }

    public void Join(RecordingSession s)
    {
        foreach (var st in s.Steps)
        {
            if (st.Kind == "ribbon") st.Join = JoinRibbon(st);
            else if (st.Kind == "dialog") st.Form = MatchForm(st.DialogTitle);
        }
    }

    /// <summary>Items under a ribbon-tab entry.</summary>
    public IEnumerable<SourceMapEntry> ItemsOf(SourceMapEntry tab) =>
        Items.Where(i => i.Id.StartsWith(tab.Id + "/", StringComparison.Ordinal));
}
