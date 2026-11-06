using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AcadStatus.Core;

namespace AcadStatus.Tests;

public class UnknownModalTests
{
    static DateTime Now => TestData.Now;

    [Fact]
    public void ADisabledMainWindowWithNoWindowFoundIsStillADialog()
    {
        var d = DialogPick.OrUnknownModal(null, modal: true)!;
        Assert.Equal("(unknown modal window)", d.Title);
        Assert.Equal("", d.Class);
        Assert.True(d.Modal);
        Assert.Empty(d.Texts);
        Assert.Empty(d.Buttons);
        Assert.Equal(DialogPick.UnknownModalHandle, d.Handle);
    }

    [Fact]
    public void AnEnabledMainWindowWithNoWindowFoundIsNoDialog() => Assert.Null(DialogPick.OrUnknownModal(null, modal: false));

    [Fact]
    public void AFoundDialogIsKeptAsItIs()
    {
        var found = TestData.Dialog(handle: 55);
        Assert.Same(found, DialogPick.OrUnknownModal(found, modal: true));
        Assert.Same(found, DialogPick.OrUnknownModal(found, modal: false));
    }

    [Fact]
    public void TheUnknownModalUsesTheSameHandleEveryTick() =>
        Assert.Equal(DialogPick.UnknownModal().Handle, DialogPick.UnknownModal().Handle);

    [Fact]
    public void ItGoesWaitingInputThenBlockedAfterFifteenSecondsAndIdleWhenTheWindowIsEnabledAgain()
    {
        var clock = new DialogClock();
        var b = new AcadStatusBuilder("w", 1);
        string StateAt(int sec, bool disabled)
        {
            var now = Now.AddSeconds(sec);
            var d = DialogPick.OrUnknownModal(null, disabled);
            return b.Build(TestData.Snap(dialog: d, dialogSince: clock.Observe(d, now)), now).Entry.State;
        }
        Assert.Equal("idle", StateAt(0, false));
        Assert.Equal("waiting-input", StateAt(1, true));
        Assert.Equal("waiting-input", StateAt(15, true));    // 14 s
        Assert.Equal("blocked", StateAt(16, true));          // 15 s
        Assert.Equal("blocked", StateAt(40, true));
        Assert.Equal("idle", StateAt(41, false));
    }

    [Fact]
    public void TheSummaryNamesTheUnknownWindow()
    {
        var b = new AcadStatusBuilder("w", 1);
        var doc = b.Build(TestData.Snap(dialog: DialogPick.UnknownModal(), dialogSince: Now.AddSeconds(-20)), Now);
        Assert.Equal("blocked", doc.Entry.State);
        Assert.Equal("Blocked by dialog \"(unknown modal window)\"", doc.Entry.Summary);
        Assert.Equal("Blocked", doc.Entry.Short);
        Assert.Empty(doc.Entry.Acad!.Dialog!.Texts);
    }

    [Fact]
    public void TheScanReportsItWhenTheMainWindowIsDisabled()
    {
        var scan = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Here())!, "..", "..", "src", "AcadStatus", "AutoCad", "DialogScan.cs"));
        Assert.Contains("DialogPick.OrUnknownModal(null, modal)", scan);
    }

    static string Here([CallerFilePath] string p = "") => p;
}

public class UpdatedAndTtlBoundsTests
{
    static DateTime Now => TestData.Now;

    static ParseResult Parse(string updated, int ttl = 10) =>
        StatusParser.Parse("x.json", TestData.Json().Replace("2026-11-05T10:14:03Z", updated).Replace("\"ttlSec\": 15", "\"ttlSec\": " + ttl), Now);

    [Fact]
    public void AnUpdatedUpToADayAheadIsFine()
    {
        Assert.True(Parse("2026-11-05T10:14:03Z").Ok);
        Assert.True(Parse("2026-11-05T10:24:03Z").Ok);
        Assert.True(Parse("2026-11-06T10:14:03Z").Ok);   // exactly a day
    }

