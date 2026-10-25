// Recordings ingest, part 3: aggregate over sessions (frequencies, n-grams, ribbon coverage).
namespace Retrace.Engine.Recordings;

public sealed class RibbonUse
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string? ItemId { get; init; }
    public Emit.SourceMapEntry? Entry { get; init; }
    public string? MatchedBy { get; init; }
    public int Count { get; set; }
    public HashSet<string> Sessions { get; } = new();
    public Dictionary<string, int> Commands { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> Next { get; } = new();
}

public sealed class CommandUse
{
    public string Name { get; init; } = "";
    public int Total { get; set; }
    public int FromRibbon { get; set; }
    public int Typed { get; set; }
    public int Cancelled { get; set; }
    public int Failed { get; set; }
    public HashSet<string> Sessions { get; } = new();
}

public sealed record NGram(string[] Tokens, int Count, int Sessions);

public sealed class TabCoverage
{
    public Emit.SourceMapEntry Tab { get; init; } = null!;
    public int Total { get; init; }
    public List<Emit.SourceMapEntry> Exercised { get; init; } = new();
    public List<Emit.SourceMapEntry> Missing { get; init; } = new();
}

public sealed class RecordingAnalysis
{
    public List<RecordingSession> Sessions { get; init; } = new();
    public List<RibbonUse> Ribbon { get; init; } = new();
    public List<CommandUse> Commands { get; init; } = new();
    public List<NGram> Sequences { get; init; } = new();
    public List<TabCoverage> Coverage { get; init; } = new();
    public List<RecordingStep> UnmatchedRibbon { get; init; } = new();
    public SourceMapIndex? Map { get; init; }

    /// <summary>`2026-10-08_1000_change-settings` stays as it is; anything but letters, digits, '-', '_' and '.' becomes '-'.</summary>
    public static string PageName(string name)
    {
        var t = new string((name ?? "").Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray()).Trim('-', '.');
        return t.Length == 0 ? "recording" : t;
    }

    public static RecordingAnalysis Run(List<RecordingSession> sessions, SourceMapIndex? map, int maxN = 4, int top = 25)
    {
        // unique page names: the test folder's name, safe in a file name and a link; a repeated name gets -2, -3...
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sessions)
        {
            var slug = PageName(s.Name);
            var u = slug; int n = 2;
            while (!used.Add(u)) u = $"{slug}-{n++}";
            s.Slug = u;
            map?.Join(s);
        }

        var ribbon = new Dictionary<string, RibbonUse>(StringComparer.Ordinal);
        var cmds = new Dictionary<string, CommandUse>(StringComparer.OrdinalIgnoreCase);
        var unmatched = new List<RecordingStep>();
        var tokenLists = new List<(string session, List<string> tokens)>();

        foreach (var s in sessions)
        {
            var tokens = new List<string>();
            RibbonUse? prevUse = null;
            foreach (var st in s.Steps)
            {
                string? token = null;
                if (st.Kind == "ribbon")
                {
                    var key = st.Join?.Entry.Id ?? $"?{st.Tab}|{st.Panel}|{st.Text}|{st.ItemId}";
                    if (!ribbon.TryGetValue(key, out var use))
                    {
                        use = new RibbonUse
                        {
                            Key = key, Label = RibbonLabel(st), ItemId = st.ItemId, Entry = st.Join?.Entry, MatchedBy = st.Join?.MatchedBy,
                        };
                        ribbon[key] = use;
                    }
                    use.Count++;
                    use.Sessions.Add(s.Name);
                    foreach (var c in st.Commands.Take(1)) use.Commands[c] = use.Commands.GetValueOrDefault(c) + 1;
                    if (st.Join == null) unmatched.Add(st);
                    if (prevUse != null) prevUse.Next[use.Label] = prevUse.Next.GetValueOrDefault(use.Label) + 1;
                    prevUse = use;
                    token = use.Label;
                }
                else if (st.Kind == "command" || st.Kind == "lisp")
                {
                    token = st.Kind == "lisp" ? "LISP" : "cmd " + (st.Command ?? "?").ToUpperInvariant();
                    prevUse = null;
                }
                else if (st.Kind == "dialog") prevUse = null;

                if (st.Kind is "ribbon" or "command")
                {
                    for (int i = 0; i < st.Commands.Count; i++)
                    {
                        var name = st.Commands[i].ToUpperInvariant();
                        if (!cmds.TryGetValue(name, out var cu)) cmds[name] = cu = new CommandUse { Name = name };
                        cu.Total++;
                        cu.Sessions.Add(s.Name);
                        if (st.Kind == "ribbon" && i == 0) cu.FromRibbon++; else if (st.Kind == "command" && i == 0) cu.Typed++;
                        if (i == 0 && st.Outcome == "cancelled") cu.Cancelled++;
                        if (i == 0 && st.Outcome == "failed") cu.Failed++;
                    }
                }
                if (token != null) tokens.Add(token);
            }
            tokenLists.Add((s.Name, tokens));
        }

