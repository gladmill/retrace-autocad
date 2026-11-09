using System;
using System.Linq;
using AcadStatus.Core;

namespace AcadStatus.Tests;

public class StatusDumpTests
{
    static DateTime Now => TestData.Now;

    static MergeResult Merge(params StatusEntry[] entries) => TestMerge.Merge(entries, Now, _ => true);

    static StatusEntry AcadEntry(Snapshot s, string? doc = null)
    {
        var b = new AcadStatusBuilder("AcadStatus 0.1.0", 4812);
        var d = b.Build(s, Now);
        return StatusParser.Parse("acad-4812.json", d.Json).Entry!;
    }

    [Fact]
    public void ABlockHasAHeaderAndLines()
    {
        var m = Merge(TestData.Entry("retrace", 4812, updated: Now.AddSeconds(-2), state: "running", summary: "Suite 3/7: smoke040", owner: "COM", file: "retrace-4812.json")).Shown.Single();
        var b = StatusDump.Block(m, Now);
        Assert.Equal(Tone.Blue, b.Tone);
        Assert.Equal("retrace-4812  running  2s ago", b.Header);
        Assert.Equal("Suite 3/7: smoke040", b.Lines[0]);
        Assert.Equal("owner COM, Retrace 0.6.5, pid 4812, ttl 15s", b.Lines[1]);
    }

    [Fact]
    public void ABlockedBlockSaysHowLong()
    {
        var e = TestData.Entry("retrace", 1, state: "blocked", since: Now.AddSeconds(-151), file: "retrace-1.json");
        Assert.EndsWith("blocked 2:31", StatusDump.Block(Merge(e).Shown.Single(), Now).Header);
    }

    [Fact]
    public void AStaleBlockSaysNoReply()
    {
        var e = TestData.Entry("retrace", 1, state: "running", updated: Now.AddSeconds(-40), ttl: 5, file: "retrace-1.json");
        var h = StatusDump.Block(Merge(e).Shown.Single(), Now).Header;
        Assert.Equal("retrace-1  busy (stale running)  no reply for 40s", h);
    }

    [Fact]
    public void TheAcadBlockListsTheDialogItsTextsAndButtons()
    {
        var d = TestData.Dialog(texts: new[] { "status board test" }, buttons: new[] { "OK", "Cancel" });
        var e = AcadEntry(TestData.Snap(doc: "fx-all.dwg", docs: 2, cmdActive: 1, cmdNames: "OFFSET", prompt: "Specify point:", dialog: d, dialogSince: Now.AddSeconds(-12)));
        var b = StatusDump.Block(Merge(e).Shown.Single(), Now);
        var all = string.Join("\n", b.Lines);
        Assert.Contains("drawing fx-all.dwg (2 open), command OFFSET, CMDACTIVE 1", all);
        Assert.Contains("prompt: Specify point:", all);
        Assert.Contains("dialog: \"AutoCAD Message\" (#32770, modal, since 10:13:51Z)", all);
        Assert.Contains("  text: status board test", all);
        Assert.Contains("  buttons: OK | Cancel", all);
    }

    [Fact]
    public void OnlyTheTimingChangesWhileAnEntryStaysAsItIs()
    {
        var d = TestData.Dialog(texts: new[] { "status board test" });
        var e = AcadEntry(TestData.Snap(cmdActive: 32, dialog: d, dialogSince: Now.AddSeconds(-20)));
        var later = Now.AddSeconds(7);
        var a = StatusDump.Block(TestMerge.Merge(new[] { e }, Now, _ => true).Shown.Single(), Now);
        var b = StatusDump.Block(TestMerge.Merge(new[] { e }, later, _ => true).Shown.Single(), later);
        Assert.Equal(a.Lead, b.Lead);
        Assert.Equal(a.Lines, b.Lines);          // what a window rebuilds from: the same
        Assert.Equal(a.Tone, b.Tone);
        Assert.NotEqual(a.Timing, b.Timing);     // what it replaces in place: the age (and how long it has been blocked)
        Assert.Equal(a.Header, a.Lead + a.Timing);
    }

    [Fact]
    public void ABlockedEntrysClockIsPartOfTheTimingNotTheLead()
    {
        var e = TestData.Entry("retrace", 1, state: "blocked", since: Now.AddSeconds(-151), file: "retrace-1.json");
        var b = StatusDump.Block(Merge(e).Shown.Single(), Now);
        Assert.Equal("retrace-1  blocked  ", b.Lead);
        Assert.Equal("0s ago  blocked 2:31", b.Timing);
    }

    [Fact]
    public void AnAcadBlockWithoutDialogOrPromptHasNeither()
    {
        var all = string.Join("\n", StatusDump.Block(Merge(AcadEntry(TestData.Snap())).Shown.Single(), Now).Lines);
        Assert.DoesNotContain("dialog", all);
        Assert.DoesNotContain("prompt", all);
    }

