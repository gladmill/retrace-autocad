using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AcadStatus.Core;

namespace AcadStatus.Tests;

/// <summary>The examples in CONTRACT.md must parse with the reader, and the amended rules must hold.</summary>
public class ContractTests
{
    static string ContractPath([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "CONTRACT.md"));

    static List<string> JsonBlocks()
    {
        var text = File.ReadAllText(ContractPath());
        return Regex.Matches(text, "```json\\s*(.*?)```", RegexOptions.Singleline).Select(m => m.Groups[1].Value.Trim()).ToList();
    }

    [Fact]
    public void TheContractFileIsThere() => Assert.True(File.Exists(ContractPath()), ContractPath());

    [Fact]
    public void TheRetraceExampleIsAValidFile()
    {
        var example = JsonBlocks().Single(b => b.Contains("\"source\": \"retrace\""));
        var r = StatusParser.Parse("retrace-4812.json", example);
        Assert.True(r.Ok, r.Error);
        var e = r.Entry!;
        Assert.Equal("Suite 3/7", e.Short);
        Assert.Equal("COM", e.Owner);
        Assert.Equal(15, e.TtlSec);
        Assert.Equal(new DateTime(2026, 11, 5, 10, 13, 40, DateTimeKind.Utc), e.Since);
        Assert.Contains("\"suite\"", e.DetailJson);
        // 10 s after its last write it is live, 16 s after it is stale
        var at10 = TestMerge.Merge(new[] { e }, e.Updated.AddSeconds(10), _ => true).Shown.Single();
        Assert.False(at10.Stale);
        Assert.Equal("Suite 3/7", CompactText.TextOf(at10, e.Updated.AddSeconds(10)));
        var at16 = TestMerge.Merge(new[] { e }, e.Updated.AddSeconds(16), _ => true).Shown.Single();
        Assert.True(at16.Stale);
    }

    [Fact]
    public void TheAcadDetailExampleIsReadByTheAcadParser()
    {
        var detail = JsonBlocks().Single(b => b.Contains("\"cmdActive\""));
        var json = TestData.Json("acad", 4812, extra: ", \"detail\": " + detail);
        var e = StatusParser.Parse("acad-4812.json", json).Entry!;
        Assert.Equal("fx-all.dwg", e.Acad!.Doc);
        Assert.Equal("OFFSET", e.Acad.CmdNames);
        Assert.Equal("Specify point on side to offset or [Exit/Multiple/Undo] <Exit>:", e.Acad.Prompt);
        Assert.Equal("AutoCAD Message", e.Acad.Dialog!.Title);
        Assert.Equal(new[] { "OK" }, e.Acad.Dialog.Buttons);
    }

    [Fact]
    public void TheOtherDetailExamplesAreValidJson()
    {
        foreach (var b in JsonBlocks())
            using (var doc = System.Text.Json.JsonDocument.Parse(b)) Assert.Equal(System.Text.Json.JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    public void TheContractSaysWhatTheAmendedRulesSay()
    {
        var text = File.ReadAllText(ContractPath());
        Assert.Contains("A `stopped` entry is never ranked `busy`", text);
        Assert.Contains("^[a-z0-9][a-z0-9-]{0,39}\\.json$", text);
        Assert.Contains("`v` is not an integer >= 1", text);
    }

    [Fact]
    public void TheContractsStatesAreTheOnesTheCodeKnows()
    {
        var text = File.ReadAllText(ContractPath());
        var line = text.Split('\n').Single(l => l.StartsWith("Display priority, highest first:", StringComparison.Ordinal));
        var states = Regex.Matches(line, "`([a-z-]+)`").Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(new[] { "blocked", "needs-person", "waiting-input", "busy", "error", "running", "idle", "stopped" }, states);
        Assert.All(states, s => Assert.True(States.IsKnown(s), s));
        var ranks = states.Select(States.Rank).ToArray();
        Assert.Equal(ranks.OrderByDescending(r => r).ToArray(), ranks);
    }

    [Fact]
    public void TheWritersOwnFileIsAValidFileOfTheContract()
    {
        // required fields of the contract table, all present in what AcadStatus writes
        var doc = new AcadStatusBuilder("AcadStatus 0.1.0", 4812).Build(TestData.Snap(), TestData.Now);
        using var json = System.Text.Json.JsonDocument.Parse(doc.Json);
        foreach (var f in new[] { "v", "source", "writer", "pid", "updated", "ttlSec", "state", "summary" })
            Assert.True(json.RootElement.TryGetProperty(f, out _), f);
        Assert.True(StatusFiles.IsStatusFileName(new AcadStatusBuilder("w", 4812).FileName));
    }
}