        return new RecordingAnalysis
        {
            Sessions = sessions,
            Ribbon = ribbon.Values.OrderByDescending(r => r.Count).ThenBy(r => r.Label, StringComparer.OrdinalIgnoreCase).ToList(),
            Commands = cmds.Values.OrderByDescending(c => c.Total).ThenBy(c => c.Name, StringComparer.Ordinal).ToList(),
            Sequences = NGrams(tokenLists, maxN, top),
            Coverage = map == null ? new() : ComputeCoverage(map, ribbon.Values),
            UnmatchedRibbon = unmatched,
            Map = map,
        };
    }

    public static string RibbonLabel(RecordingStep st) =>
        string.Join(" ▸ ", new[] { st.Tab, st.Panel, st.Text ?? st.ItemId }.Where(x => !string.IsNullOrWhiteSpace(x)));

    /// <summary>Counts every n-gram (2..maxN) of action tokens within sessions. An n-gram is dropped when a longer one
    /// containing it occurs just as often (it adds nothing). Ranked by count, then length.</summary>
    public static List<NGram> NGrams(List<(string session, List<string> tokens)> lists, int maxN, int top, int minCount = 2)
    {
        var counts = new Dictionary<string, (string[] toks, int count, HashSet<string> sessions)>(StringComparer.Ordinal);
        for (int n = 2; n <= maxN; n++)
            foreach (var (session, tokens) in lists)
                for (int i = 0; i + n <= tokens.Count; i++)
                {
                    var g = tokens.GetRange(i, n).ToArray();
                    var key = string.Join("\u001F", g);
                    if (!counts.TryGetValue(key, out var v)) counts[key] = v = (g, 0, new HashSet<string>());
                    v.sessions.Add(session);
                    counts[key] = (v.toks, v.count + 1, v.sessions);
                }

        var all = counts.Values.Where(v => v.count >= minCount).ToList();
        bool Dominated((string[] toks, int count, HashSet<string> sessions) v) =>
            all.Any(w => w.toks.Length == v.toks.Length + 1 && w.count == v.count && (IsSub(v.toks, w.toks)));
        return all.Where(v => !Dominated(v))
            .OrderByDescending(v => v.count).ThenByDescending(v => v.toks.Length).ThenBy(v => string.Join(">", v.toks), StringComparer.Ordinal)
            .Take(top).Select(v => new NGram(v.toks, v.count, v.sessions.Count)).ToList();
    }

    static bool IsSub(string[] small, string[] big)
    {
        for (int i = 0; i + small.Length <= big.Length; i++)
        {
            int j = 0;
            while (j < small.Length && big[i + j] == small[j]) j++;
            if (j == small.Length) return true;
        }
        return false;
    }

    static List<TabCoverage> ComputeCoverage(SourceMapIndex map, IEnumerable<RibbonUse> uses)
    {
        var hit = uses.Where(u => u.Entry != null).Select(u => u.Entry!.Id).ToHashSet(StringComparer.Ordinal);
        var list = new List<TabCoverage>();
        foreach (var tab in map.Tabs.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase))
        {
            var items = map.ItemsOf(tab).ToList();
            list.Add(new TabCoverage
            {
                Tab = tab,
                Total = items.Count,
                Exercised = items.Where(i => hit.Contains(i.Id)).ToList(),
                Missing = items.Where(i => !hit.Contains(i.Id)).ToList(),
            });
        }
        return list;
    }
}