    [Theory]
    [InlineData("2026-11-06T10:14:04Z")]   // a second past a day
    [InlineData("2026-12-25T00:00:00Z")]
    [InlineData("9999-12-31T23:59:59Z")]
    [InlineData("9999-12-31T23:59:59+00:00")]
    public void AnUpdatedMoreThanADayAheadIsSkipped(string updated)
    {
        var r = Parse(updated);
        Assert.False(r.Ok);
        Assert.Contains("future", r.Error);
    }

    [Theory]
    [InlineData("1999-12-31T23:59:59Z")]
    [InlineData("1970-01-01T00:00:00Z")]
    [InlineData("0001-01-01T00:00:00Z")]
    public void AnUpdatedBeforeTheYear2000IsSkipped(string updated)
    {
        var r = Parse(updated);
        Assert.False(r.Ok);
        Assert.Contains("2000", r.Error);
    }

    [Fact]
    public void TheFirstInstantOf2000IsFine() => Assert.True(Parse("2000-01-01T00:00:00Z").Ok);

    [Theory]
    [InlineData(1, true)]
    [InlineData(10, true)]
    [InlineData(86400, true)]
    [InlineData(0, false)]
    [InlineData(-5, false)]
    [InlineData(86401, false)]
    [InlineData(int.MaxValue, false)]
    [InlineData(int.MinValue, false)]
    public void TtlSecMustBeBetweenOneAndADay(int ttl, bool ok)
    {
        var r = Parse("2026-11-05T10:14:03Z", ttl);
        Assert.Equal(ok, r.Ok);
        if (!ok) Assert.Contains("ttlSec", r.Error);
    }

    [Fact]
    public void WithoutAGivenNowTheRealClockJudges()
    {
        var future = DateTime.UtcNow.AddDays(3).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        Assert.False(StatusParser.Parse("x.json", TestData.Json().Replace("2026-11-05T10:14:03Z", future)).Ok);
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        Assert.True(StatusParser.Parse("x.json", TestData.Json().Replace("2026-11-05T10:14:03Z", now)).Ok);
    }

    [Fact]
    public void ABadFileInTheFolderIsSkippedAndTheOthersAreRead()
    {
        using var dir = new TempFolder();
        dir.Write("a-1.json", TestData.Json("a", 1, updated: DateTime.UtcNow));
        dir.Write("b-2.json", TestData.Json("b", 2, updated: DateTime.UtcNow).Replace("\"ttlSec\": 15", "\"ttlSec\": 2000000000"));
        dir.Write("c-3.json", TestData.Json("c", 3, updated: DateTime.UtcNow.AddDays(400)));
        var read = new StatusFiles(dir.Path).Read();
        Assert.Equal(new[] { "a-1.json" }, read.Entries.Select(e => e.File).ToArray());
        Assert.Equal(new[] { "b-2.json", "c-3.json" }, read.Skipped.Select(s => s.File).OrderBy(x => x).ToArray());
    }

    // ---- Merge guards per entry

    [Fact]
    public void AnEntryThatCannotBeRankedIsIgnoredAndTheMergeGoesOn()
    {
        var good1 = TestData.Entry("retrace", 1, state: "running", file: "retrace-1.json");
        var good2 = TestData.Entry("acad", 2, state: "idle", file: "acad-2.json");
        // built by hand: a parsed file cannot carry this, but nothing else may be trusted to stay inside the dates AddSeconds can take
        var bad = TestData.Entry("bad", 3, file: "bad-3.json") with { Updated = DateTime.MaxValue, TtlSec = 86400 };
        var merged = TestMerge.Merge(new[] { good1, bad, good2 }, Now, _ => true);
        Assert.Equal(new[] { "retrace-1", "acad-2" }, merged.Shown.Select(m => m.Entry.Id).ToArray());
        var ignored = Assert.Single(merged.Ignored);
        Assert.Equal("bad-3.json", ignored.Entry.File);
        Assert.Contains("could not be ranked", ignored.Why);
        Assert.Equal("no update for 0s, could not be ranked (ArgumentOutOfRangeException)", ignored.Reason);
    }

