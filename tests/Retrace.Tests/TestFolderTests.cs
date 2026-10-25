using System.Text.Json;
using System.Text.RegularExpressions;
using Retrace.Core;
using Retrace.Engine.Recordings;

namespace Retrace.Tests;

/// <summary>Test folders: recorder naming, renaming of pictures, results history, finding recordings. Synthetic data only.</summary>
public class TestFolderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("retrace-test-testfolders").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    const string Header = """{"type":"header","schema":1,"name":"draw test","started":"2026-10-10T00:08:07.1234567+02:00","autocad":"25.1.0.0","product":"AutoCAD 2026","workspace":"Drafting"}""";

    static readonly string[] Lines =
    {
        Header,
        """{"n":1,"t":10,"type":"state","state":{"count":0,"byType":{},"layers":["0"],"hash":"e3"}}""",
        """{"n":2,"t":1000,"type":"ribbon","id":"ID_Line","text":"Line","macro":"^C^C_line","tab":"Home","panel":"Draw"}""",
        """{"n":3,"t":1010,"type":"cmd_start","command":"LINE","origin":"ribbon"}""",
        """{"n":4,"t":1100,"type":"prompt","kind":"point","message":"Specify first point:","status":"OK","value":[9,9,0],"input":false}""",
        """{"n":5,"t":1200,"type":"prompt","kind":"point","message":"Specify first point:","status":"OK","value":[0,0,0],"input":true,"by":"mouse"}""",
        """{"n":6,"t":1300,"type":"prompt","kind":"point","message":"Specify next point or [Undo]:","keywords":["Undo"],"status":"OK","value":[5,5,0],"input":true,"by":"mouse"}""",
        """{"n":7,"t":1400,"type":"cmd_end","command":"LINE"}""",
        """{"n":8,"t":2000,"type":"cmd_start","command":"DRAWGRID","origin":"typed/other"}""",
        """{"n":9,"t":2010,"type":"modal_enter","title":""}""",
        """{"n":10,"t":2300,"type":"modal_title","title":"Draw grid"}""",
        """{"n":11,"t":3000,"type":"modal_leave"}""",
        """{"n":12,"t":3010,"type":"cmd_end","command":"DRAWGRID"}""",
        """{"n":13,"t":4000,"type":"mark","note":"line drawn","state":{"count":1,"byType":{"LINE":1},"layers":["0"],"hash":"ab"}}""",
        """{"n":14,"t":4001,"type":"shot","for":2,"file":"screenshots/002-ribbon.png","what":"window"}""",
        """{"n":15,"t":4002,"type":"shot","for":4,"file":"screenshots/004-noise.png","what":"window"}""",
        """{"n":16,"t":4003,"type":"shot","for":6,"file":"screenshots/006-prompt.png","what":"window"}""",
        """{"n":17,"t":4004,"type":"shot","for":7,"file":"screenshots/007-end.png","what":"window"}""",
        """{"n":18,"t":4005,"type":"shot","for":10,"file":"screenshots/010-dialog.png","what":"dialog"}""",
        """{"n":19,"t":4006,"type":"shot","for":13,"file":"screenshots/013-mark.png","what":"window"}""",
        """{"n":20,"t":4007,"type":"shot","for":999,"file":"screenshots/orphan.png","what":"window"}""",
        """{"n":21,"t":5000,"type":"session_end"}""",
    };

    static readonly string[] Pictures = { "002-ribbon", "004-noise", "006-prompt", "007-end", "010-dialog", "013-mark", "orphan", "stray" };

    /// <summary>A 1x1 PNG with one trailing byte, so each picture has its own content.</summary>
    static byte[] Png(int i) => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==").Append((byte)i).ToArray();

    /// <summary>A recording in a new test folder with its pictures in screenshots\ (and one "stray" picture no event mentions).</summary>
    private string TestFolder(string folderName = "2026-10-10_0008_draw-test", string[]? lines = null)
    {
        var dir = Path.Combine(_root, folderName);
        Directory.CreateDirectory(Path.Combine(dir, "screenshots"));
        File.WriteAllLines(Path.Combine(dir, "recording.jsonl"), lines ?? Lines);
        for (int i = 0; i < Pictures.Length; i++) File.WriteAllBytes(Path.Combine(dir, "screenshots", Pictures[i] + ".png"), Png(i));
        return dir;
    }

    private static List<string> Files(string dir) =>
        Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/')).OrderBy(f => f, StringComparer.Ordinal).ToList();

    // ---- recorder -------------------------------------------------------------------------------------------------

    [Fact]
    public void Recorder_creates_one_folder_per_recording_named_by_name_and_start()
    {
        string path, folder;
        using (var w = new SessionWriter("dtest", new Dictionary<string, object?>(), _root)) { path = w.Path; w.Event("a", null); }
        folder = Path.GetDirectoryName(path)!;
        Assert.Equal("recording.jsonl", Path.GetFileName(path));
        Assert.Equal(_root, Path.GetDirectoryName(folder));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}_\d{4}_dtest$", Path.GetFileName(folder));
        var header = JsonDocument.Parse(File.ReadLines(path).First()).RootElement;
        var started = DateTimeOffset.Parse(header.GetProperty("started").GetString()!).DateTime;
        Assert.Equal(TestFolders.FolderName("dtest", started), Path.GetFileName(folder));
        Assert.Equal("dtest", header.GetProperty("name").GetString());
    }

    [Fact]
    public void Folder_name_gets_a_number_when_it_exists_and_is_safe_for_windows()
    {
        var name = TestFolders.FolderName("dtest", new DateTime(2026, 10, 10, 2, 43, 59));
        Assert.Equal("2026-10-10_0243_dtest", name);
        Directory.CreateDirectory(Path.Combine(_root, name));
        Assert.Equal(Path.Combine(_root, name + "_2"), TestFolders.UniqueFolder(_root, name));
        Directory.CreateDirectory(Path.Combine(_root, name + "_2"));
        Assert.Equal(Path.Combine(_root, name + "_3"), TestFolders.UniqueFolder(_root, name));
        Assert.Equal("2026-10-10_0243_a-b-c-d", TestFolders.FolderName("a:b/c?d", new DateTime(2026, 10, 10, 2, 43, 0)));
        Assert.Equal("2026-10-10_0243_a-b-c-d-e-f-g-h-i-", TestFolders.FolderName("a<b>c:d\"e/f\\g|h?i*", new DateTime(2026, 10, 10, 2, 43, 0)));
        Assert.Equal("2026-10-10_0243_test", TestFolders.FolderName("  ", new DateTime(2026, 10, 10, 2, 43, 0)));
    }

    [Fact]
    public void Screenshots_go_to_screenshots_next_to_a_recording()
    {
        Assert.Equal("screenshots", ShotNaming.FolderFor("/x/2026-10-10_0243_dtest/recording.jsonl"));
        Assert.Equal("screenshots/012-ribbon.png", ShotNaming.RelativePathFor("/x/t/recording.jsonl", 12, "ribbon"));
    }

    // ---- names, unused, idempotence ----------------------------------------------------------------------

    [Fact]
    public void Steps_name_pictures_after_steps_and_move_the_rest_to_unused()
    {
        var dir = TestFolder();
        Assert.True(EngineRunner.WriteSteps(Path.Combine(dir, "recording.jsonl")).Success);

        Assert.Equal(new[]
        {
            "recording.jsonl",
            "screenshots/step-01-click.png", "screenshots/step-01-end.png", "screenshots/step-01-pick-1.png",
            "screenshots/step-02-dialog-before.png", "screenshots/step-03-check.png",
            "screenshots/unused/004-noise.png", "screenshots/unused/orphan.png", "screenshots/unused/stray.png",
            "steps.md",
        }, Files(dir));
        // content moved with the name (picture 0 = 002-ribbon, 2 = 006-prompt ...)
        Assert.Equal(Png(0), File.ReadAllBytes(Path.Combine(dir, "screenshots", "step-01-click.png")));
        Assert.Equal(Png(2), File.ReadAllBytes(Path.Combine(dir, "screenshots", "step-01-pick-1.png")));
        Assert.Equal(Png(4), File.ReadAllBytes(Path.Combine(dir, "screenshots", "step-02-dialog-before.png")));
        Assert.Equal(Png(1), File.ReadAllBytes(Path.Combine(dir, "screenshots", "unused", "004-noise.png")));

        var md = File.ReadAllText(Path.Combine(dir, "steps.md"));
        Assert.Contains("](screenshots/step-01-click.png)", md);
        Assert.Contains("](screenshots/step-01-pick-1.png)", md);
        Assert.Contains("](screenshots/step-01-end.png)", md);
        Assert.Contains("](screenshots/step-02-dialog-before.png)", md);
        Assert.Contains("](screenshots/step-03-check.png)", md);
        Assert.DoesNotContain("unused", md.Replace("`unused`", ""));
        // every link in the script points at a file that exists
        foreach (Match m in Regex.Matches(md, @"\]\((screenshots/[^)]+)\)")) Assert.True(File.Exists(Path.Combine(dir, m.Groups[1].Value)), m.Value);
    }

    [Fact]
    public void Steps_rewrite_only_the_file_of_shot_events_and_keeps_every_other_byte()
    {
        var dir = TestFolder();
        var rec = Path.Combine(dir, "recording.jsonl");
        var before = File.ReadAllText(rec).Split('\n');
        Assert.True(EngineRunner.WriteSteps(rec).Success);
        var after = File.ReadAllText(rec).Split('\n');

        Assert.Equal(before.Length, after.Length);
        for (int i = 0; i < before.Length; i++)
        {
            if (!before[i].Contains("\"type\":\"shot\"")) { Assert.Equal(before[i], after[i]); continue; }
            // same line but for the file value: replace it back and compare
            var file = JsonDocument.Parse(after[i]).RootElement.GetProperty("file").GetString()!;
            var oldFile = JsonDocument.Parse(before[i]).RootElement.GetProperty("file").GetString()!;
            Assert.Equal(before[i], after[i].Replace(file, oldFile));
        }
        Assert.Contains("\"for\":2,\"file\":\"screenshots/step-01-click.png\"", File.ReadAllText(rec));
        Assert.Contains("\"for\":4,\"file\":\"screenshots/unused/004-noise.png\"", File.ReadAllText(rec));
        Assert.Contains("\"for\":999,\"file\":\"screenshots/unused/orphan.png\"", File.ReadAllText(rec));
    }

    [Fact]
    public void Steps_twice_changes_nothing_and_links_keep_working()
    {
        var dir = TestFolder();
        var rec = Path.Combine(dir, "recording.jsonl");
        Assert.True(EngineRunner.WriteSteps(rec).Success);
        var snap = Files(dir).ToDictionary(f => f, f => File.ReadAllBytes(Path.Combine(dir, f)));
        Assert.True(EngineRunner.WriteSteps(rec).Success);
        Assert.Equal(snap.Keys.OrderBy(k => k, StringComparer.Ordinal), Files(dir));
        foreach (var (f, bytes) in snap) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(dir, f)));
        // a third run after the script was deleted still finds every picture
        File.Delete(Path.Combine(dir, "steps.md"));
        Assert.True(EngineRunner.WriteSteps(rec).Success);
        var md = File.ReadAllText(Path.Combine(dir, "steps.md"));
        foreach (Match m in Regex.Matches(md, @"\]\((screenshots/[^)]+)\)")) Assert.True(File.Exists(Path.Combine(dir, m.Groups[1].Value)), m.Value);
        Assert.Equal(snap["recording.jsonl"], File.ReadAllBytes(rec));
    }

    [Fact]
    public void Script_top_has_a_files_section_between_the_intro_and_the_steps()
    {
        var dir = TestFolder();
        Assert.True(EngineRunner.WriteSteps(Path.Combine(dir, "recording.jsonl")).Success);
        var md = File.ReadAllText(Path.Combine(dir, "steps.md"));
        var intro = md.IndexOf("Do the steps in order.", StringComparison.Ordinal);
        var files = md.IndexOf("## Files in this folder", StringComparison.Ordinal);
        var steps = md.IndexOf("## Steps", StringComparison.Ordinal);
        Assert.True(intro >= 0 && intro < files && files < steps);
        var section = md[files..steps];
        foreach (var row in new[] { "| `steps.md` |", "| `recording.jsonl` |", "| `screenshots` |", "| `results` |" }) Assert.Contains(row, section);
        Assert.Contains("Retrace** tab", section);
        Assert.Contains("**Run test**", section);
        Assert.Equal(4, section.Split('\n').Count(l => l.StartsWith("| `")));
    }

    [Fact]
    public void Colliding_picture_names_get_a_number()
    {
        // two prompts of one step with the same role get -1 and -2 (K); two end pictures of one step collide on name and get -2
        var lines = Lines.Concat(new[]
        {
            """{"n":22,"t":4008,"type":"shot","for":7,"file":"screenshots/007-end.png","what":"window"}""",
            """{"n":23,"t":4009,"type":"shot","for":7,"file":"screenshots/007-end-b.png","what":"window"}""",
        }).ToArray();
        var dir = TestFolder(lines: lines);
        File.WriteAllBytes(Path.Combine(dir, "screenshots", "007-end-b.png"), Png(20));
        Assert.True(EngineRunner.WriteSteps(Path.Combine(dir, "recording.jsonl")).Success);
        Assert.True(File.Exists(Path.Combine(dir, "screenshots", "step-01-end.png")));
        Assert.True(File.Exists(Path.Combine(dir, "screenshots", "step-01-end-2.png")));
        Assert.Equal(Png(20), File.ReadAllBytes(Path.Combine(dir, "screenshots", "step-01-end-2.png")));
    }

    // ---- results -------------------------------------------------------------------------------------------------

    [Fact]
    public void Results_keep_a_history_and_latest_json_is_the_newest_copy()
    {
        var dir = TestFolder();
        var rec = Path.Combine(dir, "recording.jsonl");
        var first = new ReplayResult { Verdict = ReplayResult.Fail, Session = "x" }.Write(rec)!;
        var second = new ReplayResult { Verdict = ReplayResult.Pass, Session = "x" }.Write(rec)!;
        Assert.NotEqual(first, second);
        Assert.True(File.Exists(first));   // the older run stays
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}_(FAIL|PASS)(_2)?\.json$", Path.GetFileName(first));
        Assert.EndsWith("_PASS.json", Path.GetFileName(second).Replace("_2.json", ".json"));
        Assert.Equal(Path.Combine(dir, "results"), Path.GetDirectoryName(second));
        var latest = Path.Combine(dir, "results", "latest.json");
        Assert.Equal(File.ReadAllText(second), File.ReadAllText(latest));
        Assert.Contains("\"PASS\"", File.ReadAllText(latest));
        Assert.Equal(latest, ReplayResult.LatestFor(rec));
        Assert.Equal(3, Directory.GetFiles(Path.Combine(dir, "results")).Length);
        Assert.Empty(Directory.GetFiles(Path.Combine(dir, "results"), "*.tmp"));
    }

    // ---- finding recordings -----------------------------------------------------------------------------------------

    [Fact]
    public void Recordings_are_found_in_test_folders_only()
    {
        TestFolder("2026-10-10_0008_a");
        var b = TestFolder("2026-10-11_0900_b", Lines.Select(l => l.Replace("2026-10-10T00:08:07.1234567+02:00", "2026-10-11T09:00:00+02:00")).ToArray());
        // a flat session, a result file and a staging folder are not recordings
        File.WriteAllText(Path.Combine(_root, "20261009-120000-old.jsonl"), Header + "\n");
        File.WriteAllText(Path.Combine(_root, "x.result.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_root, ".staging-1", "c"));
        File.WriteAllText(Path.Combine(_root, ".staging-1", "c", "recording.jsonl"), Header + "\n");

        var found = TestFolders.FindRecordings(_root).Select(f => Path.GetRelativePath(_root, f)).OrderBy(f => f, StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { Path.Combine("2026-10-10_0008_a", "recording.jsonl"), Path.Combine("2026-10-11_0900_b", "recording.jsonl") }, found);
        Assert.Equal(Path.Combine(b, "recording.jsonl"), TestFolders.Newest(_root));   // newest = latest `started`
        File.Delete(Path.Combine(b, "recording.jsonl"));
        Assert.Equal(Path.Combine(_root, "2026-10-10_0008_a", "recording.jsonl"), TestFolders.Newest(_root));
        Assert.Null(TestFolders.Newest(Path.Combine(_root, "nope")));

        // the engine finds the same files (a folder argument), and names a recording after its folder
        var engine = RecordingReader.Expand([_root]).Select(f => Path.GetRelativePath(_root, f)).OrderBy(f => f, StringComparer.Ordinal).ToList();
        Assert.Equal(found.Where(f => !f.Contains("_b")), engine);
        Assert.Equal("2026-10-10_0008_a", RecordingReader.Load(Path.Combine(_root, "2026-10-10_0008_a", "recording.jsonl")).Name);
        // a single test folder or an explicit recording.jsonl works; any other file does not
        Assert.Single(RecordingReader.Expand([Path.Combine(_root, "2026-10-10_0008_a")]));
        Assert.Single(RecordingReader.Expand([Path.Combine(_root, "2026-10-10_0008_a", "recording.jsonl")]));
        Assert.Throws<FileNotFoundException>(() => RecordingReader.Expand([Path.Combine(_root, "20261009-120000-old.jsonl")]));
    }

    [Fact]
    public void Test_list_strips_the_date_prefix_and_reads_the_latest_verdict()
    {
        var dir = TestFolder("2026-10-10_0008_draw-test");
        Assert.Equal("draw-test", TestList.NameOf("2026-10-10_0008_draw-test"));
        Assert.Equal("draw-test_2", TestList.NameOf("2026-10-10_0008_draw-test_2"));
        new ReplayResult { Verdict = ReplayResult.Pass }.Write(Path.Combine(dir, "recording.jsonl"));
        var e = Assert.Single(TestList.Read(_root));
        Assert.Equal("PASS", e.Verdict);
        Assert.Equal(Path.Combine(dir, "steps.md"), e.Steps);
        Assert.Equal(Path.Combine(dir, "steps.docx"), e.Docx);
        Assert.Equal(Path.Combine(dir, "steps.xlsx"), e.Xlsx);
    }

    [Fact]
    public void Recordings_are_listed_newest_first_and_an_unnamed_test_falls_back_to_test()
    {
        var a = TestFolder("2026-10-10_0008_a");
        var b = TestFolder("2026-10-11_0900_b", Lines.Select(l => l.Replace("2026-10-10T00:08:07.1234567+02:00", "2026-10-11T09:00:00+02:00")).ToArray());
        Assert.Equal(new[] { Path.Combine(b, "recording.jsonl"), Path.Combine(a, "recording.jsonl") }, TestFolders.FindRecordings(_root));
        Assert.Equal(TestFolders.FindRecordings(_root)[0], TestFolders.Newest(_root));
        Assert.Equal("test", TestFolders.CleanName(" . "));
    }
}
