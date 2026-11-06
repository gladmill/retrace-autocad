using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AcadStatus.Core;

namespace AcadStatus.Tests;

public class AtomicFileTests
{
    [Fact]
    public void WritesTheTextAsUtf8WithoutBom()
    {
        using var dir = new TempFolder();
        var path = Path.Combine(dir.Path, "acad-1.json");
        AtomicFile.Write(path, "{ \"a\": \"é€\" }\n", 1);
        var bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "BOM");
        Assert.Equal("{ \"a\": \"é€\" }\n", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void ReplacesAnExistingFileAndLeavesNoTempFile()
    {
        using var dir = new TempFolder();
        var path = Path.Combine(dir.Path, "acad-1.json");
        AtomicFile.Write(path, "first", 77);
        AtomicFile.Write(path, "second", 77);
        Assert.Equal("second", File.ReadAllText(path));
        Assert.Equal(new[] { "acad-1.json" }, Directory.GetFiles(dir.Path).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void DoesNotCreateTheFolderAndSaysWhenItIsMissing()
    {
        // the caller ensures the folder once, with its permissions, and again only after this exception
        using var dir = new TempFolder();
        var path = Path.Combine(dir.Path, "deeper", "acad-1.json");
        Assert.Throws<DirectoryNotFoundException>(() => AtomicFile.Write(path, "x", 1));
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "deeper")));
        Directory.CreateDirectory(Path.Combine(dir.Path, "deeper"));
        AtomicFile.Write(path, "x", 1);
        Assert.Equal("x", File.ReadAllText(path));
    }

    [Fact]
    public void NeverSleeps()
    {
        // one try on the UI thread: no retry loop, so no Thread.Sleep
        static string Code(string path) => System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(path), "//[^\n]*", "");   // comments do not count
        var src = Code(Path.Combine(Path.GetDirectoryName(Here())!, "..", "..", "src", "AcadStatus", "Core", "AtomicFile.cs"));
        Assert.DoesNotContain("Sleep", src);
        Assert.DoesNotContain("attempts", src);
        var clip = Code(Path.Combine(Path.GetDirectoryName(Here())!, "..", "..", "src", "AcadStatus", "Ui", "ClipboardText.cs"));
        Assert.DoesNotContain("Sleep", clip);
        Assert.DoesNotContain("SetDataObject", clip);   // that call sleeps between its own retries
        Assert.Contains("clipboard busy, try again", clip);
    }

    static string Here([System.Runtime.CompilerServices.CallerFilePath] string p = "") => p;

    [Fact]
    public void ThrowsAndLeavesNoTempFileWhenTheTargetCannotBeReplaced()
    {
        using var dir = new TempFolder();
        var path = Path.Combine(dir.Path, "acad-1.json");
        Directory.CreateDirectory(path);   // a folder where the file should go
        Assert.ThrowsAny<Exception>(() => AtomicFile.Write(path, "x", 5));
        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void AReaderNeverSeesHalfAFile()
    {
        using var dir = new TempFolder();
        var path = Path.Combine(dir.Path, "acad-1.json");
        var big = TestData.Json(extra: ", \"pad\": \"" + new string('p', 60_000) + "\"");
        AtomicFile.Write(path, big, 1);
        var writer = new Thread(() =>
        {
            for (int i = 0; i < 150; i++)
                AtomicFile.Write(path, i % 2 == 0 ? big : TestData.Json(state: "idle"), 1);
        });
        writer.Start();
        int reads = 0, bad = 0;
        while (writer.IsAlive)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs);
                var text = sr.ReadToEnd();
                reads++;
                if (!StatusParser.Parse("acad-1.json", text).Ok) bad++;
            }
            catch (IOException) { /* a moment with no file to open is allowed on some systems: never a half file */ }
        }
        writer.Join();
        Assert.True(reads > 0);
        Assert.Equal(0, bad);
    }

    [Fact]
    public void ATempFileOfAnotherWriterIsNotTouched()
    {
        using var dir = new TempFolder();
        var path = Path.Combine(dir.Path, "acad-1.json");
        File.WriteAllText(path + ".tmp-999", "other writer's");
        AtomicFile.Write(path, "mine", 5);
        Assert.Equal("other writer's", File.ReadAllText(path + ".tmp-999"));
    }
}

public class WritePolicyTests
{
    static DateTime Now => TestData.Now;

    [Fact]
    public void TheFirstWriteAlwaysHappens() => Assert.True(WritePolicy.ShouldWrite(null, null, "k", Now));

    [Fact]
    public void ChangedContentIsWrittenAtOnce() => Assert.True(WritePolicy.ShouldWrite("a", Now, "b", Now.AddMilliseconds(10)));

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(1.49, false)]
    [InlineData(1.5, true)]
    [InlineData(2, true)]
    [InlineData(5, true)]
    public void UnchangedContentIsRefreshedAfterOneAndAHalfSeconds(double sinceLast, bool expected) =>
        Assert.Equal(expected, WritePolicy.ShouldWrite("a", Now, "a", Now.AddSeconds(sinceLast)));

    [Fact]
    public void TheRefreshIsWellInsideTheTtl() => Assert.True(WritePolicy.RefreshSec <= AcadStatusBuilder.TtlSec / 2.0);
}

public class PaneSettingTests
{
    [Fact]
    public void TheDefaultIsShown()
    {
        using var dir = new TempFolder();
        Assert.True(PaneSetting.Load(Path.Combine(dir.Path, "settings.json")));
    }

    [Fact]
    public void ItRoundTrips()
    {
        using var dir = new TempFolder();
        var path = Path.Combine(dir.Path, "sub", "settings.json");
        PaneSetting.Save(path, false);
        Assert.False(PaneSetting.Load(path));
        PaneSetting.Save(path, true);
        Assert.True(PaneSetting.Load(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("[1]")]
    [InlineData("{ \"pane\": \"no\" }")]
    [InlineData("{ \"other\": false }")]
    public void ABrokenFileMeansShown(string text)
    {
        using var dir = new TempFolder();
        var path = dir.Write("settings.json", text);
        Assert.True(PaneSetting.Load(path));
    }
}
