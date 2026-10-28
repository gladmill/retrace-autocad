// guide/: a user guide the owner edits in place. The engine owns the marked blocks, the owner owns the rest.
using System.Text;
using System.Text.Json;
using Retrace.Engine.Util;

namespace Retrace.Engine.Emit;

public sealed partial class GuideWriter(ModelIndex ix, EngineOptions opt, IReadOnlyList<Retrace.Engine.Recordings.RecordingSession>? sessions = null)
{
    public const string Dir = DocsLayout.Guide;
    private const string OwnerNote = "(your text; the engine never changes it)";

    private EngineModel M => ix.Model;
    private static readonly UTF8Encoding Utf8 = new(false);

    public int Pages { get; private set; }
    public int ButtonCount { get; private set; }
    public int CapturesUsed { get; private set; }

    /// <summary>form id -> absolute PNG path, from every --captures folder (first folder wins).</summary>
    private readonly Dictionary<string, string> _captureSrc = new(StringComparer.Ordinal);
    /// <summary>form id -> path under the output root, filled when a capture is copied.</summary>
    private readonly Dictionary<string, string> _captureOut = new(StringComparer.Ordinal);

    public void WriteAll()
    {
        LoadCaptures();
        var tabs = ix.AllTabs;
        PlanHowTos(sessions ?? []);
        WriteIndex(tabs);
        foreach (var tab in tabs) WriteTab(tab);
        WriteHowTos();
    }

    /// <summary>A button section of a tab page: its block key and the HTML anchor the how-to pages link to.</summary>
    private sealed record Section(RibbonPanel Panel, RibbonHit Hit, string Key, string Anchor);

    private readonly Dictionary<RibbonTab, List<Section>> _sections = new();
    /// <summary>Source map id of a ribbon item -> tab guide page and anchor (first section wins).</summary>
    private readonly Dictionary<string, (string Doc, string Anchor)> _sectionOf = new(StringComparer.Ordinal);

