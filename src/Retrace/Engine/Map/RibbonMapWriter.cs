using System.Globalization;
using System.Text;
using System.Text.Json;
using Retrace.Engine.Emit;

namespace Retrace.Engine.Map;

/// <summary>Writes a <see cref="RibbonMapResult"/> as ribbon-map.md (to read), ribbon-map.csv (for Excel) and ribbon-map.json (for tools),
/// each to a temp file first and then moved over the old one. The format is described in docs/ribbon-map.md.</summary>
public static class RibbonMapWriter
{
    public const string MarkdownFile = "ribbon-map.md";
    public const string CsvFile = "ribbon-map.csv";
    public const string JsonFile = "ribbon-map.json";

    /// <summary>Writes the three files into <paramref name="outDir"/> (created if needed) and returns their full paths. Throws IOException /
    /// UnauthorizedAccessException when the folder cannot be written.</summary>
    public static IReadOnlyList<string> Write(RibbonMapResult map, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var md = Path.Combine(outDir, MarkdownFile);
        var csv = Path.Combine(outDir, CsvFile);
        var json = Path.Combine(outDir, JsonFile);
        WriteFile(md, Markdown(map), new UTF8Encoding(false));
        WriteFile(csv, Csv(map), new UTF8Encoding(true));   // the BOM tells Excel it is UTF-8
        WriteFile(json, Json(map), new UTF8Encoding(false));
        return [md, csv, json];
    }

