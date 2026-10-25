using Autodesk.Windows;

namespace Retrace.AutoCad;

public sealed record RibbonLocation(string? TabId, string? TabTitle, string? PanelId, string? PanelTitle, string? Path);

/// <summary>Item Id -> (tab, panel) map built by walking Ribbon.Tabs -> Panels -> Source.Items, recursing into
/// RibbonRowPanel (incl. fold panels) and RibbonListButton (incl. split buttons). Rebuilt lazily when the ribbon changes.</summary>
public sealed class RibbonMap
{
    readonly Dictionary<string, RibbonLocation> _map = new(StringComparer.Ordinal);
    bool _dirty = true;

    public void Invalidate() => _dirty = true;

    public RibbonLocation? Find(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        if (_dirty) Rebuild();
        return _map.TryGetValue(id, out var l) ? l : null;
    }

    public void Rebuild()
    {
        _dirty = false;
        _map.Clear();
        try
        {
            var ribbon = ComponentManager.Ribbon;
            if (ribbon == null) return;
            foreach (var tab in ribbon.Tabs)
            {
                foreach (var panel in tab.Panels)
                {
                    var src = panel.Source;
                    if (src == null) continue;
                    var ctx = new RibbonLocation(tab.Id, tab.Title, src.Id ?? panel.Id, src.Title, null);
                    Walk(src.Items, ctx, src.Title ?? "");
                    if (src.DialogLauncher != null) Add(src.DialogLauncher, ctx, "dialog launcher");
                }
            }
        }
        catch (Exception ex) { Log.Error("RibbonMap.Rebuild", ex); }
    }

    void Walk(IEnumerable<RibbonItem>? items, RibbonLocation ctx, string path)
    {
        if (items == null) return;
        foreach (var item in items)
        {
            Add(item, ctx, path);
            var label = Label(item);
            switch (item)
            {
                case RibbonRowPanel row: Walk(row.Items, ctx, path); break;           // also RibbonFoldPanel
                case RibbonListButton lb: Walk(lb.Items, ctx, path + " > " + label); break; // also RibbonSplitButton
            }
        }
    }

    void Add(RibbonItem item, RibbonLocation ctx, string path)
    {
        var id = item.Id;
        if (string.IsNullOrEmpty(id) || _map.ContainsKey(id)) return;
        _map[id] = ctx with { Path = path };
    }

    public static string Label(RibbonItem item)
    {
        var s = item.Text;
        if (string.IsNullOrWhiteSpace(s)) s = item.Name;
        if (string.IsNullOrWhiteSpace(s)) s = item.Id;
        return (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
    }
}
