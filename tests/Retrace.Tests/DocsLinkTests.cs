using System.Text.Json;
using System.Text.RegularExpressions;
using Retrace.Engine.Emit;

namespace Retrace.Tests;

/// <summary>The docs folder as the engine writes it with a tests folder and captures: every relative link and image in every page resolves to
/// an existing file (and anchor), and the paths in data\sourcemap.json and data\recordings.json exist. Synthetic data only.</summary>
public class DocsLinkTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("retrace-links").FullName;
    private string Docs => Path.Combine(_root, "docs");
    private string Tests => Path.Combine(_root, "tests");
    private string Captures => Path.Combine(_root, "captures");

    public void Dispose() => Directory.Delete(_root, true);

    static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    // Garden > Beds > Settings opens the Settings dialog (SampleModel); a typed CIRCLE; a checkpoint; pictures for most of them.
    static readonly string[] Lines =
    {
        """{"type":"header","schema":1,"name":"Change settings","started":"2026-10-08T10:00:00","autocad":"25.1.0.0","product":"AutoCAD 2026","workspace":"Drafting"}""",
        """{"n":1,"t":10,"type":"state","state":{"count":0,"byType":{},"layers":["0"],"hash":"e3"}}""",
        """{"n":2,"t":1000,"type":"ribbon","id":"DEMO_Settings","text":"Settings","macro":"^C^C_DEMO_SETTINGS","tab":"Garden","panel":"Beds"}""",
        """{"n":3,"t":1050,"type":"cmd_start","command":"DEMO_SETTINGS","origin":"ribbon"}""",
        """{"n":4,"t":1100,"type":"modal_enter","title":"Settings"}""",
        """{"n":5,"t":2000,"type":"modal_leave"}""",
        """{"n":6,"t":2010,"type":"cmd_end","command":"DEMO_SETTINGS"}""",
        """{"n":7,"t":3000,"type":"cmd_start","command":"CIRCLE","origin":"typed/other"}""",
        """{"n":8,"t":3100,"type":"prompt","kind":"point","message":"Specify center point for circle:","status":"OK","value":[12,20,0],"input":true}""",
        """{"n":9,"t":3210,"type":"cmd_end","command":"CIRCLE"}""",
        """{"n":10,"t":4000,"type":"mark","note":"circle is visible","state":{"count":1,"byType":{"CIRCLE":1},"layers":["0"],"hash":"abcd"}}""",
        """{"n":11,"t":4001,"type":"shot","for":2,"file":"screenshots/002-ribbon.png","what":"window"}""",
        """{"n":12,"t":4002,"type":"shot","for":4,"file":"screenshots/004-dialog.png","what":"dialog"}""",
        """{"n":13,"t":4003,"type":"shot","for":9,"file":"screenshots/009-cmd-end.png","what":"window"}""",
        """{"n":14,"t":4004,"type":"shot","for":10,"file":"screenshots/010-mark.png","what":"window"}""",
        """{"n":15,"t":5000,"type":"session_end"}""",
    };

    /// <summary>A test folder with pictures; two of them, so a second recordings page exists.</summary>
    private string AddTest(string folder, string name)
    {
        var dir = Path.Combine(Tests, folder);
        Directory.CreateDirectory(Path.Combine(dir, "screenshots"));
        File.WriteAllLines(Path.Combine(dir, "recording.jsonl"), Lines.Select(l => l.Replace("Change settings", name)));
        foreach (var f in new[] { "002-ribbon", "004-dialog", "009-cmd-end", "010-mark" }) File.WriteAllBytes(Path.Combine(dir, "screenshots", f + ".png"), Png);
        return Path.Combine(dir, "recording.jsonl");
    }

    private void Build()
    {
        AddTest("2026-10-08_1000_change-settings", "Change settings");
        AddTest("2026-10-09_0900_other-run", "Other run");
        Directory.CreateDirectory(Captures);
        File.WriteAllBytes(Path.Combine(Captures, "Plugin__FormSettings.png"), Png);
        File.WriteAllText(Path.Combine(Captures, "Plugin__FormSettings.json"), "{\"formId\":\"winform.Plugin.FormSettings\"}");
        var m = EmitTests.SampleModel();
        Linker.AddRibbonHandlers(m);
        Linker.Link(m);
        Writer.WriteAll(m, new EngineOptions { Input = "/in", Output = Docs, Sessions = [Tests], Captures = [Captures] });
    }

    static readonly Regex Link = new(@"!?\[(?:[^\]\\]|\\.)*\]\(([^)\s]+)\)", RegexOptions.CultureInvariant);
    static readonly Regex Anchor = new("<a id=\"([^\"]+)\"></a>", RegexOptions.CultureInvariant);

    /// <summary>Every relative target of a page: (target path, fragment).</summary>
    static IEnumerable<(string Target, string Fragment)> Targets(string md)
    {
        foreach (Match m in Link.Matches(md))
        {
            var t = m.Groups[1].Value;
            if (t.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || t.StartsWith("https:", StringComparison.OrdinalIgnoreCase) || t.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) continue;
            var hash = t.IndexOf('#');
            yield return (Uri.UnescapeDataString(hash >= 0 ? t[..hash] : t), hash >= 0 ? t[(hash + 1)..] : "");
        }
    }

    /// <summary>The broken targets of every .md under <paramref name="root"/>, as "page -> target" lines.</summary>
    static List<string> Broken(string root)
    {
        var broken = new List<string>();
        foreach (var page in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(page);
            foreach (var (target, fragment) in Targets(text))
            {
                var rel = Path.GetRelativePath(root, page);
                if (target.Length == 0) { if (fragment.Length > 0 && !Anchor.Matches(text).Any(a => a.Groups[1].Value == fragment) && !text.Contains("# ")) broken.Add($"{rel} -> #{fragment}"); continue; }
                var file = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(page)!, target.Replace('/', Path.DirectorySeparatorChar)));
                if (!File.Exists(file)) { broken.Add($"{rel} -> {target}"); continue; }
                if (fragment.Length > 0 && file.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !Anchor.Matches(File.ReadAllText(file)).Any(a => a.Groups[1].Value == fragment))
                    broken.Add($"{rel} -> {target}#{fragment} (no such anchor)");
            }
        }
        return broken;
    }

    [Fact]
    public void Every_relative_link_and_image_in_the_docs_resolves()
    {
        Build();
        var pages = Directory.EnumerateFiles(Docs, "*.md", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(Docs, f).Replace('\\', '/')).ToList();
        // the pages this test is about exist, in the documented places
        foreach (var expected in new[]
        {
            "README.md", "guide/index.md", "guide/ribbon/code/plugin/garden.md", "guide/howto/change-settings.md",
            "reference/ribbon/index.md", "reference/ribbon/code/plugin/garden.md", "reference/commands/index.md", "reference/commands/plugin.md",
            "reference/dialogs/index.md", "reference/dialogs/plugin/formsettings.md", "reference/projects.md",
            "test-plans/index.md", "test-plans/commands/plugin.md", "test-plans/dialogs/plugin.md", "test-plans/ribbon/code/plugin/garden.md",
            "recordings/index.md", "recordings/2026-10-08_1000_change-settings.md", "recordings/2026-10-09_0900_other-run.md",
        })
            Assert.Contains(expected, pages);
        var links = pages.Sum(p => Targets(File.ReadAllText(Path.Combine(Docs, p))).Count());
        Assert.True(links > 60, $"only {links} links checked");

        Assert.Empty(Broken(Docs));
    }

    [Fact]
    public void Images_and_data_files_are_where_the_pages_say()
    {
        Build();
        foreach (var f in new[]
        {
            "reference/images/ribbon/code/plugin/garden.svg", "reference/images/dialogs/plugin/formsettings.svg",
            "guide/images/captures/winform-plugin-formsettings.png",
            "recordings/images/2026-10-08_1000_change-settings/002-ribbon.png", "guide/images/howto/change-settings/002-ribbon.png",
            "data/sourcemap.json", "data/recordings.json", "data/model.json",
        })
            Assert.True(File.Exists(Path.Combine(Docs, f.Replace('/', Path.DirectorySeparatorChar))), f);
        // nothing at the old places
        foreach (var old in new[] { "sourcemap.json", "model.json", "projects.md", "ribbon", "commands", "ui", "assets", "tests" })
            Assert.False(File.Exists(Path.Combine(Docs, old)) || Directory.Exists(Path.Combine(Docs, old)), old);
        Assert.False(Directory.Exists(Path.Combine(Docs, "guide", "assets")));

        using var map = JsonDocument.Parse(File.ReadAllText(Path.Combine(Docs, "data", "sourcemap.json")));
        var checkedPaths = 0;
        foreach (var e in map.RootElement.GetProperty("entries").EnumerateArray())
        {
            foreach (var key in new[] { "doc", "asset" })
            {
                if (!e.TryGetProperty(key, out var v) || v.GetString() is not { } rel) continue;
                var file = rel.Split('#')[0];
                Assert.True(File.Exists(Path.Combine(Docs, file.Replace('/', Path.DirectorySeparatorChar))), $"{e.GetProperty("id").GetString()}: {key} {rel}");
                checkedPaths++;
            }
            if (e.TryGetProperty("tests", out var tests))
                foreach (var t in tests.EnumerateArray())
                {
                    var file = t.GetString()!.Split('#')[0];
                    Assert.True(File.Exists(Path.Combine(Docs, file.Replace('/', Path.DirectorySeparatorChar))), $"{e.GetProperty("id").GetString()}: test {t}");
                    checkedPaths++;
                }
        }
        Assert.True(checkedPaths > 10);

        using var rec = JsonDocument.Parse(File.ReadAllText(Path.Combine(Docs, "data", "recordings.json")));
        foreach (var s in rec.RootElement.GetProperty("sessions").EnumerateArray())
            Assert.True(File.Exists(Path.Combine(Docs, s.GetProperty("page").GetString()!.Replace('/', Path.DirectorySeparatorChar))), s.GetProperty("page").GetString());
    }

    [Fact]
    public void Steps_pages_link_into_the_docs_and_to_their_pictures()
    {
        Build();
        var rec = Path.Combine(Tests, "2026-10-08_1000_change-settings", "recording.jsonl");
        Assert.True(EngineRunner.WriteSteps(rec, sourceMap: Docs).Success);
        var steps = Path.Combine(Path.GetDirectoryName(rec)!, "steps.md");
        var text = File.ReadAllText(steps);
        Assert.Contains("](../../docs/reference/ribbon/code/plugin/garden.md)", text);
        Assert.Contains("![Click Garden › Beds › Settings](screenshots/", text);   // alt text keeps the words of a link, not its address   // the ribbon click links to its reference page
        var broken = Targets(text).Where(t => t.Target.Length > 0 && !File.Exists(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(steps)!, t.Target.Replace('/', Path.DirectorySeparatorChar))))).ToList();
        Assert.Empty(broken);
        Assert.True(Targets(text).Count(t => t.Target.StartsWith("screenshots/")) >= 3);
    }

    [Fact]
    public void Running_again_changes_nothing_and_keeps_owner_text()
    {
        Build();
        var page = Path.Combine(Docs, "guide", "howto", "change-settings.md");
        var md = File.ReadAllText(page);
        File.WriteAllText(page, md.Replace("_What does this procedure achieve", "Owner wrote this.\n\n_What does this procedure achieve"));
        var tab = Path.Combine(Docs, "guide", "ribbon", "code", "plugin", "garden.md");
        File.WriteAllText(tab, File.ReadAllText(tab).Replace("_What is the Garden tab for?", "Owner: the garden tab.\n\n_What is the Garden tab for?"));
        Dictionary<string, byte[]> Snap() => Directory.GetFiles(Docs, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(Docs, f), File.ReadAllBytes);
        var before = Snap();
        var m = EmitTests.SampleModel();
        Linker.AddRibbonHandlers(m);
        Linker.Link(m);
        Writer.WriteAll(m, new EngineOptions { Input = "/in", Output = Docs, Sessions = [Tests], Captures = [Captures] });
        var after = Snap();
        Assert.Equal(before.Keys.OrderBy(k => k, StringComparer.Ordinal), after.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var (f, bytes) in before.Where(kv => !kv.Key.StartsWith("data" + Path.DirectorySeparatorChar) && kv.Key != "README.md"))   // timestamps live in data\ and the README
            Assert.True(bytes.AsSpan().SequenceEqual(after[f]), f);
        Assert.Contains("Owner wrote this.", File.ReadAllText(page));
        Assert.Contains("Owner: the garden tab.", File.ReadAllText(tab));
        Assert.Empty(Broken(Docs));
    }
}