    /// <summary>A reader (Excel, an editor) must never see half a file: write beside it, then move over it.</summary>
    private static void WriteFile(string path, string text, Encoding encoding)
    {
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, text, encoding);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception)
        {
            try { File.Delete(tmp); } catch (Exception) { }
            throw;
        }
    }

    /// <summary>The name of a language in the map: C#, VB.NET, C++, LISP, CUIX.</summary>
    public static string LanguageName(Lang lang) => lang switch
    {
        Lang.CSharp => "C#",
        Lang.VbNet => "VB.NET",
        Lang.Cpp => "C++",
        Lang.Lisp => "LISP",
        Lang.Cuix => "CUIX",
        _ => lang.ToString(),
    };

    /// <summary>"Mapped", "Not found", "No action".</summary>
    public static string StatusName(MapStatus status) => status switch
    {
        MapStatus.Mapped => "Mapped",
        MapStatus.NotFound => "Not found",
        _ => "No action",
    };

    /// <summary>A place for people: relative to its folder, "menu/a.cuix!RibbonRoot.cui:120".</summary>
    private static string Where(MapSource s) => s.Short + (s.Part != null ? "!" + s.Part : "") + (s.Line > 0 ? ":" + s.Line : "");

    // ------------------------------------------------------------ markdown

    private static string Markdown(RibbonMapResult map)
    {
        var sb = new StringBuilder();
        sb.Append("# Ribbon map\n\n");
        var from = map.Folders.Count == 1 ? "1 folder" : $"{map.Folders.Count} folders";
        sb.Append("Made ").Append(map.Generated.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(" from ").Append(from)
          .Append(map.LiveMapped ? " and the ribbon AutoCAD showed" : "").Append(".\n\n");

        sb.Append(Md.Table(["What", "Count"],
        [
            ["Ribbon items", map.Rows.Count.ToString(CultureInfo.InvariantCulture)],
            ["Mapped to code", map.Mapped.ToString(CultureInfo.InvariantCulture)],
            ["Not found in the folders", map.NotFound.ToString(CultureInfo.InvariantCulture)],
            ["No action", map.NoAction.ToString(CultureInfo.InvariantCulture)],
            ["Files read", map.FilesRead.ToString(CultureInfo.InvariantCulture)],
            ["Live tabs left out", map.LiveTabsLeftOut.ToString(CultureInfo.InvariantCulture)],
        ]));

        sb.Append("\nFolders read:\n\n");
        foreach (var f in map.Folders) sb.Append("- ").Append(Md.Code(f)).Append('\n');

        // a section per tab: the rows come tab by tab
        foreach (var tab in Sections(map.Rows))
        {
            sb.Append("\n## ").Append(OneLine(tab.Title) is { Length: > 0 } title ? title : "(no title)").Append("\n\n").Append(tab.Origin).Append("\n\n");
            sb.Append(Md.Table(["Panel", "Item", "Runs", "Code", "Found by"], tab.Rows.Select(r => new[] { r.Panel, r.Item, r.Runs, CodeCell(r), r.FoundBy })));
        }

        if (map.Warnings.Count > 0)
        {
            sb.Append("\n## Warnings\n\n");
            foreach (var w in map.Warnings) sb.Append("- ").Append(OneLine(w)).Append('\n');
        }
        return sb.ToString();
    }

    private sealed record Section(string Title, string Origin, List<MapRow> Rows);

    /// <summary>Runs of rows of one tab, from one place: a .cuix file, code, a loaded file, the live ribbon.</summary>
    private static List<Section> Sections(IEnumerable<MapRow> rows)
    {
        var list = new List<Section>();
        string? key = null;
        foreach (var r in rows)
        {
            var origin = r.Origin switch
            {
                "code" => "Built in code.",
                "live" => "Live ribbon.",
                _ => r.DefinedIn is { } d ? $"From {Md.Code(d.Short)}" + (r.Origin == "loaded cuix" ? " (loaded in AutoCAD)." : ".") : "From a customization file.",
            };
            // the same tab name in two files is two sections
            var k = $"{r.Tab}\u0001{r.TabId}\u0001{origin}";
            if (k != key)
            {
                list.Add(new Section(r.Tab, origin, new List<MapRow>()));
                key = k;
            }
            list[^1].Rows.Add(r);
        }
        return list;
    }

    private static string OneLine(string s) => s.Replace("\r", "").Replace("\n", " ").Trim();

    private static string CodeCell(MapRow r)
    {
        if (r.Sources.Count > 0)
        {
            var d = r.Sources[0];
            var more = r.Sources.Count > 1 ? $" +{r.Sources.Count - 1} more" : "";
            return $"{LanguageName(d.Lang)} {Md.Code(d.Symbol)} in {Md.Code(Where(d.Source))}{more}";
        }
        return r.Status switch
        {
            MapStatus.NotFound => "not found: " + r.Note,
            MapStatus.NoAction when r.Note != null => "no action: " + r.Note,   // e.g. "DIESEL macro: not followed"
            _ => "-",
        };
    }

    // ------------------------------------------------------------ csv

    private static readonly string[] CsvHeader =
    [
        "Tab", "Panel", "Item", "Item id", "Kind", "Origin", "Defined in", "Shown in AutoCAD", "Macro", "Runs", "Status",
        "Language", "Code", "File", "Line", "More code", "Found by", "Note",
    ];

    /// <summary>UTF-8 with a BOM, CRLF line ends, RFC 4180 quoting. One row per ribbon item; the first code found is in the Language, Code,
    /// File and Line columns, the others in More code.</summary>
    private static string Csv(RibbonMapResult map)
    {
        var sb = new StringBuilder();
        void Line(IEnumerable<string?> cells) => sb.Append(string.Join(",", cells.Select(Field))).Append("\r\n");
        Line(CsvHeader);
        foreach (var r in map.Rows)
        {
            var first = r.Sources.Count > 0 ? r.Sources[0] : null;
            Line(
            [
                r.Tab, r.Panel, r.Item, r.ItemId, r.Kind, r.Origin, r.DefinedIn?.ToString(),
                r.Live switch { true => "yes", false => "no", _ => "" },
                r.Macro, r.Runs, StatusName(r.Status),
                first == null ? "" : LanguageName(first.Lang), first?.Symbol, first?.Source.Path,
                first is { Source.Line: > 0 } ? first.Source.Line.ToString(CultureInfo.InvariantCulture) : "",
                string.Join(" | ", r.Sources.Skip(1).Select(d => $"{LanguageName(d.Lang)} {d.Symbol} ({d.Source})")),
                r.FoundBy, r.Note,
            ]);
        }
        return sb.ToString();
    }

    /// <summary>A CSV field. Excel and Calc run a cell that starts with = + - @ (or a tab or CR) as a formula, so such a cell gets a leading
    /// apostrophe (OWASP's advice); then RFC 4180 quoting for commas, quotes and line breaks.</summary>
    private static string Field(string? value)
    {
        var s = value ?? "";
        if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r') s = "'" + s;
        return s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    // ------------------------------------------------------------ json

    private static string Json(RibbonMapResult map) => JsonSerializer.Serialize(new
    {
        schema = "retrace/ribbon-map@1",
        generated = map.Generated,
        folders = map.Folders,
        liveMapped = map.LiveMapped,
        liveTabsLeftOut = map.LiveTabsLeftOut,
        filesRead = map.FilesRead,
        rows = map.Rows,
        warnings = map.Warnings,
    }, SourceMapWriter.Json);
}
