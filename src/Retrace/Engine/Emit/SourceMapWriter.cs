// sourcemap.json: one entry per UI element, the input for a screenshot capture tool.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Retrace.Engine.Emit;

public sealed class SourceMapEntry
{
    public required string Id { get; init; }
    /// <summary>ribbon-tab | ribbon-panel | ribbon-item | command | form</summary>
    public required string Kind { get; init; }
    public required string Title { get; init; }
    public string? Parent { get; init; }
    public string? Project { get; init; }
    public SourceRef? Source { get; init; }
    /// <summary>Reference page (and anchor) relative to the docs folder.</summary>
    public string? Doc { get; init; }
    /// <summary>Wireframe SVG relative to the docs folder.</summary>
    public string? Asset { get; init; }
    /// <summary>id attribute of this element inside the asset SVG.</summary>
    public string? AssetElement { get; init; }
    public string? Command { get; init; }
    public string? Handler { get; init; }
    /// <summary>Steps a person or a UI automation script takes to bring this element on screen.</summary>
    public List<string> Reach { get; init; } = new();
    /// <summary>What to capture once reached.</summary>
    public Dictionary<string, string> Capture { get; init; } = new();
    /// <summary>Source-map ids this element leads to (button -> command, command -> forms).</summary>
    public List<string> Opens { get; init; } = new();
    public List<string> OpenedBy { get; init; } = new();
    public List<string>? Tests { get; init; }
}

public sealed class SourceMapWriter(ModelIndex ix, OutputSink sink)
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public List<SourceMapEntry> Build()
    {
        var list = new List<SourceMapEntry>();
        var m = ix.Model;

        foreach (var tab in ix.AllTabs)
        {
            var tabId = ModelIndex.TabId(tab);
            list.Add(new SourceMapEntry
            {
                Id = tabId, Kind = "ribbon-tab", Title = tab.Title,
                Project = tab.Origin == RibbonOrigin.Code ? tab.Container : null,
                Source = tab.Source, Doc = ix.TabDoc[tab], Asset = ix.TabAsset[tab], AssetElement = SvgIds.Tab(tab),
                Reach = ix.ReachTab(tab),
                Capture = new() { ["type"] = "ribbon-tab", ["tab"] = tab.Title, ["tabId"] = tab.Id },
                Tests = new() { $"{ManualTestWriter.RibbonTestDoc(ix, tab)}" },
            });
            foreach (var p in tab.Panels)
                list.Add(new SourceMapEntry
                {
                    Id = ModelIndex.PanelId(tab, p), Kind = "ribbon-panel", Title = p.Title, Parent = tabId,
                    Source = p.Source, Doc = ix.TabDoc[tab], Asset = ix.TabAsset[tab], AssetElement = SvgIds.Panel(p),
                    Command = p.DialogLauncherCommand,
                    Reach = ix.ReachTab(tab).Append($"Find the '{p.Title}' panel").ToList(),
                    Capture = new() { ["type"] = "ribbon-panel", ["tab"] = tab.Title, ["panel"] = p.Title },
                });
            foreach (var (panel, item, parent, slide) in Linker.Walk(tab))
            {
                if (item.Kind == RibbonItemKind.Separator) continue;
                var hit = new RibbonHit(tab, panel, item, parent, slide);
                list.Add(new SourceMapEntry
                {
                    Id = ModelIndex.ItemId(tab, panel, item), Kind = "ribbon-item", Title = ModelIndex.Label(item),
                    Parent = parent != null ? ModelIndex.ItemId(tab, panel, parent) : ModelIndex.PanelId(tab, panel),
                    Source = item.Source, Doc = ix.TabDoc[tab], Asset = ix.TabAsset[tab], AssetElement = SvgIds.Item(item),
                    Command = item.Command, Handler = item.HandlerMethod,
                    Reach = ix.ReachItem(hit),
                    Capture = new()
                    {
                        ["type"] = "ribbon-item", ["tab"] = tab.Title, ["panel"] = panel.Title, ["item"] = ModelIndex.Label(item),
                        ["tooltipHover"] = item.Tooltip != null ? "true" : "false",
                    },
                    Opens = ix.EntriesFor(item).Select(ModelIndex.CommandId).ToList(),
                });
            }
        }

        foreach (var c in m.Commands)
        {
            var forms = c.Flow.Where(s => s.UiFormId != null).Select(s => s.UiFormId!).Distinct().ToList();
            list.Add(new SourceMapEntry
            {
                Id = ModelIndex.CommandId(c), Kind = "command", Title = c.Name, Project = c.Project,
                Source = c.Source, Doc = $"{ix.CommandDoc[c.Project]}#{ix.CommandAnchor[c]}",
                Command = c.Kind == EntryKind.RibbonHandler ? null : c.Name, Handler = c.Method,
                Reach = ix.ReachCommand(c),
                Capture = new() { ["type"] = "command-line", ["command"] = c.Name, ["prompts"] = c.Flow.Count(s => s.Kind is FlowKind.Prompt or FlowKind.Selection).ToString() },
                Opens = forms.Select(f => ModelIndex.FormId(ix.FormsById[f])).ToList(),
                OpenedBy = ix.RibbonFor(c)
                    .Select(h => ModelIndex.ItemId(h.Tab, h.Panel, h.Item)).ToList(),
                Tests = new() { $"{ManualTestWriter.CommandTestDoc(c.Project)}#{ix.TestId[c].ToLowerInvariant()}" },
            });
        }

        foreach (var f in m.Forms)
            list.Add(new SourceMapEntry
            {
                Id = ModelIndex.FormId(f), Kind = "form", Title = f.Title, Project = f.Project,
                Source = f.Source, Doc = ix.FormDoc[f], Asset = ix.FormAsset[f], AssetElement = SvgIds.Form(f),
                Reach = ix.ReachForm(f),
                Capture = new()
                {
                    ["type"] = f.Kind == UiFormKind.PaletteSet ? "palette" : "window",
                    ["windowTitle"] = f.Title,
                    ["className"] = f.ClassNames.FirstOrDefault() ?? "",
                    ["resourceId"] = f.ResourceId ?? "",
                    ["formKind"] = f.Kind.ToString(),
                },
                OpenedBy = ix.OpenersByForm[f.Id].Select(o => ModelIndex.CommandId(o.Command)).Distinct().ToList(),
                Tests = new() { ManualTestWriter.FormTestDoc(f.Project) },
            });
        return list;
    }

    public void Write()
    {
        var doc = new
        {
            schema = "retrace/sourcemap@1",
            input = ix.Model.InputRoot,
            generated = ix.Model.Generated,
            note = "Assets are wireframes drawn from source. 'reach' + 'capture' describe how to take the real screenshot in AutoCAD.",
            entries = Build(),
        };
        sink.Write(DocsLayout.SourceMapFile, JsonSerializer.Serialize(doc, Json));
    }
}

/// <summary>Element ids the renderers should put on SVG groups, so the source map can point inside an asset.
/// Renderers may use other ids; then AssetElement simply won't match.</summary>
public static class SvgIds
{
    /// <summary>Same scheme as the renderers' id allocators (first occurrence; repeats get "-2", "-3").</summary>
    public static string Of(string prefix, string raw) =>
        prefix + Rx.Replace(raw ?? "", @"[^\w.-]+", "_");

    public static string Tab(RibbonTab t) => Of("tab-", t.Id);
    public static string Panel(RibbonPanel p) => Of("panel-", p.Id);
    public static string Item(RibbonItem i) => Of("item-", i.Id);
    public static string Form(UiForm f) => Of("form-", f.Id);
}
