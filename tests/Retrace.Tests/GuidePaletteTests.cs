using System.Text;
using Retrace.Core;
using Retrace.Core;
using Xunit;

namespace Retrace.Tests;

// The pure parts of the Guide palette (src/Retrace/Core/GuideModel.cs). The WPF/WebView2 parts only run in AutoCAD.
public class GuidePaletteTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "retrace-test-docs-" + Guid.NewGuid().ToString("N"));

    public GuidePaletteTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (Exception) { } }

    string Write(string rel, string text)
    {
        var p = Path.Combine(_dir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
        return p;
    }

    // ---- page tree ----

    [Fact]
    public void Tree_has_index_howto_and_ribbon_tabs_grouped_by_source()
    {
        Write("index.md", "# Guide index\n");
        Write("howto/draw-a-line.md", "intro\n# Draw a line\n");
        Write("howto/zz-no-heading.md", "just text\n");
        Write("ribbon/cuix/acad/home.md", "# Home tab\n");
        Write("ribbon/cuix/acad/insert.md", "# Insert tab\n");
        Write("ribbon/code/contoso/contoso.md", "# Contoso\n");

        var roots = GuideTree.Build(_dir);

        Assert.Equal(new[] { "Guide index", "How-to", "Ribbon tabs" }, roots.Select(r => r.Title));
        Assert.Equal(PageKind.Index, roots[0].Kind);
        Assert.Equal(new[] { "Draw a line", "zz-no-heading" }, roots[1].Children.Select(c => c.Title));
        Assert.All(roots[1].Children, c => Assert.Equal(PageKind.HowTo, c.Kind));

        var sources = roots[2].Children;
        Assert.Equal(new[] { "code/contoso", "cuix/acad" }, sources.Select(s => s.Title));
        Assert.Equal(new[] { "Home tab", "Insert tab" }, sources[1].Children.Select(c => c.Title));
        Assert.All(roots[2].Flatten().Where(n => n.Kind == PageKind.Ribbon), n => Assert.True(File.Exists(n.Path)));
    }

    [Fact]
    public void Tree_of_missing_or_empty_folder_is_empty()
    {
        Assert.Empty(GuideTree.Build(Path.Combine(_dir, "nope")));
        Assert.Empty(GuideTree.Build(_dir));
    }

    // ---- generated blocks ----

    const string Page = "# Tab\r\nmine\r\n<!-- engine:begin page -->\r\nTitle line\r\n<!-- engine:end page -->\r\nmy notes\r\n" +
                        "<!-- engine:begin button:b1 -->\r\n**Line**\r\n<!-- engine:end button:b1 -->\r\n";

    [Fact]
    public void Blocks_parse_by_key_with_normalised_line_endings()
    {
        var b = GeneratedBlocks.Parse(Page);
        Assert.Equal(2, b.Count);
        Assert.Equal("Title line", b["page"]);
        Assert.Equal("**Line**", b["button:b1"]);
    }

    [Fact]
    public void Editing_text_outside_blocks_is_not_a_generated_edit()
    {
        var edited = Page.Replace("mine", "mine, changed").Replace("my notes", "my notes\r\nmore notes");
        Assert.Empty(GeneratedBlocks.EditedBlocks(Page, edited));
        Assert.Empty(GeneratedBlocks.EditedBlocks(Page, Page.Replace("\r\n", "\n")));   // only the line endings differ
    }

    [Fact]
    public void Editing_inside_a_block_is_reported_by_key()
    {
        var edited = Page.Replace("**Line**", "**Line** (my addition)");
        Assert.Equal(new[] { "button:b1" }, GeneratedBlocks.EditedBlocks(Page, edited));
        var both = edited.Replace("Title line", "Other title");
        Assert.Equal(new[] { "page", "button:b1" }, GeneratedBlocks.EditedBlocks(Page, both));
    }

    [Fact]
    public void Deleting_a_whole_block_or_a_marker_pair_is_not_an_inside_edit()
    {
        var withoutBlock = "# Tab\r\n<!-- engine:begin page -->\r\nTitle line\r\n<!-- engine:end page -->\r\n";
        Assert.Empty(GeneratedBlocks.EditedBlocks(Page, withoutBlock));
        Assert.Empty(GeneratedBlocks.EditedBlocks("no blocks at all", Page));
    }

    // ---- links ----

    [Theory]
    [InlineData("../../howto/draw.md", LinkKind.Markdown)]
    [InlineData("index.md#top", LinkKind.Markdown)]
    [InlineData("https://guide.local/guide/howto/x.md", LinkKind.Markdown)]
    [InlineData("#b-line", LinkKind.Anchor)]
    [InlineData("../../../../assets/ribbon/acad/home.svg", LinkKind.Asset)]
    [InlineData("assets/captures/Line.png", LinkKind.Asset)]
    [InlineData("https://guide.local/assets/a.svg", LinkKind.Asset)]
    [InlineData("https://example.com/readme.md", LinkKind.External)]
    [InlineData("http://example.com/", LinkKind.External)]
    [InlineData("mailto:a@b.c", LinkKind.External)]
    public void Links_are_classified(string href, LinkKind expected) => Assert.Equal(expected, GuideLinks.Classify(href));

    [Fact]
    public void Guide_urls_map_to_files_under_the_root_only()
    {
        var file = Write("guide/howto/a b.md", "x");
        var url = GuideLinks.ToUrl(file, _dir, "step-1");
        Assert.Equal("https://guide.local/guide/howto/a%20b.md#step-1", url);
        Assert.Equal(file, GuideLinks.ToFilePath(new Uri(url), _dir));
        // Uri collapses "..", and anything it does not collapse is caught by the root check: the result is never outside the root
        foreach (var evil in new[] { "https://guide.local/../../etc/passwd.md", "https://guide.local/%2e%2e/%2e%2e/x.md", "https://guide.local/guide/..%2f..%2f..%2fx.md" })
        {
            var r = GuideLinks.ToFilePath(new Uri(evil), _dir);
            Assert.True(r == null || r.StartsWith(Path.GetFullPath(_dir) + Path.DirectorySeparatorChar), evil + " -> " + r);
        }
        Assert.Null(GuideLinks.ToFilePath(new Uri("https://other.example/guide/index.md"), _dir));
    }

    // ---- saving ----

    [Fact]
    public void Save_keeps_crlf_and_writes_no_bom()
    {
        var p = Write("crlf.md", "a\r\nb\r\n");
        PageFile.Save(p, "a\nb\nc\n");   // the editor hands back whatever endings it likes
        var bytes = File.ReadAllBytes(p);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal("a\r\nb\r\nc\r\n", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Save_keeps_lf_and_mixed_files_use_the_majority()
    {
        var lf = Write("lf.md", "a\nb\n");
        PageFile.Save(lf, "a\r\nb\r\nc\r\n");
        Assert.Equal("a\nb\nc\n", File.ReadAllText(lf));

        var mixed = Write("mixed.md", "a\r\nb\r\nc\nd\r\n");
        PageFile.Save(mixed, "x\ny\n");
        Assert.Equal("x\r\ny\r\n", File.ReadAllText(mixed));
    }

    [Fact]
    public void Save_writes_utf8_and_drops_an_existing_bom()
    {
        var p = Path.Combine(_dir, "bom.md");
        File.WriteAllText(p, "ä\r\n", new UTF8Encoding(true));
        PageFile.Save(p, "äö\n");
        var bytes = File.ReadAllBytes(p);
        Assert.Equal(new byte[] { 0xC3, 0xA4, 0xC3, 0xB6, 0x0D, 0x0A }, bytes);
    }

    [Fact]
    public void Eol_detection_and_conversion()
    {
        Assert.Equal("\r\n", PageFile.DetectEol("a\r\nb"));
        Assert.Equal("\n", PageFile.DetectEol("a\nb\r\nc\n"));
        Assert.Equal("\n", PageFile.DetectEol("none", "\n"));
        Assert.Equal("a\r\nb", PageFile.WithEol("a\nb", "\r\n"));
        Assert.Equal("a\nb", PageFile.WithEol("a\r\nb", "\n"));
        Assert.Equal(PageFile.Normalize("a\r\nb\r"), PageFile.Normalize("a\nb\n"));
    }

    // ---- editor setting (linked RetraceSettings) ----

    [Fact]
    public void Default_editor_prefers_code_on_path_then_the_vscode_folder_then_notepad()
    {
        var onPath = RetraceSettings.DefaultEditor(f => f == Path.Combine("/bin", "code.cmd"), "/usr" + Path.PathSeparator + "/bin", "/local");
        Assert.Equal(Path.Combine("/bin", "code.cmd"), onPath);

        var vsFolder = Path.Combine("/local", "Programs", "Microsoft VS Code", "Code.exe");
        Assert.Equal(vsFolder, RetraceSettings.DefaultEditor(f => f == vsFolder, "/usr", "/local"));
        Assert.Equal("notepad.exe", RetraceSettings.DefaultEditor(_ => false, "/usr", "/local"));
    }

    [Fact]
    public void Editor_setting_round_trips_and_empty_means_default()
    {
        var s = new RetraceSettings { EditorPath = "" };
        Assert.Equal(RetraceSettings.DefaultEditor(), s.Editor);
        s.EditorPath = "\"C:\\tools\\ed.exe\"";
        Assert.Equal("C:\\tools\\ed.exe", s.Editor);
        var json = System.Text.Json.JsonSerializer.Serialize(s);
        Assert.Contains("\"editorPath\"", json);
        Assert.Equal(s.EditorPath, System.Text.Json.JsonSerializer.Deserialize<RetraceSettings>(json)!.EditorPath);
    }
}
