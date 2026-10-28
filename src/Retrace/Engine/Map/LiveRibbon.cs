namespace Retrace.Engine.Map;

// The ribbon as AutoCAD shows it, copied out of AutoCAD on its UI thread (AutoCad/LiveRibbonReader.cs) into plain data that the engine
// reads on any thread. Ids are as the live ribbon reports them: AutoCAD puts the customization group in front of a CUIX id
// ("ACAD.ID_TabHome" for "ID_TabHome"); match them to scanned ids with Util/LiveIds.

/// <param name="Visible">the tab shows in the current workspace (hidden tabs belong to other workspaces)</param>
/// <param name="Contextual">a contextual tab (Text Editor, Array...): it shows only for a selection or a command</param>
public sealed record LiveTab(string? Id, string? Title, bool Visible, bool Contextual, IReadOnlyList<LivePanel> Panels);

/// <param name="DialogLauncher">the small arrow in the panel's title bar, when the panel has one</param>
public sealed record LivePanel(string? Id, string? Title, IReadOnlyList<LiveItem> Items, LiveItem? DialogLauncher = null);

/// <summary>One ribbon item. Containers (row panels, fold panels) are items too: their items are <paramref name="Children"/>.</summary>
/// <param name="Type">the item's .NET type name: RibbonButton, RibbonSplitButton, RibbonRowPanel, RibbonSeparator ...</param>
/// <param name="Macro">CommandParameter when it is a string: the macro a click sends. AutoCAD's own CUI buttons have none.</param>
/// <param name="ParameterType">CommandParameter's full type name when it is an object, not a string</param>
/// <param name="HandlerType">CommandHandler's full type name: the class whose Execute runs on a click</param>
/// <param name="Children">a split button's or drop-down's members, a row panel's items</param>
public sealed record LiveItem(string? Id, string? Text, string? Name, string Type, string? Macro, string? ParameterType, string? HandlerType,
    IReadOnlyList<LiveItem> Children);