    private List<Section> SectionsOf(RibbonTab tab)
    {
        if (_sections.TryGetValue(tab, out var done)) return done;
        var list = new List<Section>();
        var itemKeys = new Keys();
        var anchors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var panel in tab.Panels)
            foreach (var (p, item, parent, slide) in Buttons(tab).Where(b => b.panel == panel))
            {
                var id = ModelIndex.ItemId(tab, panel, item);
                var a = "b-" + Slug.Of(ModelIndex.Label(item));
                var anchor = a;
                for (int i = 2; !anchors.Add(anchor); i++) anchor = $"{a}-{i}";
                list.Add(new Section(panel, new RibbonHit(tab, panel, item, parent, slide), "button:" + itemKeys.Unique(id), anchor));
                _sectionOf.TryAdd(id, (GuideDoc(ix.TabDoc[tab]), anchor));
            }
        return _sections[tab] = list;
    }

    // ------------------------------------------------------------ captures

    private void LoadCaptures()
    {
        foreach (var dir in opt.Captures)
        {
            if (!Directory.Exists(dir)) { M.Warnings.Add($"guide: captures folder not found: {dir}"); continue; }
            foreach (var jf in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(jf));
                    if (!doc.RootElement.TryGetProperty("formId", out var fid) || fid.GetString() is not { Length: > 0 } id) continue;
                    var png = Path.ChangeExtension(jf, ".png");
                    if (File.Exists(png)) _captureSrc.TryAdd(id, png);
                }
                catch (Exception ex) when (ex is JsonException or IOException)
                {
                    M.Warnings.Add($"guide: cannot read capture {jf}: {ex.Message}");
                }
            }
        }
    }

    /// <summary>Copy the capture for a form into guide/images/captures/ and return its path under the output root.</summary>
    private string? Capture(UiForm f)
    {
        if (_captureOut.TryGetValue(f.Id, out var done)) return done;
        if (!_captureSrc.TryGetValue(f.Id, out var src)) return null;
        var used = _captureOut.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rel = $"{DocsLayout.GuideImages}/captures/{Slug.Of(f.Id)}.png";
        for (int i = 2; used.Contains(rel); i++) rel = $"{DocsLayout.GuideImages}/captures/{Slug.Of(f.Id)}-{i}.png";
        var dest = Path.Combine(opt.Output, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var bytes = File.ReadAllBytes(src);
        if (!File.Exists(dest) || !File.ReadAllBytes(dest).AsSpan().SequenceEqual(bytes)) File.WriteAllBytes(dest, bytes);
        CapturesUsed++;
        return _captureOut[f.Id] = rel;
    }

    /// <summary>The capture for a form, else its wireframe if one was written, else null (path under the output root).</summary>
    private string? FormImage(UiForm f) =>
        Capture(f) ?? (File.Exists(Path.Combine(opt.Output, ix.FormAsset[f].Replace('/', Path.DirectorySeparatorChar))) ? ix.FormAsset[f] : null);

    // ------------------------------------------------------------ pages

    /// <summary>`reference/ribbon/x.md` -> `guide/ribbon/x.md`.</summary>
    private static string GuideDoc(string refDoc) => DocsLayout.GuidePageOf(refDoc);

    private void WriteIndex(IReadOnlyList<RibbonTab> tabs)
    {
        const string doc = Dir + "/index.md";
        var sb = new StringBuilder("# Ribbon tabs\n\n");
        sb.Append("Every ribbon tab the engine found, grouped by where it is defined. ")
          .Append($"Reference pages with all technical detail are under [ribbon]({Md.Rel(doc, DocsLayout.RefRibbon + "/index.md")}).\n\n");
        void Group(string heading, IEnumerable<RibbonTab> group)
        {
            var list = group.ToList();
            if (list.Count == 0) return;
            sb.Append($"## {heading}\n\n");
            sb.Append(Md.Table(new[] { "Tab", "Id", "Panels", "Buttons" },
                list.Select(t => new[]
                {
                    $"[{Md.Cell(t.Title)}]({Md.Rel(doc, GuideDoc(ix.TabDoc[t]))})",
                    Md.Code(t.Id), t.Panels.Count.ToString(), Buttons(t).Count().ToString(),
                }))).Append('\n');
        }
        foreach (var c in M.Cuix) Group($"{c.File} (CUIX)", c.Tabs);
        foreach (var g in M.CodeTabs.GroupBy(t => t.Container)) Group($"{g.Key} (built in code)", g);
        if (_howTos.Count > 0)
        {
            sb.Append("## How-to\n\nStep-by-step procedures, written from recorded sessions.\n\n");
            foreach (var h in _howTos) sb.Append($"- [{Md.Cell(h.Name)}]({Md.Rel(doc, h.Doc)})\n");
            sb.Append('\n');
        }
        var pre = "# User guide\n\n_Introduce the guide: who it is for and how to use it. " + OwnerNote + "_\n\n";
        var blocks = new List<GuideBlock> { new("page", sb.ToString(), "_Anything else readers should know first? " + OwnerNote + "_") };
        WritePage(doc, pre, blocks);
    }

    /// <summary>Ribbon buttons that get their own section: everything except separators and row containers.</summary>
    private static IEnumerable<(RibbonPanel panel, RibbonItem item, RibbonItem? parent, bool slide)> Buttons(RibbonTab tab) =>
        Linker.Walk(tab).Where(w => w.item.Kind is not (RibbonItemKind.Separator or RibbonItemKind.RowPanel));

    private void WriteTab(RibbonTab tab)
    {
        var refDoc = ix.TabDoc[tab];
        var doc = GuideDoc(refDoc);
        var blocks = new List<GuideBlock>();
        var buttons = Buttons(tab).ToList();
        var sections = SectionsOf(tab);

        var head = new StringBuilder($"# {Md.Cell(tab.Title)}\n\n");
        head.Append($"{tab.Panels.Count} panel{(tab.Panels.Count == 1 ? "" : "s")}, {buttons.Count} button{(buttons.Count == 1 ? "" : "s")}.\n\n");
        head.Append("**How to reach it:** ").Append(string.Join(" → ", ix.ReachTab(tab))).Append("\n\n");
        var asset = ix.TabAsset[tab];
        if (File.Exists(Path.Combine(opt.Output, asset.Replace('/', Path.DirectorySeparatorChar))))
            head.Append($"![{Md.Cell(tab.Title)} {ix.TabPictureName(tab)}]({Md.Rel(doc, asset)})\n\n");
        head.Append($"Reference page: [{Md.Cell(tab.Title)}]({Md.Rel(doc, refDoc)})\n");
        blocks.Add(new("page", head.ToString(), $"_What is the {StubName(tab.Title)} tab for? {OwnerNote}_"));

        var panelKeys = new Keys();
        foreach (var panel in tab.Panels)
        {
            var pKey = "panel:" + panelKeys.Unique(panel.Id);
            blocks.Add(new(pKey, PanelBlock(doc, tab, panel), $"_What is the {StubName(panel.Title)} panel for? {OwnerNote}_"));
            foreach (var sec in sections.Where(x => x.Panel == panel))
            {
                blocks.Add(new(sec.Key, ButtonBlock(doc, sec.Hit, sec.Anchor),
                    $"_What does {StubName(ModelIndex.Label(sec.Hit.Item))} do, and when would someone use it? {OwnerNote}_"));
                ButtonCount++;
            }
        }
        WritePage(doc, "", blocks);
    }

    private static string StubName(string s) => s.Replace("_", "\\_").Replace("*", "\\*");

    private string PanelBlock(string doc, RibbonTab tab, RibbonPanel panel)
    {
        var sb = new StringBuilder($"## {Md.Cell(panel.Title)}\n\n");
        var rows = Buttons(tab).Where(b => b.panel == panel).Select(b => new[]
        {
            (b.parent != null ? "↳ " : "") + Md.Cell(ModelIndex.Label(b.item)),
            Md.Code(b.item.Command ?? b.item.HandlerMethod ?? Trunc(b.item.Macro, 40)),
            Md.Cell(Trunc(b.item.Tooltip, 80)),
        }).ToList();
        if (rows.Count == 0) sb.Append("No buttons.\n");
        else sb.Append(Md.Table(new[] { "Button", "Runs", "Tooltip" }, rows));
        return sb.ToString();
    }

    private string ButtonBlock(string doc, RibbonHit h, string anchor)
    {
        var item = h.Item;
        var label = ModelIndex.Label(item);
        var sb = new StringBuilder($"<a id=\"{anchor}\"></a>\n### {Md.Cell(label)}\n\n");
        var start = $"Ribbon: {h.Tab.Title} › {h.Panel.Title}" + (h.Parent != null ? $" › {ModelIndex.Label(h.Parent)}" : "") + $" › {label}";
        if (h.Slideout) start += " (in the panel slide-out)";
        sb.Append("- **Starts with:** ").Append(Md.Cell(start)).Append('\n');
        if (item.Command != null) sb.Append($"- **Command:** {Md.Code(item.Command)}\n");
        if (!string.IsNullOrWhiteSpace(item.Macro)) sb.Append($"- **Macro:** {Md.Code(item.Macro)}\n");
        var entries = ix.EntriesFor(item).ToList();
        if (item.HandlerMethod != null || entries.Count > 0)
        {
            var methods = entries.Select(e => e.Method).Concat(item.HandlerMethod != null ? new[] { item.HandlerMethod } : [])
                .Distinct().ToList();
            var where = entries.Select(e => e.Source.ToString()).Distinct().FirstOrDefault() ?? item.Source?.ToString();
            sb.Append($"- **Code entry point:** {string.Join(", ", methods.Select(Md.Code))}{(where != null ? " at " + Md.Code(where) : "")}\n");
        }
        if (!string.IsNullOrWhiteSpace(item.Tooltip)) sb.Append($"- **Tooltip:** {Md.Cell(item.Tooltip)}\n");
        if (ButtonPicture(h, anchor) is { } pic) sb.Append($"\n![{Md.Cell(label)} in the {Md.Cell(h.Panel.Title)} panel]({Md.Rel(doc, pic)})\n");

        var forms = entries.SelectMany(e => e.Flow).Where(s => s.UiFormId != null && ix.FormsById.ContainsKey(s.UiFormId))
            .Select(s => ix.FormsById[s.UiFormId!]).Distinct().ToList();
        foreach (var f in forms)
        {
            sb.Append($"- **Opens:** {f.Kind} [{Md.Cell(f.Title)}]({Md.Rel(doc, ix.FormDoc[f])})\n");
        }
        foreach (var f in forms)
        {
            var img = FormImage(f);
            if (img != null) sb.Append($"\n![{Md.Cell(f.Title)}]({Md.Rel(doc, img)})\n");
        }

        var used = UsedIn(ModelIndex.ItemId(h.Tab, h.Panel, item));
        if (used.Count > 0)
            sb.Append("\n**Used in:** ").Append(string.Join(", ", used.Select(u => $"[{Md.Cell(u.Name)}]({Md.Rel(doc, u.Doc)})"))).Append('\n');

        sb.Append("\nReference: ");
        var refs = new List<string> { $"[{Md.Cell(h.Tab.Title)} tab]({Md.Rel(doc, ix.TabDoc[h.Tab])})" };
        refs.AddRange(entries.Select(e => $"[command {Md.Cell(e.Name)}]({ix.CommandLink(doc, e)})"));
        sb.Append(string.Join(", ", refs)).Append('\n');
        return sb.ToString();
    }

    /// <summary>The button's panel as captured in AutoCAD with the button outlined (written under guide/images/ribbon), or null.</summary>
    private string? ButtonPicture(RibbonHit h, string anchor)
    {
        if (ix.Captures.For(h.Tab) is not { } cap) return null;
        string? svg;
        try { svg = ix.Captures.ButtonSvg(cap, h.Panel, h.Item); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { M.Warnings.Add($"ribbon capture of {h.Tab.Title}: {ex.Message}"); return null; }
        if (svg == null) return null;
        // one folder per tab, named as its reference picture (unique among the tabs)
        var tabName = ix.TabAsset[h.Tab][(DocsLayout.RefImages.Length + "/ribbon/".Length)..^".svg".Length];
        var rel = $"{DocsLayout.GuideRibbonImages}/{tabName}/{anchor}.svg";
        var dest = Path.Combine(opt.Output, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        if (!File.Exists(dest) || File.ReadAllText(dest) != svg) File.WriteAllText(dest, svg, Utf8);
        return rel;
    }

    private static string? Trunc(string? s, int n) => s == null ? null : s.Length <= n ? s : s[..(n - 1)] + "…";

    /// <summary>Unique keys: a repeated id gets "~2", "~3" so every key in a page is distinct.</summary>
    private sealed class Keys
    {
        private readonly HashSet<string> _used = new(StringComparer.Ordinal);
        public string Unique(string id)
        {
            var safe = GuideMerge.SafeKey(id);
            var k = safe;
            for (int i = 2; !_used.Add(k); i++) k = $"{safe}~{i}";
            return k;
        }
    }

    // ------------------------------------------------------------ file merge

    private void WritePage(string rel, string preamble, List<GuideBlock> blocks)
    {
        Pages++;
        var path = Path.Combine(opt.Output, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
        {
            File.WriteAllBytes(path, Utf8.GetBytes(GuideMerge.Create(preamble, blocks)));
            return;
        }
        var old = Utf8.GetString(File.ReadAllBytes(path));
        var result = GuideMerge.Merge(old, blocks);
        var side = Path.ChangeExtension(path, null) + ".engine-new.md";
        if (result.Text == null)
        {
            File.WriteAllBytes(side, Utf8.GetBytes(GuideMerge.Create(preamble, blocks)));
            var msg = $"guide: {rel} has unbalanced or duplicate engine markers ({result.Problem}); left untouched, fresh version written to {Path.GetFileName(side)}";
            M.Warnings.Add(msg);
            return;
        }
        if (result.Text != old) File.WriteAllBytes(path, Utf8.GetBytes(result.Text));
        if (File.Exists(side)) File.Delete(side);
    }
}
