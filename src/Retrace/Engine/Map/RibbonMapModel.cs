using System.Text.Json.Serialization;

namespace Retrace.Engine.Map;

// The ribbon map: one row per ribbon item, with the code it runs found in the folders the user chose. Built by RibbonMapper, written by
// RibbonMapWriter, shown by the plugin's Map window. Plain data, safe to hand from the engine's thread to the UI thread.

public sealed class RibbonMapOptions
{
    /// <summary>The folders to read, in the user's order. Missing ones are a warning; one inside another is read once.</summary>
    public required IReadOnlyList<string> Folders { get; init; }
    /// <summary>The ribbon AutoCAD shows now (AutoCad/LiveRibbonReader); null = the live ribbon is not mapped.</summary>
    public IReadOnlyList<LiveTab>? Live { get; init; }
    /// <summary>The .cuix / .cui files AutoCAD has loaded. Read only to tell which customization file a live item comes from and which
    /// macro it runs (AutoCAD's live CUI buttons carry no macro); not listed unless the live ribbon shows them.</summary>
    public IReadOnlyList<string> Customization { get; init; } = [];
    /// <summary>List every live tab. Off: a tab only the live ribbon or a loaded customization file has is listed only when at least one of
    /// its items maps to code in the folders, so AutoCAD's own tabs stay out.</summary>
    public bool AllLiveTabs { get; init; }
    /// <summary>Where ribbon-map.md, .csv and .json are written; null = not written.</summary>
    public string? Output { get; init; }
}

public enum MapStatus
{
    /// <summary>At least one definition of what the item runs was found in the folders.</summary>
    Mapped,
    /// <summary>The item runs something (a command, a LISP function, a method...) that none of the folders defines.</summary>
    NotFound,
    /// <summary>Nothing to look up: no macro, no handler (a drop-down, a gallery, a built-in AutoCAD button the live ribbon tells nothing about).</summary>
    NoAction,
}

/// <summary>A place in a file. <paramref name="Path"/> is the full path on disk; <paramref name="Part"/> the entry inside a .cuix
/// (a zip), e.g. "RibbonRoot.cui"; <paramref name="Line"/> is 1-based, 0 when unknown. <paramref name="Folder"/> is the chosen folder the
/// file is in, null for a loaded customization file outside them.</summary>
public sealed record MapSource(string Path, int Line, string? Folder = null, string? Part = null)
{
    /// <summary>The path relative to its folder ('/'-separated), else the full path.</summary>
    [JsonIgnore] public string Short
    {
        get
        {
            if (Folder is not { Length: > 0 } f) return Path;
            var dir = f.TrimEnd('\\', '/');
            // inside the folder only at a separator: "C:\src2\a.cs" is not in "C:\src"
            if (Path.Length > dir.Length + 1 && Path.StartsWith(dir, StringComparison.OrdinalIgnoreCase) && Path[dir.Length] is '\\' or '/')
                return Path[(dir.Length + 1)..].Replace('\\', '/');
            return Path;
        }
    }

    /// <summary>"C:\src\Tools\Cmds.cs:42", "C:\src\menu.cuix!RibbonRoot.cui:120".</summary>
    public override string ToString() => Path + (Part != null ? "!" + Part : "") + (Line > 0 ? ":" + Line : "");
}

/// <summary>Code that a ribbon item runs, found in the folders.</summary>
/// <param name="What">what was looked up: "command", "lisp" (a LISP function), "method", "class", "file" (a LISP file the macro loads)</param>
/// <param name="Name">the name looked up: "DRAWGRID", "mytool", "Contoso.Ribbon.Handlers.OnGrid", "tools.lsp"</param>
/// <param name="Entry">how the code declares it: "[CommandMethod]", "&lt;CommandMethod&gt;", "[LispFunction]", "addCommand",
/// "ACED_ARXCOMMAND_ENTRY_AUTO", "defun c:", "vlax-add-cmd", "defun", "method", "class", "file"</param>
/// <param name="Symbol">the method or function as declared: "Contoso.Tools.Cmds.DrawGrid", "c:mytool", "CMyArx::cmdGrid", "tools.lsp"</param>
public sealed record MapDef(string What, string Name, Lang Lang, string Entry, string Symbol, MapSource Source, string? Project = null);