    [Fact]
    public void ADetailOfAnotherSourceIsShownAsOneLine()
    {
        var e = StatusParser.Parse("retrace-1.json", TestData.Json(extra: ", \"detail\": { \"mode\": \"suite\", \"suite\": { \"index\": 3, \"count\": 7 } }")).Entry!;
        var line = StatusDump.Block(Merge(e).Shown.Single(), Now).Lines.Last();
        Assert.StartsWith("detail: {\"mode\":\"suite\"", line);
    }

    [Fact]
    public void TheTextListsEntriesIgnoredFilesAndSkippedFiles()
    {
        var live = TestData.Entry("acad", 1, state: "idle", file: "acad-1.json");
        var gone = TestData.Entry("retrace", 2, state: "running", updated: Now.AddHours(-3), ttl: 15, file: "retrace-2.json");
        var merged = TestMerge.Merge(new[] { live, gone }, Now, p => p == 1);
        var text = StatusDump.Text(merged, new[] { new Unreadable("bad-3.json", "not valid JSON (x)") }, Now);
        Assert.Contains("acad-1  idle", text);
        Assert.Contains("ignored: retrace-2.json (no update for 3h00m, process 2 gone)", text);
        Assert.Contains("skipped: bad-3.json (not valid JSON (x))", text);
    }

    [Fact]
    public void ManyIgnoredFilesAreCappedNewestFirst()
    {
        var entries = Enumerable.Range(1, 9).Select(i =>
            TestData.Entry("retrace", i, state: "running", updated: Now.AddMinutes(-10 * i), file: "retrace-" + i + ".json")).ToArray();
        var merged = TestMerge.Merge(entries, Now, _ => false);
        Assert.Equal(9, merged.Ignored.Count);
        var text = StatusDump.Text(merged, Array.Empty<Unreadable>(), Now);
        var listed = text.Split('\n').Where(l => l.StartsWith("ignored: ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(StatusDump.MaxListed, listed.Length);
        Assert.StartsWith("ignored: retrace-1.json", listed[0]);   // the newest first
        Assert.StartsWith("ignored: retrace-5.json", listed[4]);
        Assert.Contains("... and 4 more ignored", text);
        var full = StatusDump.Full("D:\\x", merged, Array.Empty<Unreadable>(), Now);
        Assert.Equal(StatusDump.MaxListed, full.Split('\n').Count(l => l.StartsWith("### ", StringComparison.Ordinal)));
    }

    [Fact]
    public void ManySkippedFilesAreCapped()
    {
        var skipped = Enumerable.Range(1, 8).Select(i => new Unreadable("bad-" + i + ".json", "not valid JSON")).ToArray();
        var text = StatusDump.Text(Merge(), skipped, Now);
        Assert.Equal(StatusDump.MaxListed, text.Split('\n').Count(l => l.StartsWith("skipped: ", StringComparison.Ordinal)));
        Assert.Contains("... and 3 more skipped", text);
    }

    [Fact]
    public void ThePaneNoteIsInTheHeaderWhenGiven()
    {
        Assert.Contains("Pane: added (dot and text pane at 56 and 57 of 58)",
            StatusDump.Full("D:\\x", Merge(), Array.Empty<Unreadable>(), Now, "added (dot and text pane at 56 and 57 of 58)"));
        Assert.DoesNotContain("Pane:", StatusDump.Full("D:\\x", Merge(), Array.Empty<Unreadable>(), Now));
    }

    [Fact]
    public void NoEntriesSaySo() =>
        Assert.StartsWith("No status entries.", StatusDump.Text(Merge(), Array.Empty<Unreadable>(), Now));

    [Fact]
    public void TheFullDumpEndsWithTheRawJsonOfEveryFile()
    {
        var a = AcadEntry(TestData.Snap(cmdActive: 1, cmdNames: "LINE", prompt: "Specify first point:"));
        var r = TestData.Entry("retrace", 4812, state: "running", file: "retrace-4812.json");
        var gone = TestData.Entry("retrace", 9, state: "running", updated: Now.AddHours(-3), file: "retrace-9.json");
        var merged = TestMerge.Merge(new[] { a, r, gone }, Now, p => p != 9);
        var dump = StatusDump.Full("C:\\ProgramData\\Gladmill\\acad-status", merged, Array.Empty<Unreadable>(), Now);
        Assert.StartsWith("AcadStatus dump 2026-11-05T10:14:03Z", dump);
        Assert.Contains("Folder: C:\\ProgramData\\Gladmill\\acad-status", dump);
        Assert.Contains("Top: acad-4812 waiting-input", dump);
        var raw = dump[dump.IndexOf("--- raw JSON ---", StringComparison.Ordinal)..];
        Assert.Contains("### acad-4812.json", raw);
        Assert.Contains("### retrace-4812.json", raw);
        Assert.Contains("### retrace-9.json", raw);
        Assert.Contains("\"prompt\": \"Specify first point:\"", raw);
        Assert.EndsWith("\n", dump);
    }

    [Fact]
    public void TheFullDumpOfNothingIsStillADump()
    {
        var dump = StatusDump.Full("D:\\x", Merge(), Array.Empty<Unreadable>(), Now);
        Assert.Contains("No status entries.", dump);
        Assert.DoesNotContain("Top:", dump);
    }
}
