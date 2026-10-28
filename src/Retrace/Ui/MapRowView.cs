using Retrace.Engine;
using Retrace.Engine.Map;

namespace Retrace.Ui;

// NO WPF references in this file: the text the Map window shows for a row and for its status line. The window binds to the public properties.

/// <summary>One row of the Map window's grid: a <see cref="MapRow"/> with the strings the grid shows, the tooltips, the text that
/// "Copy row" puts on the clipboard, and what the filter box searches.</summary>
sealed class MapRowView
{
    const int TipMax = 240;

    readonly string _haystack;

    public MapRowView(MapRow row)
    {
        Row = row;
        var sources = row.Sources;
        Code = sources.Count == 0 ? "" : Describe(sources[0]) + (sources.Count > 1 ? $"  +{sources.Count - 1} more" : "");
        File = sources.Count == 0 ? "" : Place(sources[0].Source);
        StatusText = RibbonMapWriter.StatusName(row.Status);
        FirstSource = sources.Count == 0 ? null : sources[0].Source;

        ItemTip = ItemTipFor(row);
        CodeTip = sources.Count == 0 ? null : string.Join("\n", sources.Select(s => $"{RibbonMapWriter.LanguageName(s.Lang)}  {Symbol(s)}   ({s.Entry})"));
        FileTip = sources.Count == 0 ? null : string.Join("\n", sources.Select(s => s.Source.ToString())) + (sources.Count > 1 ? "\nOpen code opens the first one." : "");
        StatusTip = string.IsNullOrWhiteSpace(row.Note) ? null : Clip(row.Note);

        var text = new List<string?> { row.Tab, row.Panel, row.Item, row.ItemId, row.Macro, row.Runs, row.FoundBy, StatusText, row.Note };
        foreach (var s in sources) { text.Add(s.Name); text.Add(s.Symbol); text.Add(s.Entry); text.Add(s.Source.Short); text.Add(RibbonMapWriter.LanguageName(s.Lang)); }
        _haystack = string.Join("\n", text.Where(t => !string.IsNullOrEmpty(t))).ToLowerInvariant();
    }

    public MapRow Row { get; }

    // ---- the grid's columns ---------------------------------------------------------------------------------------------------

    public string Tab => Row.Tab;
    public string Panel => Row.Panel;
    public string Item => Row.Item;
    public string Runs => Row.Runs;
    /// <summary>Language and symbol of the first definition ("C#  Contoso.Tools.Cmds.DrawGrid"), "+2 more" when there are more.</summary>
    public string Code { get; }
    /// <summary>The first definition's file relative to its folder, and its line ("Tools/Cmds.cs:42").</summary>
    public string File { get; }
    public string FoundBy => Row.FoundBy ?? "";
    public MapStatus Status => Row.Status;
    public string StatusText { get; }

    // ---- tooltips (null = none) -----------------------------------------------------------------------------------------------

    public string ItemTip { get; }
    public string? CodeTip { get; }
    public string? FileTip { get; }
    public string? StatusTip { get; }

    /// <summary>Where "Open code" goes: the first definition found, null when none.</summary>
    public MapSource? FirstSource { get; }

    // ---- filter and copy ------------------------------------------------------------------------------------------------------

    /// <summary>True when every word is somewhere in the row: its tab, panel, item, id, macro, what it runs, how it was found, its status
    /// or note, or the name, symbol, entry, language or file of any definition. Case does not matter. No words matches every row.</summary>
    public bool Matches(string[] words) => words.All(w => _haystack.Contains(w, StringComparison.Ordinal));

    /// <summary>The row as one line of tab-separated text for a spreadsheet: tab, panel, item, runs, all code, all files, found by, status, note.</summary>
    public string CopyText()
    {
        var code = string.Join(" | ", Row.Sources.Select(s => $"{RibbonMapWriter.LanguageName(s.Lang)} {Symbol(s)}"));
        var files = string.Join(" | ", Row.Sources.Select(s => s.Source.ToString()));
        return string.Join("\t", new[] { Tab, Panel, Item, Runs, code, files, FoundBy, StatusText, Row.Note ?? "" }.Select(Flat));
    }

    // ---- helpers --------------------------------------------------------------------------------------------------------------

    static string Symbol(MapDef d) => d.Symbol.Length > 0 ? d.Symbol : d.Name;
    static string Describe(MapDef d) => $"{RibbonMapWriter.LanguageName(d.Lang)}  {Symbol(d)}";
    static string Place(MapSource s) => s.Short + (s.Line > 0 ? ":" + s.Line : "");
    /// <summary>One cell of the copied line: no tabs or line breaks inside.</summary>
    static string Flat(string s) => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    static string Clip(string s) => s.Length <= TipMax ? s : s[..(TipMax - 3)] + "...";

    static string ItemTipFor(MapRow row)
    {
        var lines = new List<string> { Clip(row.Item) };
        if (!string.IsNullOrEmpty(row.ItemId)) lines.Add("id: " + row.ItemId);
        if (row.Kind.Length > 0) lines.Add("kind: " + row.Kind);
        lines.Add("from: " + OriginText(row.Origin));
        if (row.DefinedIn != null) lines.Add("defined in: " + row.DefinedIn);
        if (row.Live is bool live) lines.Add(live ? "AutoCAD shows it now" : "AutoCAD does not show it now");
        if (!string.IsNullOrWhiteSpace(row.Macro)) lines.Add("macro: " + Clip(row.Macro.Trim()));
        return string.Join("\n", lines);
    }

    static string OriginText(string origin) => origin switch
    {
        "cuix" => "a CUIX file in the folders",
        "cui" => "a CUI file in the folders",
        "code" => "ribbon code in the folders",
        "loaded cuix" => "a CUIX file AutoCAD has loaded",
        "live" => "the live ribbon only (built in code when AutoCAD runs)",
        _ => origin,
    };
}

/// <summary>The words of the Map window that do not belong to one row.</summary>
static class MapText
{
    /// <summary>"1 folder", "3 folders".</summary>
    public static string Plural(int n, string one) => $"{n} {(n == 1 ? one : one + "s")}";

    /// <summary>The filter box's text as words (split at white space), lower case; empty = no filter.</summary>
    public static string[] Words(string? filter) =>
        (filter ?? "").ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The status line before a map runs.</summary>
    public static string IdleStatus(int folders, int ticked) =>
        folders == 0 ? "Add the folders with your source, then Map."
        : ticked == 0 ? "Tick at least one folder, then Map."
        : $"Press Map to read {Plural(ticked, "folder")}.";

    /// <summary>The status line after a map: "142 ribbon items: 97 mapped to code, 41 not found, 4 with no action. 2 warnings."</summary>
    public static string Summary(RibbonMapResult map, int warnings)
    {
        if (map.Rows.Count == 0)
            return "No ribbon items found. The folders need .cuix or .cui files, or ribbon code in C# or VB.NET." + (warnings > 0 ? $" {Plural(warnings, "warning")}." : "");
        var s = $"{Plural(map.Rows.Count, "ribbon item")}: {map.Mapped} mapped to code, {map.NotFound} not found, {map.NoAction} with no action.";
        if (map.LiveTabsLeftOut > 0) s += $" {Plural(map.LiveTabsLeftOut, "live tab")} left out (option \"List every live tab\").";
        if (warnings > 0) s += $" {Plural(warnings, "warning")}.";
        return s;
    }
}