    [Fact]
    public void ADateAtTheOtherEndIsGuardedToo()
    {
        var bad = TestData.Entry("bad", 3, file: "bad-3.json") with { Updated = DateTime.MinValue };
        var good = TestData.Entry("acad", 2, state: "idle", file: "acad-2.json");
        var merged = TestMerge.Merge(new[] { bad, good }, Now, _ => false);   // ancient, and its process is gone
        Assert.Equal("acad-2", Assert.Single(merged.Shown).Entry.Id);
        Assert.Equal("bad-3.json", Assert.Single(merged.Ignored).Entry.File);
    }
}

public class WritePolicyClockTests
{
    static DateTime Now => TestData.Now;

    [Fact]
    public void AClockThatWentBackwardWritesAtOnce() => Assert.True(WritePolicy.ShouldWrite("a", Now, "a", Now.AddSeconds(-5)));

    [Fact]
    public void AClockThatWentBackwardByAMillisecondAlsoWrites() => Assert.True(WritePolicy.ShouldWrite("a", Now, "a", Now.AddMilliseconds(-1)));

    [Fact]
    public void TheNormalRulesStillHold()
    {
        Assert.False(WritePolicy.ShouldWrite("a", Now, "a", Now));
        Assert.False(WritePolicy.ShouldWrite("a", Now, "a", Now.AddSeconds(1.4)));
        Assert.True(WritePolicy.ShouldWrite("a", Now, "a", Now.AddSeconds(1.5)));
        Assert.True(WritePolicy.ShouldWrite("a", Now, "b", Now));
    }
}

public class LatestReadTests
{
    [Fact]
    public void BeforeTheFirstTaskThereIsNoResult()
    {
        var r = new LatestRead<string>(() => "x");
        Assert.Null(r.Latest);
        Assert.False(r.InFlight);
    }

    [Fact]
    public void ARunningTaskIsNotStartedAgain()
    {
        using var gate = new ManualResetEventSlim();
        int starts = 0;
        var r = new LatestRead<string>(() => { Interlocked.Increment(ref starts); gate.Wait(); return "done"; });
        Assert.True(r.Kick());
        Assert.False(r.Kick());
        Assert.False(r.Kick());
        Assert.True(r.InFlight);
        Assert.Null(r.Latest);          // never waits for it
        gate.Set();
        Assert.True(SpinWait.SpinUntil(() => r.Latest == "done", 5000));
        Assert.Equal(1, starts);
    }

    [Fact]
    public void TheNextTaskStartsOnceTheLastIsCollected()
    {
        int n = 0;
        var r = new LatestRead<string>(() => "r" + Interlocked.Increment(ref n));
        Assert.True(r.Kick());
        Assert.True(SpinWait.SpinUntil(() => r.Latest == "r1", 5000));
        Assert.True(r.Kick());
        Assert.True(SpinWait.SpinUntil(() => r.Latest == "r2", 5000));
        Assert.Equal(2, n);
    }

    [Fact]
    public void AFailedTaskKeepsThePreviousResultAndTheNextMayStart()
    {
        int n = 0;
        var r = new LatestRead<string>(() => { var k = Interlocked.Increment(ref n); if (k == 2) throw new IOException("boom"); return "ok" + k; });
        r.Kick();
        Assert.True(SpinWait.SpinUntil(() => r.Latest == "ok1", 5000));
        r.Kick();
        Assert.True(SpinWait.SpinUntil(() => !r.InFlight, 5000));
        Assert.Equal("ok1", r.Latest);   // the failed one changed nothing
        Assert.True(r.Kick());
        Assert.True(SpinWait.SpinUntil(() => r.Latest == "ok3", 5000));
    }

