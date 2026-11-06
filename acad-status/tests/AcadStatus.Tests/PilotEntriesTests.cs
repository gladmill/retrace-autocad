using System;
using System.Linq;
using AcadStatus.Core;

namespace AcadStatus.Tests;

/// <summary>Writers outside AutoCAD use their own process id (CONTRACT.md): one file per dev kit script, several of one source at once.
/// Nothing groups entries by source, so each one is shown on its own.</summary>
public class PilotEntriesTests
{
    static DateTime Now => TestData.Now;

    static StatusEntry Pilot(int pid, string state, string summary) =>
        StatusParser.Parse("acad-pilot-" + pid + ".json", TestData.Json("acad-pilot", pid, Now.AddSeconds(-1), 30, state, summary,
            extra: ", \"detail\": { \"script\": \"run\", \"cmds\": \"ZOOM E|-RETRACEMARK plan\", \"stallSec\": 30 }")).Entry!;

    [Theory]
    [InlineData("acad-pilot-7720.json", true)]
    [InlineData("acad-pilot-7.json", true)]
    [InlineData("acad-pilot-1234567.json", true)]
    [InlineData("acad-pilot.json", true)]
    public void ThePilotFileNamesAreStatusFiles(string name, bool expected) => Assert.Equal(expected, StatusFiles.IsStatusFileName(name));

    [Fact]
    public void SeveralPilotEntriesAreAllShownEachWithItsOwnId()
    {
        var entries = new[]
        {
            Pilot(7720, "running", "run: ZOOM E"),
            Pilot(7721, "waiting-input", "query"),
            Pilot(7722, "idle", "idle"),
            TestData.Entry("acad", 4812, state: "idle", file: "acad-4812.json"),
        };
        var merged = TestMerge.Merge(entries, Now, _ => true);
        Assert.Equal(4, merged.Shown.Count);
        Assert.Equal(new[] { "acad-pilot-7721", "acad-pilot-7720", "acad-4812", "acad-pilot-7722" }.OrderBy(x => x).ToArray(),
            merged.Shown.Select(m => m.Entry.Id).OrderBy(x => x).ToArray());
        Assert.Equal("acad-pilot-7721", merged.Top!.Entry.Id);   // waiting-input outranks running and idle
    }

    [Fact]
    public void TheTooltipHasOneLinePerPilotEntry()
    {
        var entries = Enumerable.Range(7720, 4).Select(p => Pilot(p, "running", "run " + p)).ToArray();
        var view = CompactText.Build(TestMerge.Merge(entries, Now, _ => true), Now);
        var lines = view.Tooltip.Split('\n');
        Assert.Equal(4, lines.Length);
        Assert.All(lines, l => Assert.StartsWith("acad-pilot-77", l));
        Assert.Equal(4, lines.Distinct().Count());
        Assert.Equal(Tone.Blue, view.Tone);
    }

    [Fact]
    public void TheDetailsBlocksAndTheDumpListEachPilotEntryWithItsDetail()
    {
        var entries = new[] { Pilot(7720, "running", "run: ZOOM E"), Pilot(7721, "running", "run: PLOT") };
        var merged = TestMerge.Merge(entries, Now, _ => true);
        var blocks = merged.Shown.Select(m => StatusDump.Block(m, Now)).ToList();
        Assert.Equal(2, blocks.Select(b => b.Lead).Distinct().Count());
        Assert.All(blocks, b => Assert.Contains(b.Lines, l => l.StartsWith("detail: {\"script\":\"run\"", StringComparison.Ordinal)));
        var dump = StatusDump.Full("D:\\x", merged, Array.Empty<Unreadable>(), Now);
        Assert.Contains("### acad-pilot-7720.json", dump);
        Assert.Contains("### acad-pilot-7721.json", dump);
    }

    [Fact]
    public void ADeadPilotScriptsFileIsIgnoredAndTheLiveOnesAreNot()
    {
        var live = Pilot(7720, "running", "run");
        var dead = StatusParser.Parse("acad-pilot-7721.json", TestData.Json("acad-pilot", 7721, Now.AddMinutes(-10), 30, "running", "run")).Entry!;
        var merged = TestMerge.Merge(new[] { live, dead }, Now, pid => pid == 7720);
        Assert.Equal("acad-pilot-7720", Assert.Single(merged.Shown).Entry.Id);
        Assert.Equal("acad-pilot-7721.json", Assert.Single(merged.Ignored).Entry.File);
    }
}
