// Output paths, stable ids and cross-reference lookups shared by every emitter.
using Retrace.Engine.Util;

namespace Retrace.Engine.Emit;

public sealed record RibbonHit(RibbonTab Tab, RibbonPanel Panel, RibbonItem Item, RibbonItem? Parent, bool Slideout);

public sealed record FormOpener(CommandEntry Command, FlowStep Step);

public sealed class ModelIndex
{
    public EngineModel Model { get; }
    public EngineOptions Options { get; }

    public IReadOnlyList<RibbonTab> AllTabs { get; }
    public Dictionary<RibbonTab, string> TabDoc { get; } = new();
    public Dictionary<RibbonTab, string> TabAsset { get; } = new();
    public Dictionary<UiForm, string> FormDoc { get; } = new();
    public Dictionary<UiForm, string> FormAsset { get; } = new();
    public Dictionary<string, UiForm> FormsById { get; } = new();
    /// <summary>Command doc page per project.</summary>
    public Dictionary<string, string> CommandDoc { get; } = new(StringComparer.Ordinal);
    public Dictionary<CommandEntry, string> CommandAnchor { get; } = new();
    public Dictionary<CommandEntry, string> TestId { get; } = new();

    /// <summary>Upper-case command name -> scanned entry points with that name.</summary>
    public ILookup<string, CommandEntry> CommandsByName { get; }
    /// <summary>Upper-case command name or handler method -> ribbon buttons that run it.</summary>
    public ILookup<string, RibbonHit> RibbonByCommand { get; }
    public ILookup<string, FormOpener> OpenersByForm { get; }

    public ModelIndex(EngineModel model, EngineOptions options)
    {
        Model = model;
        Options = options;
        AllTabs = model.Cuix.SelectMany(c => c.Tabs).Concat(model.CodeTabs).ToList();

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tab in AllTabs)
        {
            var dir = tab.Origin == RibbonOrigin.Cuix
                ? $"cuix/{Slug.Of(Path.GetFileNameWithoutExtension(tab.Container))}"
                : $"code/{Slug.Of(tab.Container)}";
            var name = Unique(used, $"{dir}/{Slug.Of(tab.Title)}", $"{dir}/{Slug.Of(tab.Title)}-{Slug.Of(tab.Id)}");
            TabDoc[tab] = $"{DocsLayout.RefRibbon}/{name}.md";
            TabAsset[tab] = $"{DocsLayout.RefImages}/ribbon/{name}.svg";
        }

        used.Clear();
        foreach (var f in model.Forms)
        {
            FormsById[f.Id] = f;
            var dir = Slug.Of(f.Project);
            var simple = f.ClassNames.Count > 0 ? Linker.SimpleName(f.ClassNames[0]) : f.ResourceId ?? f.Title;
            var name = Unique(used, $"{dir}/{Slug.Of(simple)}", $"{dir}/{Slug.Of(f.Kind + "-" + simple)}", $"{dir}/{Slug.Of(f.Id)}");
            FormDoc[f] = $"{DocsLayout.RefDialogs}/{name}.md";
            FormAsset[f] = $"{DocsLayout.RefImages}/dialogs/{name}.svg";
        }

