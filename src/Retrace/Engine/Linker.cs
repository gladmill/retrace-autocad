namespace Retrace.Engine;

/// <summary>Cross-links ribbon items, commands, flows and forms after all scanners ran.</summary>
public static class Linker
{
    /// <summary>A code ribbon button's handler object only names a class and a method (see CodeRibbonReader); keep it when
    /// <paramref name="declared"/> finds that method in the scanned sources, otherwise drop it with one warning.</summary>
    public static void KeepDeclaredHandlers(EngineModel model, Func<string, bool> declared)
    {
        var dropped = new List<string>();
        foreach (var tab in model.CodeTabs)
        foreach (var (_, item, _, _) in Walk(tab))
        {
            if (item.HandlerMethod is not { Length: > 0 } handler || declared(handler)) continue;
            dropped.Add(handler);
            item.HandlerMethod = null;
            item.Extra.Remove("assembly");
        }
        if (dropped.Count == 0) return;
        var names = dropped.Distinct().ToList();
        model.Warnings.Add($"code ribbon: {dropped.Count} button(s) pass a CommandParameter object that names no method in the scanned sources, " +
                           $"so they get no code entry point: {string.Join(", ", names.Take(3))}{(names.Count > 3 ? ", ..." : "")}");
    }

    /// <summary>Code ribbon buttons that call a method directly (no registered command) become entry points
    /// of kind RibbonHandler, so their flows get analyzed and tested like commands.</summary>
    public static void AddRibbonHandlers(EngineModel model)
    {
        var known = new HashSet<string>(model.Commands.Select(c => c.Method), StringComparer.Ordinal);
        foreach (var tab in model.CodeTabs)
        foreach (var (panel, item, _, _) in Walk(tab))
        {
            if (item.HandlerMethod is not { Length: > 0 } handler || item.Command != null) continue;
            if (!known.Add(handler)) continue;
            model.Commands.Add(new CommandEntry
            {
                Name = string.IsNullOrWhiteSpace(item.Text) ? item.Id : item.Text.Trim(),
                Kind = EntryKind.RibbonHandler,
                Lang = Lang.CSharp,
                Project = tab.Container,
                Source = item.Source ?? panel.Source ?? tab.Source ?? new SourceRef("?", 0),
                Method = handler,
                Extra = { ["ribbon"] = $"{tab.Title} › {panel.Title} › {item.Text}" },
            });
        }
    }

    /// <summary>Point Dialog/Palette flow steps at the UiForm they open.</summary>
    public static void Link(EngineModel model)
    {
        var byName = new Dictionary<string, UiForm>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in model.Forms)
        {
            foreach (var cls in f.ClassNames)
            {
                byName.TryAdd(cls, f);
                byName.TryAdd(SimpleName(cls), f);
            }
            if (f.ResourceId != null) byName.TryAdd(f.ResourceId, f);
        }
        foreach (var c in model.Commands)
        foreach (var s in c.Flow)
        {
            if (s.Kind is not (FlowKind.Dialog or FlowKind.Palette) || s.Target == null) continue;
            if (byName.TryGetValue(s.Target, out var form) || byName.TryGetValue(SimpleName(s.Target), out form))
                s.UiFormId = form.Id;
        }
    }

    public static string SimpleName(string name)
    {
        int dot = name.LastIndexOf('.');
        int colons = name.LastIndexOf("::", StringComparison.Ordinal);
        int cut = Math.Max(dot + 1, colons >= 0 ? colons + 2 : 0);
        return cut < name.Length ? name[cut..] : name;
    }

    /// <summary>Every item of a tab, depth first, with its panel and the parent item (split buttons, row panels).</summary>
    public static IEnumerable<(RibbonPanel panel, RibbonItem item, RibbonItem? parent, bool slideout)> Walk(RibbonTab tab)
    {
        foreach (var p in tab.Panels)
        foreach (var r in p.Rows)
        foreach (var x in WalkItems(p, r.Items, null, r.IsSlideout))
            yield return x;
    }

    private static IEnumerable<(RibbonPanel, RibbonItem, RibbonItem?, bool)> WalkItems(
        RibbonPanel p, List<RibbonItem> items, RibbonItem? parent, bool slideout)
    {
        foreach (var i in items)
        {
            yield return (p, i, parent, slideout);
            foreach (var x in WalkItems(p, i.Children, i, slideout)) yield return x;
            foreach (var r in i.SubRows)
            foreach (var x in WalkItems(p, r.Items, parent, slideout || r.IsSlideout))
                yield return x;
        }
    }
}