public sealed class MapRow
{
    public required string Tab { get; init; }
    public string? TabId { get; init; }
    public required string Panel { get; init; }
    /// <summary>The item's label; a member of a split button or drop-down is "Parent › Member".</summary>
    public required string Item { get; init; }
    public string? ItemId { get; init; }
    /// <summary>"button", "split button", "toggle", "drop-down", "gallery", "control", "dialog launcher", "other".</summary>
    public string Kind { get; init; } = "";
    /// <summary>Where the item comes from: "cuix" / "cui" (a file in the folders), "code" (ribbon code in the folders),
    /// "loaded cuix" (a customization file AutoCAD has loaded), "live" (only the live ribbon has it: built by code at run time).</summary>
    public required string Origin { get; init; }
    /// <summary>Where the ribbon item itself is defined: its CUIX element, or the code that builds it. Null when only the live ribbon has it.</summary>
    public MapSource? DefinedIn { get; init; }
    /// <summary>Whether the live ribbon shows the item; null when the live ribbon was not mapped.</summary>
    public bool? Live { get; set; }
    /// <summary>The macro a click sends, as written.</summary>
    public string? Macro { get; set; }
    /// <summary>What a click runs, in words: "command DRAWGRID", "LISP (mytool)", "loads tools.lsp", "method N.C.OnGrid", "class N.Handler";
    /// several are joined with ", ". Empty for NoAction.</summary>
    public string Runs { get; set; } = "";
    /// <summary>How the target was found: "macro", "command parameter", "handler object", "command handler class",
    /// "command parameter object", "button id (the handler sends it)", "button id (a guess)".</summary>
    public string? FoundBy { get; set; }
    public MapStatus Status { get; set; }
    /// <summary>Why the status is NotFound or NoAction, in words ("command DRAWX: not in these folders", "DIESEL macro: not followed"). A Mapped
    /// row names what it asked for and no folder defines ("loads x: not in these folders"); otherwise null.</summary>
    public string? Note { get; set; }
    /// <summary>Every definition found, best first (the folder order, then the file path).</summary>
    public List<MapDef> Sources { get; init; } = new();
}

public sealed class RibbonMapResult
{
    /// <summary>The folders read (full paths, in the user's order, after dropping missing and nested ones).</summary>
    public required IReadOnlyList<string> Folders { get; init; }
    /// <summary>Whether the live ribbon was part of the map.</summary>
    public bool LiveMapped { get; init; }
    public DateTimeOffset Generated { get; init; } = DateTimeOffset.Now;
    /// <summary>Tab by tab, in ribbon order: the folders' CUIX / CUI tabs, their code tabs, then tabs only the live ribbon has.</summary>
    public List<MapRow> Rows { get; init; } = new();
    /// <summary>Non-fatal problems: a missing folder, a file that could not be read, a scanner that failed.</summary>
    public List<string> Warnings { get; init; } = new();
    /// <summary>Live tabs left out because nothing in them maps to code in the folders (<see cref="RibbonMapOptions.AllLiveTabs"/> off).</summary>
    public int LiveTabsLeftOut { get; set; }
    /// <summary>Files the scan read, all folders together.</summary>
    public int FilesRead { get; set; }

    [JsonIgnore] public int Mapped => Rows.Count(r => r.Status == MapStatus.Mapped);
    [JsonIgnore] public int NotFound => Rows.Count(r => r.Status == MapStatus.NotFound);
    [JsonIgnore] public int NoAction => Rows.Count(r => r.Status == MapStatus.NoAction);
}