        used.Clear();
        var anchors = new HashSet<string>(StringComparer.Ordinal);
        int n = 0;
        foreach (var c in model.Commands.OrderBy(c => c.Project, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            CommandDoc.TryAdd(c.Project, $"{DocsLayout.RefCommands}/{Slug.Of(c.Project)}.md");
            var a = "cmd-" + Slug.Of(c.Name);
            var anchor = anchors.Add(c.Project + "#" + a) ? a : $"{a}-{++n}";
            anchors.Add(c.Project + "#" + anchor);
            CommandAnchor[c] = anchor;
        }
        int t = 0;
        foreach (var c in model.Commands.OrderBy(c => c.Project, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            TestId[c] = $"TC-CMD-{++t:000}";

        CommandsByName = model.Commands.Where(c => c.Kind != EntryKind.RibbonHandler).ToLookup(c => c.Name.ToUpperInvariant());
        var hits = AllTabs.SelectMany(tab => Linker.Walk(tab).Select(w => new RibbonHit(tab, w.panel, w.item, w.parent, w.slideout))).ToList();
        RibbonByCommand = hits
            .SelectMany(h => new[] { h.Item.Command?.ToUpperInvariant(), h.Item.HandlerMethod }
                .Where(k => k != null).Select(k => (key: k!, h)))
            .ToLookup(x => x.key, x => x.h);
        OpenersByForm = model.Commands
            .SelectMany(c => c.Flow.Where(s => s.UiFormId != null).Select(s => (s.UiFormId!, new FormOpener(c, s))))
            .ToLookup(x => x.Item1, x => x.Item2);
    }

    private static string Unique(HashSet<string> used, params string[] candidates)
    {
        foreach (var c in candidates) if (used.Add(c)) return c;
        for (int i = 2; ; i++) if (used.Add($"{candidates[0]}-{i}")) return $"{candidates[0]}-{i}";
    }

    // ------------------------------------------------------------ ids

    public static string TabId(RibbonTab t) => $"ribbon:{t.Origin.ToString().ToLowerInvariant()}:{t.Container}:{t.Id}";
    public static string PanelId(RibbonTab t, RibbonPanel p) => $"{TabId(t)}/{p.Id}";
    public static string ItemId(RibbonTab t, RibbonPanel p, RibbonItem i) => $"{PanelId(t, p)}/{i.Id}";
    public static string CommandId(CommandEntry c) => $"command:{c.Project}:{c.Name}";
    public static string FormId(UiForm f) => $"form:{f.Id}";

    /// <summary>Ribbon buttons that run this entry point. Ribbon handlers match by method only: their name is a label.</summary>
    public IEnumerable<RibbonHit> RibbonFor(CommandEntry c) =>
        (c.Kind == EntryKind.RibbonHandler ? RibbonByCommand[c.Method] : RibbonByCommand[c.Name.ToUpperInvariant()].Concat(RibbonByCommand[c.Method])).Distinct();

    public string CommandLink(string fromDoc, CommandEntry c) => $"{Md.Rel(fromDoc, CommandDoc[c.Project])}#{CommandAnchor[c]}";

    /// <summary>Entry points a ribbon button runs: by command name, then by handler method.</summary>
    public IEnumerable<CommandEntry> EntriesFor(RibbonItem item)
    {
        var list = new List<CommandEntry>();
        if (item.Command != null) list.AddRange(CommandsByName[item.Command.ToUpperInvariant()]);
        if (item.HandlerMethod != null) list.AddRange(Model.Commands.Where(c => c.Method == item.HandlerMethod));
        return list.Distinct();
    }

    // ------------------------------------------------------------ how to reach a UI element

    public List<string> ReachTab(RibbonTab tab)
    {
        var steps = new List<string>();
        if (tab.Origin == RibbonOrigin.Code)
        {
            var loaders = Model.Commands.Where(c => c.Project == tab.Container && c.Kind == EntryKind.CommandMethod
                && (Linker.SimpleName(c.Method).Contains("Ribbon", StringComparison.OrdinalIgnoreCase) || c.Name.Contains("RIBBON"))).Select(c => c.Name).ToList();
            steps.Add($"Load the {tab.Container} plugin (NETLOAD its .dll or autoload bundle)"
                + (loaders.Count > 0 ? $" and run {string.Join(" or ", loaders)} if the tab is not shown" : ""));
        }
        else
        {
            var ws = tab.Workspaces.Count > 0 ? tab.Workspaces[0] : null;
            if (ws != null) steps.Add($"Switch to the '{ws}' workspace");
            else if (tab.ContextualRule == null) steps.Add("Tab is not in any workspace: add it with CUI, or check a partial menu");
        }
        steps.Add(tab.ContextualRule != null
            ? $"Make the contextual tab appear ({tab.ContextualRule}); it activates on its own"
            : $"Click the ribbon tab '{tab.Title}'");
        return steps;
    }

    public List<string> ReachItem(RibbonHit h)
    {
        var steps = ReachTab(h.Tab);
        steps.Add($"Find the '{h.Panel.Title}' panel");
        if (h.Slideout) steps.Add($"Expand the '{h.Panel.Title}' panel slide-out (▾ on the panel title)");
        steps.Add(h.Parent is { Kind: RibbonItemKind.SplitButton or RibbonItemKind.DropDown or RibbonItemKind.Gallery }
            ? $"Open the drop-down of '{Label(h.Parent)}' (▾) and choose '{Label(h.Item)}'"
            : $"Click '{Label(h.Item)}'");
        return steps;
    }

    public List<string> ReachCommand(CommandEntry c)
    {
        var steps = new List<string>();
        steps.Add(c.Kind switch
        {
            EntryKind.CommandMethod or EntryKind.ArxAddCommand or EntryKind.ArxCommandEntry
                => $"Type {c.Name} at the command line and press Enter",
            EntryKind.LispFunction => $"Type ({c.Name}) at the command line and press Enter",
            _ => "Use the ribbon button below (no command name is registered)",
        });
        foreach (var h in RibbonFor(c).Take(3))
            steps.Add($"or ribbon: {h.Tab.Title} › {h.Panel.Title} › {Label(h.Item)}");
        return steps;
    }

    /// <summary>Steps from a command's start to the flow step that opens the form.</summary>
    public List<string> ReachForm(UiForm f)
    {
        var opener = OpenersByForm[f.Id].FirstOrDefault();
        if (opener == null)
            return new() { $"No scanned command opens this {f.Kind} (searched for {string.Join(", ", f.ClassNames.DefaultIfEmpty(f.ResourceId ?? f.Id))})" };
        var steps = ReachCommand(opener.Command).Take(1).ToList();
        foreach (var s in opener.Command.Flow)
        {
            if (s == opener.Step) break;
            if (s.Kind is FlowKind.Prompt or FlowKind.Selection) steps.Add($"Answer the prompt '{s.Text}'");
            if (s.Kind == FlowKind.Dialog && s.UiFormId != null && FormsById.TryGetValue(s.UiFormId, out var d))
                steps.Add($"In dialog '{d.Title}', continue");
        }
        steps.Add($"The {(f.Kind == UiFormKind.PaletteSet ? "palette" : "dialog")} '{f.Title}' opens");
        return steps;
    }

    public static string Label(RibbonItem i)
    {
        var t = (i.Text ?? "").Replace("\r", "").Replace("\n", " ").Trim();
        return t.Length > 0 ? t : i.Tooltip ?? i.Command ?? i.Id;
    }
}
