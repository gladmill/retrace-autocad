using Retrace.Engine.Emit;

namespace Retrace.Tests;

public class GuideMergeTests
{
    private static GuideBlock B(string key, string content) => new(key, content + "\n", $"_stub {key}_");

    private static string Fresh(params GuideBlock[] blocks) => GuideMerge.Create("", blocks);

    [Fact]
    public void FirstCreationHasBlocksAndStubs()
    {
        var text = Fresh(B("page", "# T"), B("button:a", "### A"));
        Assert.Equal(
            "<!-- engine:begin page -->\n# T\n<!-- engine:end page -->\n\n_stub page_\n\n" +
            "<!-- engine:begin button:a -->\n### A\n<!-- engine:end button:a -->\n\n_stub button:a_\n\n", text);
    }

    [Fact]
    public void UserTextPreservedAndBlockUpdated()
    {
        var text = Fresh(B("page", "old"), B("button:a", "old a"));
        text = text.Replace("_stub page_", "My intro, \r\nwith odd   spacing.\t").Replace("_stub button:a_", "My note on A") + "trailing no newline";
        var r = GuideMerge.Merge(text, new[] { B("page", "new"), B("button:a", "new a") });
        Assert.Null(r.Problem);
        Assert.Contains("new\n<!-- engine:end page -->", r.Text);
        Assert.Contains("new a\n<!-- engine:end button:a -->", r.Text);
        Assert.DoesNotContain("old", r.Text);
        Assert.Equal(text.Replace("old a", "new a").Replace("old", "new"), r.Text);
    }

    [Fact]
    public void NewKeyInsertedBeforeNextExistingKeyAfterUserText()
    {
        var text = Fresh(B("page", "p"), B("button:c", "c"));
        text = text.Replace("_stub page_", "USER AFTER PAGE");
        var r = GuideMerge.Merge(text, new[] { B("page", "p"), B("button:a", "a"), B("button:b", "b"), B("button:c", "c") });
        var t = r.Text!;
        int page = t.IndexOf("engine:begin page"), user = t.IndexOf("USER AFTER PAGE"), a = t.IndexOf("engine:begin button:a"),
            b = t.IndexOf("engine:begin button:b"), c = t.IndexOf("engine:begin button:c");
        Assert.True(page < user && user < a && a < b && b < c);
        Assert.Contains("_stub button:a_", t);
        Assert.Contains("_stub button:b_", t);
    }

    [Fact]
    public void NewKeyWithNoFollowerGoesToEnd()
    {
        var text = Fresh(B("page", "p")).TrimEnd('\n') + " user tail";
        var r = GuideMerge.Merge(text, new[] { B("page", "p"), B("button:z", "z") });
        Assert.StartsWith(text + "\n\n<!-- engine:begin button:z -->", r.Text);
        Assert.EndsWith("_stub button:z_\n\n", r.Text);
    }

    [Fact]
    public void RemovedKeyIsFlaggedAndUserTextKept()
    {
        var text = Fresh(B("page", "p"), B("button:gone", "g")).Replace("_stub button:gone_", "KEEP ME");
        var r = GuideMerge.Merge(text, new[] { B("page", "p") });
        Assert.Contains("<!-- engine:begin button:gone -->\n" + GuideMerge.RemovedNote + "\n<!-- engine:end button:gone -->\n\nKEEP ME", r.Text);
        // flagged blocks stay stable on later runs
        Assert.Equal(r.Text, GuideMerge.Merge(r.Text!, new[] { B("page", "p") }).Text);
        // and come back to life if the source returns
        var back = GuideMerge.Merge(r.Text!, new[] { B("page", "p"), B("button:gone", "g2") });
        Assert.Contains("g2\n<!-- engine:end button:gone -->\n\nKEEP ME", back.Text);
    }

