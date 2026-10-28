using Autodesk.Windows;
using Retrace.Engine.Map;

namespace Retrace.AutoCad;

/// <summary>
/// Copies the ribbon AutoCAD shows now into the engine's plain <see cref="LiveTab"/> records, for the ribbon map: every tab (visible, hidden
/// and contextual ones), its panels and their items. Row panels, fold panels, split buttons and drop-downs keep their members as children,
/// the way <see cref="RibbonMap"/> walks them. Nothing is filtered here; the engine decides what to list. UI thread only: it reads WPF
/// objects. A tab, panel or item that cannot be read is logged and left out; it never stops the walk.
/// </summary>
static class LiveRibbonReader
{
    /// <summary>A ribbon nests a row panel or split button two or three deep; the limit only keeps a cycle from running away.</summary>
    const int MaxDepth = 8;

    /// <summary>The live ribbon in AutoCAD's order. Empty while the ribbon is not loaded.</summary>
    public static List<LiveTab> Snapshot()
    {
        var tabs = new List<LiveTab>();
        var ribbon = ComponentManager.Ribbon;
        if (ribbon == null) return tabs;
        foreach (var tab in ribbon.Tabs)
        {
            try { tabs.Add(ReadTab(tab)); }
            catch (Exception ex) { Log.Error("live ribbon: tab " + Describe(tab), ex); }
        }
        return tabs;
    }

    static LiveTab ReadTab(RibbonTab tab)
    {
        var panels = new List<LivePanel>();
        foreach (var panel in tab.Panels)
        {
            try { if (ReadPanel(panel) is { } p) panels.Add(p); }
            catch (Exception ex) { Log.Error($"live ribbon: a panel of tab {Describe(tab)}", ex); }
        }
        return new LiveTab(tab.Id, tab.Title, tab.IsVisible, tab.IsContextualTab, panels);
    }

    static LivePanel? ReadPanel(RibbonPanel panel)
    {
        var src = panel.Source;
        if (src == null) return null;
        LiveItem? launcher = null;
        if (src.DialogLauncher != null)
        {
            try { launcher = ReadItem(src.DialogLauncher, 0); }
            catch (Exception ex) { Log.Error("live ribbon: the dialog launcher of panel " + src.Title, ex); }
        }
        return new LivePanel(src.Id ?? panel.Id, src.Title, ReadItems(src.Items, 0), launcher);
    }

    static List<LiveItem> ReadItems(IEnumerable<RibbonItem>? items, int depth)
    {
        var list = new List<LiveItem>();
        if (items == null || depth > MaxDepth) return list;
        foreach (var item in items)
        {
            try { list.Add(ReadItem(item, depth)); }
            catch (Exception ex) { Log.Error("live ribbon: item " + Describe(item), ex); }
        }
        return list;
    }

    static LiveItem ReadItem(RibbonItem item, int depth)
    {
        string? macro = null, parameterType = null, handlerType = null;
        if (item is RibbonCommandItem cmd)
        {
            // a text parameter is a macro; an object (the Samples tab, AutoCAD's own buttons) is only named
            var parameter = cmd.CommandParameter;
            if (parameter is string s) macro = s;
            else if (parameter != null) parameterType = parameter.GetType().FullName;
            handlerType = cmd.CommandHandler?.GetType().FullName;
        }
        IEnumerable<RibbonItem>? children = item switch
        {
            RibbonRowPanel row => row.Items,         // also RibbonFoldPanel
            RibbonListButton list => list.Items,     // also RibbonSplitButton
            _ => null,
        };
        return new LiveItem(item.Id, item.Text, item.Name, item.GetType().Name, macro, parameterType, handlerType, ReadItems(children, depth + 1));
    }

    /// <summary>A name for the log; never throws, because it runs while something else already failed.</summary>
    static string Describe(object? tabOrItem)
    {
        try
        {
            return tabOrItem switch
            {
                RibbonTab t => t.Id ?? t.Title ?? "?",
                RibbonItem i => i.Id ?? i.Text ?? i.Name ?? "?",
                _ => "?",
            };
        }
        catch (Exception) { return "?"; }
    }
}