    [Fact]
    public void KickNeverBlocksTheCaller()
    {
        using var gate = new ManualResetEventSlim();
        var r = new LatestRead<string>(() => { gate.Wait(); return "x"; });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        r.Kick();
        _ = r.Latest;
        r.Kick();
        Assert.True(sw.ElapsedMilliseconds < 1000, "kick and latest waited for the work");
        gate.Set();
    }

    [Fact]
    public void TheFolderCanBeReadFromTwoThreadsAtOnce()
    {
        using var dir = new TempFolder();
        for (int i = 1; i <= 20; i++) dir.Write("t-" + i + ".json", TestData.Json("t", i, updated: DateTime.UtcNow));
        var files = new StatusFiles(dir.Path);
        var failures = new List<string>();
        var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (int k = 0; k < 100; k++)
            {
                try
                {
                    var read = files.Read();
                    if (read.Entries.Count != 20) lock (failures) failures.Add("count " + read.Entries.Count);
                }
                catch (Exception ex) { lock (failures) failures.Add(ex.GetType().Name); }
            }
        })).ToArray();
        Task.WaitAll(tasks);
        Assert.Empty(failures);
    }

    [Fact]
    public void ARealFolderReadThroughTheLatestReadDeliversTheEntries()
    {
        using var dir = new TempFolder();
        dir.Write("retrace-2.json", TestData.Json("retrace", 2, updated: DateTime.UtcNow));
        dir.Write("acad-1.json", TestData.Json("acad", 1, updated: DateTime.UtcNow));
        var files = new StatusFiles(dir.Path);
        var r = new LatestRead<FolderRead>(() => files.Read("acad-1.json"));   // our own file is left out
        r.Kick();
        Assert.True(SpinWait.SpinUntil(() => r.Latest != null, 5000));
        Assert.Equal("retrace-2.json", Assert.Single(r.Latest!.Entries).File);
    }
}

public class ServiceStructureRoundThreeTests
{
    static string Src([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "AcadStatus"));
    static string Service() => File.ReadAllText(Path.Combine(Src(), "AutoCad", "StatusService.cs"));

    [Fact]
    public void TheFolderIsReadOnABackgroundTaskAndOnlyTheDumpReadsInPlace()
    {
        var text = Service();
        Assert.Contains("new LatestRead<FolderRead>(", text);
        Assert.Contains("_folder.Latest", text);
        Assert.Contains("_folder.Kick()", text);
        // the two places that call Read: the background lambda, and the dump's own
        Assert.Equal(2, Regex.Matches(text, "_files\\.Read\\(").Count);
        var dump = text[text.IndexOf("void Merge(", StringComparison.Ordinal)..];
        Assert.Contains("if (withRaw) read = _files.Read(", dump);
    }

    [Fact]
    public void TheFolderIsEnsuredOnceAndAgainOnlyAfterDirectoryNotFound()
    {
        var text = Service();
        Assert.Contains("catch (DirectoryNotFoundException)", text);
        Assert.Equal(3, Regex.Matches(text, "EnsureFolder").Count);   // its definition, the start, and the one retry after DirectoryNotFoundException
        Assert.DoesNotContain("CreateDirectory", File.ReadAllText(Path.Combine(Src(), "Core", "AtomicFile.cs")));
        // Begin ensures it once, with Log.Safe
        Assert.Contains("Log.Safe(\"create status folder\", EnsureFolder)", text);
    }

    [Fact]
    public void StatusMergeHasNoTestOnlyOverloads()
    {
        var text = File.ReadAllText(Path.Combine(Src(), "Core", "StatusMerge.cs"));
        Assert.DoesNotContain("Func<int, bool>", text);
        Assert.DoesNotContain("Func<int, DateTime?, bool>", text);
        Assert.Equal(1, Regex.Matches(text, "public static MergeResult Merge\\(").Count);
    }
}
