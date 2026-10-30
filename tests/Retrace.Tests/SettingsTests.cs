using Retrace.Core;
using Xunit;

namespace Retrace.Tests;

public class SettingsTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData(".", "")]
    [InlineData("Default", "")]
    [InlineData("C:\\a\\def", "")]
    [InlineData("D:\\other", "D:\\other")]
    [InlineData("\"D:\\q\"", "D:\\q")]
    public void Normalize_stores_defaults_as_empty(string text, string expected) =>
        Assert.Equal(expected, RetraceSettings.Normalize(text, "C:\\a\\def"));

    [Fact]
    public void Paths_are_all_under_the_root()
    {
        Assert.Equal(@"C:\temp\retrace", Paths.Root);
        var r = Path.Combine("r", "retrace");
        Assert.Equal(Path.Combine(r, "settings.json"), Paths.SettingsFile(r));
        Assert.Equal(Path.Combine(r, "retrace.log"), Paths.LogFile(r));
        Assert.Equal(Path.Combine(r, "tests"), Paths.Tests(r));
        Assert.Equal(Path.Combine(r, "captures"), Paths.Captures(r));
        Assert.Equal(Path.Combine(r, "docs"), Paths.Docs(r));
        Assert.Equal(Path.Combine(r, "cache", "webview"), Paths.WebViewCache(r));
        Assert.Equal(Path.Combine(r, "cache", "native"), Paths.NativeCache(r));
    }

    [Fact]
    public void Defaults_live_under_the_root()
    {
        var s = new RetraceSettings { RootDir = Path.Combine("r", "retrace") };
        Assert.Equal(Paths.Tests(s.RootDir), s.Tests);
        Assert.Equal(Paths.Captures(s.RootDir), s.Captures);
        Assert.Equal(Paths.Docs(s.RootDir), s.Docs);
        Assert.Equal(Path.Combine(s.Docs, "guide", "index.md"), s.GuideIndex);
        Assert.Equal("", s.Source);
        Assert.Equal("", RetraceSettings.Normalize(s.DefaultTests, s.DefaultTests));
    }

    [Fact]
    public void A_stored_folder_replaces_the_default()
    {
        var s = new RetraceSettings { TestsFolder = "/x/t", DocsFolder = "/x/d", CapturesFolder = "/x/c" };
        Assert.Equal("/x/t", s.Tests);
        Assert.Equal("/x/d", s.Docs);
        Assert.Equal("/x/c", s.Captures);
    }

    [Fact]
    public void Settings_file_has_exactly_these_keys_and_round_trips()
    {
        var root = Path.Combine(Path.GetTempPath(), "retrace-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var s = new RetraceSettings { RootDir = root, SourceFolder = "/src", DocsFolder = "/d", TestsFolder = "/t", CapturesFolder = "/c", EditorPath = "ed", ExportDocx = true, RibbonTabPosition = "last", TdocBringToFront = false, TdocAnnotate = false, TdocStepDelayMs = 2500 };
            Assert.True(s.Save(out var err), err);
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Paths.SettingsFile(root)));
            Assert.Equal(
                new[] { "autoFillDialogs", "captureAllWorkspaces", "captureRibbon", "capturesFolder", "checkpointKey", "dialogValues", "docsFolder", "editorPath", "exportDocx", "exportXlsx", "loadedCustomization", "promptStyle", "reviewCheckpoints", "ribbonTabPosition", "screenshots", "showDrivenUi", "sourceFolder", "tdocAnnotate", "tdocBringToFront", "tdocStepDelayMs", "testsFolder" },
                doc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
            var back = RetraceSettings.Load(root);
            Assert.Equal(root, back.RootDir);
            Assert.Equal("/t", back.TestsFolder);
            Assert.Equal("/d", back.DocsFolder);
            Assert.True(back.ExportDocx);
            Assert.Equal("last", back.RibbonTabPosition);
            Assert.Equal("Ctrl+Shift+K", back.CheckpointKey);
            Assert.False(back.TdocBringToFront);
            Assert.False(back.TdocAnnotate);
            Assert.Equal(2500, back.TdocStepDelayMs);
            Assert.Equal("last", RibbonTabPosition.ReadSettingFromDisk(root));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public void Tdoc_settings_default_to_front_annotate_and_1500_ms()
    {
        var s = new RetraceSettings();
        Assert.True(s.TdocBringToFront);
        Assert.True(s.TdocAnnotate);
        Assert.Equal(1500, s.TdocStepDelayMs);
        var root = Path.Combine(Path.GetTempPath(), "retrace-tdocdef-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Paths.SettingsFile(root), "{ \"screenshots\": false }");   // keys missing: the defaults
            var back = RetraceSettings.Load(root);
            Assert.True(back.TdocBringToFront);
            Assert.True(back.TdocAnnotate);
            Assert.Equal(1500, back.TdocStepDelayMs);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(250, 250)]
    [InlineData(10000, 10000)]
    [InlineData(10001, 10000)]
    [InlineData(int.MaxValue, 10000)]
    public void Tdoc_step_delay_is_clamped_on_load(int stored, int expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "retrace-tdocclamp-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Paths.SettingsFile(root), "{ \"tdocStepDelayMs\": " + stored + " }");
            Assert.Equal(expected, RetraceSettings.Load(root).TdocStepDelayMs);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public void EnsureHome_creates_the_default_folders_and_settings_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "retrace-home-" + Guid.NewGuid().ToString("N"));
        try
        {
            RetraceSettings.EnsureHome(root);
            Assert.True(File.Exists(Paths.SettingsFile(root)));
            Assert.True(Directory.Exists(Paths.Tests(root)));
            Assert.True(Directory.Exists(Paths.Captures(root)));
            Assert.True(Directory.Exists(Paths.Docs(root)));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public void Docs_options_leave_out_missing_folders()
    {
        var s = new RetraceSettings { SourceFolder = "/src", DocsFolder = "/out", CapturesFolder = "/no/such/cap", TestsFolder = Path.GetTempPath() };
        var o = EngineJobs.DocsOptions(s);
        Assert.Equal(Path.GetFullPath("/src"), o.Input);
        Assert.Equal(Path.GetFullPath("/out"), o.Output);
        Assert.Empty(o.Captures);
        Assert.Equal(new[] { Path.GetFullPath(Path.GetTempPath()) }, o.Sessions);
    }

    [Fact]
    public void Relative_folders_are_resolved_against_the_root_not_the_current_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "retrace-rel-root");
        var s = new RetraceSettings { RootDir = root, SourceFolder = Path.Combine("src", "x"), DocsFolder = "mydocs", TestsFolder = "t", CapturesFolder = "c" };
        Assert.Equal(Path.Combine(root, "src", "x"), s.Source);
        Assert.Equal(Path.Combine(root, "mydocs"), s.Docs);
        Assert.Equal(Path.Combine(root, "t"), s.Tests);
        Assert.Equal(Path.Combine(root, "c"), s.Captures);
    }
}