    [Fact]
    public void OwnerOrderIsKept()
    {
        var text = Fresh(B("page", "p"), B("button:a", "a"), B("button:b", "b"), B("button:c", "c"));
        // owner moves c to the top
        int at = text.IndexOf("<!-- engine:begin button:c");
        var moved = text[at..] + text[..at];
        var r = GuideMerge.Merge(moved, new[] { B("page", "p"), B("button:a", "a2"), B("button:b", "b2"), B("button:c", "c2") });
        var t = r.Text!;
        Assert.True(t.IndexOf("button:c -->") < t.IndexOf("engine:begin page") && t.IndexOf("engine:begin page") < t.IndexOf("engine:begin button:a"));
        Assert.Contains("c2", t);
    }

    [Theory]
    [InlineData("<!-- engine:begin a -->\nx\n")]                                                  // no end
    [InlineData("<!-- engine:end a -->\n")]                                                       // end only
    [InlineData("<!-- engine:begin a -->\n<!-- engine:begin b -->\n<!-- engine:end b -->\n<!-- engine:end a -->\n")] // nested
    [InlineData("<!-- engine:begin a -->\n<!-- engine:end a -->\n<!-- engine:begin a -->\n<!-- engine:end a -->\n")] // duplicate
    [InlineData("<!-- engine:begin a -->\n<!-- engine:end b -->\n")]                              // mismatch
    public void MalformedPageIsRejected(string text)
    {
        var r = GuideMerge.Merge(text, new[] { B("a", "x") });
        Assert.Null(r.Text);
        Assert.NotNull(r.Problem);
    }

    [Fact]
    public void MarkerLookalikesInContentAreDefused()
    {
        var text = Fresh(B("a", "<!-- engine:end a -->\nstill content"));
        Assert.Null(GuideMerge.Merge(text, new[] { B("a", "<!-- engine:end a -->") }).Problem);
    }

    [Fact]
    public void IdempotentAndCrlfSafe()
    {
        var blocks = new[] { B("page", "p"), B("button:a", "a") };
        var text = Fresh(blocks);
        Assert.Equal(text, GuideMerge.Merge(text, blocks).Text);
        var crlf = text.Replace("\n", "\r\n");
        Assert.Equal(crlf, GuideMerge.Merge(crlf, blocks).Text);
    }
}

public class GuideWriterTests : IDisposable
{
    private readonly string _out = Directory.CreateTempSubdirectory("retrace-test-guide").FullName;
    private readonly string _caps = Directory.CreateTempSubdirectory("retrace-test-caps").FullName;

    public void Dispose()
    {
        Directory.Delete(_out, true);
        Directory.Delete(_caps, true);
    }

    // 1x1 transparent PNG
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private EngineModel Run(bool captures)
    {
        var m = EmitTests.SampleModel();
        Linker.AddRibbonHandlers(m);
        Linker.Link(m);
        var opt = new EngineOptions { Input = "/in", Output = _out, Captures = captures ? [_caps] : [] };
        Writer.WriteAll(m, opt);
        return m;
    }

    private string Page => Path.Combine(_out, "guide", "ribbon", "code", "plugin", "garden.md");

    private void AddCapture()
    {
        File.WriteAllBytes(Path.Combine(_caps, "Plugin__FormSettings.png"), Png);
        File.WriteAllText(Path.Combine(_caps, "Plugin__FormSettings.json"), "{\"formId\":\"winform.Plugin.FormSettings\"}");
        File.WriteAllText(Path.Combine(_caps, "Orphan__X.json"), "{\"formId\":\"nope\"}");
    }

