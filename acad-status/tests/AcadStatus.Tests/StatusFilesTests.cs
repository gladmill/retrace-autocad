using System;
using System.IO;
using System.Linq;
using System.Text;
using AcadStatus.Core;

namespace AcadStatus.Tests;

public class StatusFilesTests
{
    static DateTime Now => TestData.Now;

    // ---- the folder

    [Fact]
    public void TheEnvironmentVariableReplacesTheDefaultFolder()
    {
        var dir = StatusFiles.Resolve(n => n == "ACAD_STATUS_DIR" ? "  D:\\status\\here " : null, () => "C:\\ProgramData");
        Assert.Equal("D:\\status\\here", dir);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithoutItTheFolderIsUnderProgramData(string? env)
    {
        var dir = StatusFiles.Resolve(_ => env, () => Path.Combine("C:", "ProgramData"));
        Assert.Equal(Path.Combine("C:", "ProgramData", "Gladmill", "acad-status"), dir);
    }

    [Fact]
    public void TheVariableIsNamedAsInTheContract() => Assert.Equal("ACAD_STATUS_DIR", StatusFiles.EnvVar);

    // ---- which files a reader reads: ^[a-z0-9][a-z0-9-]{0,39}\.json$

    [Theory]
    [InlineData("acad-4812.json", true)]
    [InlineData("retrace.json", true)]
    [InlineData("acad-pilot-12.json", true)]
    [InlineData("a.json", true)]
    [InlineData("0.json", true)]
    [InlineData("9abc.json", true)]
    [InlineData("-acad.json", false)]
    [InlineData("Acad-1.json", false)]
    [InlineData("ACAD-1.json", false)]
    [InlineData("acad_1.json", false)]
    [InlineData("acad 1.json", false)]
    [InlineData("acad.v2.json", false)]
    [InlineData("retrace.v2.json", false)]
    [InlineData("acad-1.json.tmp-4812", false)]
    [InlineData("acad-1.json.tmp-4812.json", false)]
    [InlineData("acad-1.JSON", false)]
    [InlineData("acad-1.json ", false)]
    [InlineData("acad-1.jsonl", false)]
    [InlineData(".json", false)]
    [InlineData("json", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("é.json", false)]
    public void WhichNamesAreStatusFiles(string? name, bool expected) => Assert.Equal(expected, StatusFiles.IsStatusFileName(name));

    [Fact]
    public void TheStemMayBeFortyCharactersButNotForty1()
    {
        Assert.True(StatusFiles.IsStatusFileName(new string('a', 40) + ".json"));
        Assert.False(StatusFiles.IsStatusFileName(new string('a', 41) + ".json"));
    }

    [Fact]
    public void FileForNamesAnInProcessWriter() => Assert.Equal("retrace-4812.json", StatusFiles.FileFor("retrace", 4812));

    // ---- reading a folder

    [Fact]
    public void ReadsTheFilesThatAreStatusFilesAndSaysWhyItSkippedTheOthers()
    {
        using var dir = new TempFolder();
        dir.Write("acad-1.json", TestData.Json("acad", 1, state: "idle"));
        dir.Write("retrace-2.json", TestData.Json("retrace", 2));
        dir.Write("broken-3.json", "{ not json");
        dir.Write("missing-4.json", TestData.Json().Replace("\"state\":", "\"nostate\":"));
        dir.Write("acad.v2.json", TestData.Json("acad", 5));       // other name: never read
        dir.Write("acad-1.json.tmp-1", TestData.Json("acad", 6));  // temp file: never read
        dir.Write("Upper-7.json", TestData.Json("upper", 7));      // not lower case: never read
        dir.Write("notes.txt", "hello");
        var read = new StatusFiles(dir.Path).Read();
        Assert.Equal(new[] { "acad-1.json", "retrace-2.json" }, read.Entries.Select(e => e.File).OrderBy(x => x).ToArray());
        Assert.Equal(new[] { "broken-3.json", "missing-4.json" }, read.Skipped.Select(s => s.File).OrderBy(x => x).ToArray());
        Assert.All(read.Skipped, s => Assert.False(string.IsNullOrEmpty(s.Reason)));
    }

    [Fact]
    public void AMissingFolderIsAnEmptyRead()
    {
        var read = new StatusFiles(Path.Combine(TestData.TempDir(), "nope")).Read();
        Assert.Empty(read.Entries);
        Assert.Empty(read.Skipped);
    }

    [Fact]
    public void ASkipFileIsLeftOut()
    {
        using var dir = new TempFolder();
        dir.Write("acad-1.json", TestData.Json("acad", 1));
        dir.Write("retrace-2.json", TestData.Json("retrace", 2));
        var read = new StatusFiles(dir.Path).Read("acad-1.json");
        Assert.Equal("retrace-2.json", Assert.Single(read.Entries).File);
    }

    [Fact]
    public void AChangedFileIsReadAgainAndAnUnchangedOneComesFromTheCache()
    {
        using var dir = new TempFolder();
        var path = dir.Write("retrace-2.json", TestData.Json("retrace", 2, state: "running"));
        var files = new StatusFiles(dir.Path);
        var first = files.Read().Entries.Single();
        Assert.Same(first, files.Read().Entries.Single());
        File.WriteAllText(path, TestData.Json("retrace", 2, state: "blocked"));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal("blocked", files.Read().Entries.Single().State);
    }

    [Fact]
    public void AFileThatDisappearsLeavesTheList()
    {
        using var dir = new TempFolder();
        var path = dir.Write("retrace-2.json", TestData.Json("retrace", 2));
        var files = new StatusFiles(dir.Path);
        Assert.Single(files.Read().Entries);
        File.Delete(path);
        Assert.Empty(files.Read().Entries);
    }

    [Fact]
    public void AFileThatBecomesGoodIsReadAfterItWasBad()
    {
        using var dir = new TempFolder();
        var path = dir.Write("retrace-2.json", "{ half");
        var files = new StatusFiles(dir.Path);
        Assert.Single(files.Read().Skipped);
        File.WriteAllText(path, TestData.Json("retrace", 2));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        var read = files.Read();
        Assert.Single(read.Entries);
        Assert.Empty(read.Skipped);
    }

    [Fact]
    public void ABomFileIsRead()
    {
        using var dir = new TempFolder();
        File.WriteAllBytes(Path.Combine(dir.Path, "retrace-2.json"), new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(TestData.Json("retrace", 2))).ToArray());
        Assert.Single(new StatusFiles(dir.Path).Read().Entries);
    }

    [Fact]
    public void ATooLargeFileIsSkipped()
    {
        using var dir = new TempFolder();
        dir.Write("retrace-2.json", TestData.Json("retrace", 2, extra: ", \"pad\": \"" + new string('x', StatusFiles.MaxFileBytes) + "\""));
        var read = new StatusFiles(dir.Path).Read();
        Assert.Empty(read.Entries);
        Assert.Contains("larger", Assert.Single(read.Skipped).Reason);
    }

    [Fact]
    public void AFileOpenForReadingByAnotherProgramCanStillBeReplaced()
    {
        using var dir = new TempFolder();
        var path = Path.Combine(dir.Path, "acad-1.json");
        AtomicFile.Write(path, TestData.Json("acad", 1), 1);
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        AtomicFile.Write(path, TestData.Json("acad", 1, state: "idle"), 1);
        Assert.Single(new StatusFiles(dir.Path).Read().Entries);
    }

    // ---- start-up clean-up

    static StatusEntry Old(string source, int pid, double ageHours, string? file = null) =>
        TestData.Entry(source, pid, updated: Now.AddHours(-ageHours), file: file ?? source + "-" + pid + ".json");

    [Fact]
    public void CleanupTakesOldFilesOfGonePids()
    {
        var entries = new[] { Old("acad", 10, 2), Old("retrace", 11, 5), Old("acad", 12, 1.5) };
        var names = StatusFiles.CleanupCandidates(entries, Now, (_, _, _) => false);
        Assert.Equal(new[] { "acad-10.json", "retrace-11.json", "acad-12.json" }, names);
    }

    [Fact]
    public void CleanupLeavesFilesThatAreYoungerThanAnHour()
    {
        var entries = new[] { Old("acad", 10, 0.99), Old("acad", 11, 1.0), Old("retrace", 12, 0.1) };
        Assert.Empty(StatusFiles.CleanupCandidates(entries, Now, (_, _, _) => false));
    }

    [Fact]
    public void CleanupLeavesFilesWhosePidRuns()
    {
        var entries = new[] { Old("acad", 10, 9), Old("retrace", 11, 9) };
        Assert.Equal(new[] { "retrace-11.json" }, StatusFiles.CleanupCandidates(entries, Now, (p, _, _) => p == 10));
    }

    [Fact]
    public void CleanupTakesAnyStatusFileNotOnlyThoseOfTheAcadAndRetraceWriters()
    {
        var entries = new[] { Old("other", 10, 9), Old("acad-pilot", 11, 9, "acad-pilot-11.json"), Old("retrace", 12, 9, "retrace.json"), Old("someone", 13, 9, "acadx-13.json") };
        Assert.Equal(new[] { "other-10.json", "acad-pilot-11.json", "retrace.json", "acadx-13.json" }, StatusFiles.CleanupCandidates(entries, Now, (_, _, _) => false));
    }

    [Fact]
    public void CleanupTakesAFileWhosePidNowBelongsToAnotherProcess()
    {
        var start = Now.AddHours(-30);
        var reused = TestData.Entry("acad", 10, updated: Now.AddHours(-5), pidStart: start, file: "acad-10.json");
        var same = TestData.Entry("acad", 11, updated: Now.AddHours(-5), pidStart: start, file: "acad-11.json");
        // the probe is the real rule: a process runs under each pid, but pid 10 started a day later than the file says
        bool Probe(int pid, DateTime? pidStart, TimeSpan age) =>
            ProcessIdentity.Resolve(ProcessIdentity.StateOf(true, pid == 10 ? start.AddDays(1) : start, pidStart), pidStart != null, age);
        Assert.Equal(new[] { "acad-10.json" }, StatusFiles.CleanupCandidates(new[] { reused, same }, Now, Probe));
    }

    [Fact]
    public void CleanupTreatsAFailingPidCheckAsRunning()
    {
        var entries = new[] { Old("acad", 10, 9) };
        Assert.Empty(StatusFiles.CleanupCandidates(entries, Now, (_, _, _) => throw new InvalidOperationException()));
    }

    [Fact]
    public void CleanupDeletesTheFilesAndOnlyThose()
    {
        using var dir = new TempFolder();
        dir.Write("acad-10.json", TestData.Json("acad", 10, updated: Now.AddHours(-3)));
        dir.Write("acad-11.json", TestData.Json("acad", 11, updated: Now.AddHours(-3)));
        dir.Write("retrace-12.json", TestData.Json("retrace", 12, updated: Now.AddHours(-3)));
        dir.Write("retrace-13.json", TestData.Json("retrace", 13, updated: Now.AddMinutes(-5)));
        dir.Write("acad-14.json", "{ broken");
        dir.Write("other-15.json", TestData.Json("other", 15, updated: Now.AddHours(-3)));
        dir.Write("acad-10.json.tmp-1", "tmp");
        var deleted = new StatusFiles(dir.Path).Cleanup(Now, (pid, _, _) => pid == 11);
        Assert.Equal(new[] { "acad-10.json", "other-15.json", "retrace-12.json" }, deleted.OrderBy(x => x).ToArray());   // any source
        var left = Directory.GetFiles(dir.Path).Select(Path.GetFileName).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "acad-10.json.tmp-1", "acad-11.json", "acad-14.json", "retrace-13.json" }, left);
    }

    [Fact]
    public void CleanupOfAMissingFolderIsFine() =>
        Assert.Empty(new StatusFiles(Path.Combine(TestData.TempDir(), "nope")).Cleanup(Now, (_, _, _) => false));
}
