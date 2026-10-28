using System.Text;
using System.Text.Json;
using Retrace.Engine.Map;

namespace Retrace.Tests;

/// <summary>The three map files, written from a hand-built map: quoting, escaping, the order of things.</summary>
public class RibbonMapWriterTests : IDisposable
{
    private readonly string _out = Path.Combine(Directory.CreateTempSubdirectory("retrace-mapwrite").FullName, "map");
    public void Dispose() { try { Directory.Delete(Path.GetDirectoryName(_out)!, true); } catch (IOException) { } }

    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "proj");

    private static MapSource At(string rel, int line, string? part = null) => new(Path.Combine(Folder, rel), line, Folder, part);

    /// <param name="more">sets what can be set after the row is made; the tab id and where it is defined are given here</param>
    /// <param name="definedIn">null: the default place, a cuix element</param>
    /// <param name="unplaced">no DefinedIn at all (a live-only item)</param>
    private static MapRow Row(string tab, string item, MapStatus status, Action<MapRow>? more = null, string origin = "cuix", MapSource? definedIn = null,
        bool unplaced = false, string? tabId = "auto")
    {
        var r = new MapRow
        {
            Tab = tab, TabId = tabId == "auto" ? "ID_" + tab : tabId, Panel = "Panel", Item = item, ItemId = "id_" + item, Kind = "button", Origin = origin,
            DefinedIn = unplaced ? null : definedIn ?? At("menu/a.cuix", 12, "RibbonRoot.cui"), Live = null, Macro = "^C^C_x", Runs = "command X", FoundBy = "macro",
            Status = status,
        };
        more?.Invoke(r);
        return r;
    }

    private RibbonMapResult Map(params MapRow[] rows)
    {
        var m = new RibbonMapResult { Folders = [Folder], FilesRead = 3, LiveMapped = false, Generated = new DateTimeOffset(2026, 10, 28, 14, 5, 0, TimeSpan.Zero) };
        m.Rows.AddRange(rows);
        return m;
    }

    private string Csv(RibbonMapResult map) => Text(RibbonMapWriter.Write(map, _out)[1]);

    private static string Text(string path) => File.ReadAllText(path, Encoding.UTF8).TrimStart('﻿');

    [Fact]
    public void The_files_are_written_whole_beside_the_old_ones_and_replace_them()
    {
        Directory.CreateDirectory(_out);
        File.WriteAllText(Path.Combine(_out, "ribbon-map.md"), "OLD");
        var paths = RibbonMapWriter.Write(Map(Row("T", "I", MapStatus.NoAction)), _out);
        Assert.Equal(new[] { "ribbon-map.md", "ribbon-map.csv", "ribbon-map.json" }, paths.Select(Path.GetFileName));
        Assert.DoesNotContain("OLD", File.ReadAllText(paths[0]));
        Assert.Empty(Directory.GetFiles(_out, "*.tmp"));
        Assert.Equal(new[] { 0xEF, 0xBB, 0xBF }, File.ReadAllBytes(paths[1]).Take(3).Select(b => (int)b));
        Assert.NotEqual(0xEF, File.ReadAllBytes(paths[0])[0]);   // markdown and json have no BOM
        Assert.NotEqual(0xEF, File.ReadAllBytes(paths[2])[0]);
    }

    [Fact]
    public void A_folder_that_cannot_be_made_is_an_io_error()
    {
        var blocker = Path.Combine(Path.GetDirectoryName(_out)!, "file");
        File.WriteAllText(blocker, "x");
        Assert.ThrowsAny<IOException>(() => RibbonMapWriter.Write(Map(), Path.Combine(blocker, "map")));
    }

    // ---- csv ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_csv_has_the_header_crlf_line_ends_and_one_row_per_item()
    {
        var csv = Csv(Map(Row("T", "A", MapStatus.NoAction), Row("T", "B", MapStatus.NoAction)));
        var lines = csv.Split("\r\n");
        Assert.Equal("Tab,Panel,Item,Item id,Kind,Origin,Defined in,Shown in AutoCAD,Macro,Runs,Status,Language,Code,File,Line,More code,Found by,Note", lines[0]);
        Assert.Equal(new[] { "", }, lines[3..]);   // CRLF after the last row too
        Assert.DoesNotContain("\n", csv.Replace("\r\n", ""));
    }

    [Fact]
    public void Fields_with_commas_quotes_and_line_breaks_are_quoted_the_way_rfc_4180_says()
    {
        var csv = Csv(Map(Row("T", "Say \"hi\", then\nleave", MapStatus.NoAction, r => r.Note = "a,b")));
        Assert.Contains("\"Say \"\"hi\"\", then\nleave\"", csv);
        Assert.Contains(",\"a,b\"\r\n", csv);
        var back = ParseCsv(csv);
        Assert.Equal("Say \"hi\", then\nleave", back[1][2]);
        Assert.Equal("a,b", back[1][17]);
    }

    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+cmd", "'+cmd")]
    [InlineData("-layer", "'-layer")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\tx", "'\tx")]
    [InlineData("normal", "normal")]
    [InlineData("a=b", "a=b")]
    public void A_cell_that_would_run_as_a_formula_gets_a_leading_apostrophe(string value, string expected)
    {
        var back = ParseCsv(Csv(Map(Row("T", "I", MapStatus.NoAction, r => { r.Macro = value; r.Note = value; }))));
        Assert.Equal(expected, back[1][8]);    // Macro
        Assert.Equal(expected, back[1][17]);   // Note
    }

    [Fact]
    public void A_cell_starting_with_a_carriage_return_is_marked_too()
    {
        var back = ParseCsv(Csv(Map(Row("T", "I", MapStatus.NoAction, r => r.Note = "\rx"))));
        Assert.Equal("'\rx", back[1][17]);
    }

    [Fact]
    public void The_first_code_found_has_its_own_columns_and_the_others_go_in_more_code()
    {
        var row = Row("T", "I", MapStatus.Mapped, r =>
        {
            r.Live = true;
            r.Sources.Add(new MapDef("command", "X", Lang.CSharp, "[CommandMethod]", "N.C.X", At("Net/C.cs", 42), "Net"));
            r.Sources.Add(new MapDef("command", "X", Lang.Lisp, "defun c:", "c:x", At("lisp/x.lsp", 3)));
            r.Sources.Add(new MapDef("class", "N.H", Lang.VbNet, "class", "N.H.Execute", At("Vb/H.vb", 0)));
        });
        var cells = ParseCsv(Csv(Map(row)))[1];
        Assert.Equal(("yes", "Mapped", "C#", "N.C.X"), (cells[7], cells[10], cells[11], cells[12]));
        Assert.Equal((Path.Combine(Folder, "Net", "C.cs"), "42"), (cells[13], cells[14]));
        Assert.Equal($"LISP c:x ({Path.Combine(Folder, "lisp", "x.lsp")}:3) | VB.NET N.H.Execute ({Path.Combine(Folder, "Vb", "H.vb")})", cells[15]);
        Assert.Equal(Path.Combine(Folder, "menu", "a.cuix") + "!RibbonRoot.cui:12", cells[6]);
    }

    [Fact]
    public void Shown_in_autocad_is_yes_no_or_blank_when_the_live_ribbon_was_not_mapped()
    {
        var csv = ParseCsv(Csv(Map(Row("T", "A", MapStatus.NoAction, r => r.Live = true), Row("T", "B", MapStatus.NoAction, r => r.Live = false), Row("T", "C", MapStatus.NoAction))));
        Assert.Equal(new[] { "yes", "no", "" }, csv.Skip(1).Select(c => c[7]));
    }

    [Fact]
    public void Status_is_written_in_words()
    {
        var csv = ParseCsv(Csv(Map(Row("T", "A", MapStatus.Mapped), Row("T", "B", MapStatus.NotFound), Row("T", "C", MapStatus.NoAction))));
        Assert.Equal(new[] { "Mapped", "Not found", "No action" }, csv.Skip(1).Select(c => c[10]));
    }

    // ---- json ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_json_has_the_schema_the_counts_and_camel_case_names_and_leaves_nulls_out()
    {
        var map = Map(Row("T", "I", MapStatus.NotFound, r => r.Note = "gone", tabId: null));
        map.Warnings.Add("careful");
        map.LiveTabsLeftOut = 2;
        using var doc = JsonDocument.Parse(Text(RibbonMapWriter.Write(map, _out)[2]));
        var root = doc.RootElement;
        Assert.Equal("retrace/ribbon-map@1", root.GetProperty("schema").GetString());
        Assert.Equal(new[] { Folder }, root.GetProperty("folders").EnumerateArray().Select(f => f.GetString()));
        Assert.False(root.GetProperty("liveMapped").GetBoolean());
        Assert.Equal(2, root.GetProperty("liveTabsLeftOut").GetInt32());
        Assert.Equal(3, root.GetProperty("filesRead").GetInt32());
        Assert.Equal("careful", root.GetProperty("warnings")[0].GetString());
        Assert.StartsWith("2026-10-28T14:05:00", root.GetProperty("generated").GetString());
        var row = root.GetProperty("rows")[0];
        Assert.Equal("notFound", row.GetProperty("status").GetString());
        Assert.Equal("RibbonRoot.cui", row.GetProperty("definedIn").GetProperty("part").GetString());
        Assert.False(row.TryGetProperty("live", out _));
        Assert.False(row.TryGetProperty("tabId", out _));
        Assert.False(row.GetProperty("definedIn").TryGetProperty("short", out _));
    }

    [Fact]
    public void Languages_in_the_json_are_camel_case_words_and_sources_keep_their_full_path()
    {
        var row = Row("T", "I", MapStatus.Mapped, r => r.Sources.Add(new MapDef("method", "N.C.M", Lang.VbNet, "method", "N.C.M", At("Vb/C.vb", 5), "P")));
        using var doc = JsonDocument.Parse(Text(RibbonMapWriter.Write(Map(row), _out)[2]));
        var def = doc.RootElement.GetProperty("rows")[0].GetProperty("sources")[0];
        Assert.Equal("vbNet", def.GetProperty("lang").GetString());
        Assert.Equal(Path.Combine(Folder, "Vb", "C.vb"), def.GetProperty("source").GetProperty("path").GetString());
        Assert.Equal(5, def.GetProperty("source").GetProperty("line").GetInt32());
    }

    [Fact]
    public void Non_ascii_text_is_not_escaped_in_the_json()
    {
        var text = Text(RibbonMapWriter.Write(Map(Row("Zeichnen", "Größe <&>", MapStatus.NoAction)), _out)[2]);
        Assert.Contains("Größe <&>", text);
    }

    // ---- markdown -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_report_says_when_and_from_what_and_counts_the_rows()
    {
        var map = Map(Row("T", "A", MapStatus.Mapped, r => r.Sources.Add(new MapDef("command", "X", Lang.CSharp, "[CommandMethod]", "N.C.X", At("Tools/Cmds.cs", 42)))),
            Row("T", "B", MapStatus.NotFound, r => r.Note = "command X: not in these folders"),
            Row("T", "C", MapStatus.NoAction));
        var md = Text(RibbonMapWriter.Write(new RibbonMapResult
        {
            Folders = [Folder, Path.Combine(Folder, "b"), Path.Combine(Folder, "c")], LiveMapped = true, FilesRead = 7, LiveTabsLeftOut = 1,
            Rows = map.Rows, Generated = map.Generated, Warnings = { "w1" },
        }, _out)[0]);
        Assert.StartsWith("# Ribbon map\n\nMade 2026-10-28 14:05 from 3 folders and the ribbon AutoCAD showed.\n", md);
        Assert.Contains("| Ribbon items | 3 |", md);
        Assert.Contains("| Mapped to code | 1 |", md);
        Assert.Contains("| Not found in the folders | 1 |", md);
        Assert.Contains("| No action | 1 |", md);
        Assert.Contains("| Files read | 7 |", md);
        Assert.Contains("| Live tabs left out | 1 |", md);
        Assert.Contains($"- `{Folder}`", md);
        Assert.Contains("| Panel | Item | Runs | Code | Found by |", md);
        Assert.Contains("| Panel | A | command X | C# `N.C.X` in `Tools/Cmds.cs:42` | macro |", md);
        Assert.Contains("| Panel | B | command X | not found: command X: not in these folders | macro |", md);
        Assert.Contains("| Panel | C | command X | - | macro |", md);
        Assert.EndsWith("## Warnings\n\n- w1\n", md);
    }

    [Fact]
    public void One_folder_and_no_live_ribbon_read_as_one_folder_and_nothing_more_and_no_warnings_section_without_warnings()
    {
        var md = Text(RibbonMapWriter.Write(Map(), _out)[0]);
        Assert.Contains("from 1 folder.", md);
        Assert.DoesNotContain("AutoCAD showed", md);
        Assert.DoesNotContain("## Warnings", md);
    }

    [Fact]
    public void More_sources_than_one_are_counted_after_the_first()
    {
        var row = Row("T", "A", MapStatus.Mapped, r =>
        {
            r.Sources.Add(new MapDef("command", "X", Lang.Cpp, "addCommand", "cmdX", At("a.cpp", 1)));
            r.Sources.Add(new MapDef("command", "X", Lang.Cpp, "addCommand", "cmdX", At("b.cpp", 1)));
            r.Sources.Add(new MapDef("command", "X", Lang.Cpp, "addCommand", "cmdX", At("c.cpp", 1)));
        });
        Assert.Contains("C++ `cmdX` in `a.cpp:1` +2 more", Text(RibbonMapWriter.Write(Map(row), _out)[0]));
    }

    [Fact]
    public void Pipes_and_line_breaks_in_cells_cannot_break_the_table()
    {
        var md = Text(RibbonMapWriter.Write(Map(Row("T", "a | b\nc", MapStatus.NotFound, r => { r.Runs = "x|y"; r.Note = "p|q\r\nr"; })), _out)[0]);
        Assert.Contains("| Panel | a \\| b c | x\\|y | not found: p\\|q r | macro |", md);
    }

    [Fact]
    public void Each_tab_is_a_section_that_says_where_it_comes_from_and_the_same_name_in_two_files_is_two_sections()
    {
        string Section(RibbonMapResult m) => Text(RibbonMapWriter.Write(m, _out)[0]);
        var a = Row("Home", "A", MapStatus.NoAction, definedIn: At("one/a.cuix", 3, "RibbonRoot.cui"));
        var a2 = Row("Home", "A2", MapStatus.NoAction, definedIn: At("one/a.cuix", 9, "RibbonRoot.cui"));
        var b = Row("Home", "B", MapStatus.NoAction, origin: "cui", definedIn: At("two/b.cui", 4));
        var code = Row("Code", "C", MapStatus.NoAction, origin: "code", definedIn: At("Net/Tab.cs", 20));
        var loaded = Row("Draw", "L", MapStatus.NoAction, origin: "loaded cuix", definedIn: new MapSource("/acad/acad.cuix", 2, null, "RibbonRoot.cui"));
        var live = Row("Plugin", "P", MapStatus.NoAction, origin: "live", unplaced: true);
        var md = Section(Map(a, a2, b, code, loaded, live));
        var headings = md.Split('\n').Where(l => l.StartsWith("## ")).ToList();
        Assert.Equal(new[] { "## Home", "## Home", "## Code", "## Draw", "## Plugin" }, headings);
        Assert.Contains("From `one/a.cuix`.", md);
        Assert.Contains("From `two/b.cui`.", md);
        Assert.Contains("Built in code.", md);
        Assert.Contains("From `/acad/acad.cuix` (loaded in AutoCAD).", md);
        Assert.Contains("Live ribbon.", md);
        // A and A2 share their section
        var homeOne = md[md.IndexOf("## Home", StringComparison.Ordinal)..md.LastIndexOf("## Home", StringComparison.Ordinal)];
        Assert.Contains("| Panel | A |", homeOne);
        Assert.Contains("| Panel | A2 |", homeOne);
    }

    [Fact]
    public void An_empty_map_still_writes_all_three_files()
    {
        var paths = RibbonMapWriter.Write(Map(), _out);
        Assert.All(paths, p => Assert.True(File.Exists(p)));
        Assert.Single(ParseCsv(Text(paths[1])));   // the header alone
        using var doc = JsonDocument.Parse(Text(paths[2]));
        Assert.Equal(0, doc.RootElement.GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public void Language_names_are_the_ones_people_say()
    {
        Assert.Equal(new[] { "C#", "VB.NET", "C++", "LISP", "CUIX", "Other", "Xaml" },
            new[] { Lang.CSharp, Lang.VbNet, Lang.Cpp, Lang.Lisp, Lang.Cuix, Lang.Other, Lang.Xaml }.Select(RibbonMapWriter.LanguageName));
    }

    // a small RFC 4180 reader, to read the csv back
    private static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') { row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = new(); i++; }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }
}