    [Fact]
    public void WritesIndexAndTabPageWithWireframeWhenNoCapture()
    {
        Run(false);
        var index = File.ReadAllText(Path.Combine(_out, "guide", "index.md"));
        Assert.Contains("[Garden](ribbon/code/plugin/garden.md) | `TAB_Garden` |", index);
        Assert.Contains("# User guide", index);
        var page = File.ReadAllText(Page);
        Assert.Contains("<!-- engine:begin page -->", page);
        Assert.Contains("<!-- engine:begin panel:Beds -->", page);
        Assert.Contains("<!-- engine:begin button:ribbon:code:Plugin:TAB_Garden/Beds/DEMO_Settings -->", page);
        Assert.Contains("![Garden ribbon wireframe](../../../../reference/images/ribbon/code/plugin/garden.svg)", page);
        Assert.Contains("### Settings", page);
        Assert.Contains("- **Command:** `DEMO_SETTINGS`", page);
        Assert.Contains("- **Tooltip:** Shows settings", page);
        Assert.Contains("**Opens:** WinForm [Settings \\| Main](../../../../reference/dialogs/plugin/formsettings.md)", page);
        Assert.Contains("(../../../../reference/images/dialogs/plugin/formsettings.svg)", page);
        Assert.Contains("Reference: [Garden tab](../../../../reference/ribbon/code/plugin/garden.md), [command DEMO_SETTINGS](../../../../reference/commands/plugin.md#cmd-demo-settings)", page);
        Assert.Contains("_What does Settings do, and when would someone use it? (your text; the engine never changes it)_", page);
        Assert.False(Directory.Exists(Path.Combine(_out, "guide", "images", "captures")));
    }

    [Fact]
    public void CaptureReplacesWireframe()
    {
        AddCapture();
        Run(true);
        var page = File.ReadAllText(Page);
        var png = Path.Combine(_out, "guide", "images", "captures", "winform-plugin-formsettings.png");
        Assert.Equal(Png, File.ReadAllBytes(png));
        Assert.Contains("(../../../images/captures/winform-plugin-formsettings.png)", page);
        Assert.DoesNotContain("formsettings.svg", page);
        // the reference docs keep the wireframe
        Assert.Contains("formsettings.svg", File.ReadAllText(Path.Combine(_out, "reference", "dialogs", "plugin", "formsettings.md")));
    }

    [Fact]
    public void SecondRunIsByteIdenticalAndKeepsOwnerText()
    {
        AddCapture();
        Run(true);
        Dictionary<string, byte[]> Snap() => Directory.GetFiles(Path.Combine(_out, "guide"), "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, File.ReadAllBytes);
        var first = Snap();
        Assert.NotEmpty(first);
        Run(true);
        var second = Snap();
        Assert.Equal(first.Keys.OrderBy(k => k), second.Keys.OrderBy(k => k));
        foreach (var (k, v) in first) Assert.Equal(v, second[k]);

        File.AppendAllText(Page, "\nMY OWN NOTES\n");
        File.WriteAllText(Page, File.ReadAllText(Page).Replace("_What does Settings do", "WRITTEN BY ME _What does Settings do"));
        Run(true);
        var page = File.ReadAllText(Page);
        Assert.Contains("MY OWN NOTES", page);
        Assert.Contains("WRITTEN BY ME", page);
    }

    [Fact]
    public void MalformedPageIsNotTouchedAndFreshCopyIsWritten()
    {
        Run(false);
        var broken = File.ReadAllText(Page).Replace("<!-- engine:end page -->", "");
        File.WriteAllText(Page, broken);
        var m = Run(false);
        Assert.Equal(broken, File.ReadAllText(Page));
        var side = Path.Combine(_out, "guide", "ribbon", "code", "plugin", "garden.engine-new.md");
        Assert.True(File.Exists(side));
        Assert.Contains("<!-- engine:end page -->", File.ReadAllText(side));
        Assert.Contains(m.Warnings, w => w.Contains("garden.md") && w.Contains("engine-new.md"));
    }

    [Fact]
    public void ButtonRemovedFromSourceIsFlaggedNotDeleted()
    {
        Run(false);
        File.WriteAllText(Page, File.ReadAllText(Page).Replace("_What does List Points do", "KEEP LIST NOTES _What does List Points do"));
        var m = EmitTests.SampleModel();
        m.CodeTabs[0].Panels[0].Rows[0].Items.RemoveAt(1);
        Linker.AddRibbonHandlers(m);
        Linker.Link(m);
        Writer.WriteAll(m, new EngineOptions { Input = "/in", Output = _out });
        var page = File.ReadAllText(Page);
        Assert.Contains("KEEP LIST NOTES", page);
        Assert.Contains(GuideMerge.RemovedNote, page);
    }
}
