using System.Text.Json;
using Digest = Retrace.Core.DrawingDigest;
using RecPlan = Retrace.Core.Plan;
using RecResult = Retrace.Core.ReplayResult;
using RecCheckpoint = Retrace.Core.CheckpointResult;

namespace Retrace.Tests;

/// <summary>The AutoCAD-free parts of the recordings plugin core (linked source files): digest, comparison, plan, result.</summary>
public class RecordingCheckpointTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("retrace-reccore").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    static (string, string, string) E(string dxf, string layer, double x1, double y1, double x2, double y2, string? extra = null) =>
        (dxf, layer, Digest.Key(dxf, layer, (x1, y1, 0, x2, y2, 0), extra));

    static Digest Make(params (string, string, string)[] ents) => Digest.Build(ents, new[] { "0", "DIM" }.Concat(ents.Select(e => e.Item2)));

    [Fact]
    public void EqualDrawingsHaveNoDifferencesAndOrderDoesNotMatter()
    {
        var a = Make(E("LINE", "0", 0, 0, 10, 0), E("CIRCLE", "0", -5, -5, 5, 5));
        var b = Make(E("CIRCLE", "0", -5, -5, 5, 5), E("LINE", "0", 0, 0, 10, 0));
        Assert.Equal(a.Hash, b.Hash);
        Assert.Equal(16, a.Hash.Length);
        Assert.Empty(Digest.Compare(a, b));
    }

    [Fact]
    public void RoundsTo1eMinus4AndNormalisesNegativeZero()
    {
        var a = Make(E("LINE", "0", 0.00001, -0.00001, 10.00004, 0));
        var b = Make(E("LINE", "0", 0, 0, 10, 0));
        Assert.Equal(b.Hash, a.Hash);
        var c = Make(E("LINE", "0", 0, 0, 10.001, 0));
        Assert.NotEqual(b.Hash, c.Hash);
        Assert.Equal("0", Digest.Num(-0.00001));
    }

    [Fact]
    public void ReportsTypeLayerAndHashDifferences()
    {
        var exp = Make(E("LINE", "0", 0, 0, 1, 1), E("LINE", "0", 1, 1, 2, 2));
        var act = Digest.Build(new[] { E("LINE", "0", 0, 0, 1, 1), E("LINE", "0", 1, 1, 2, 2), E("LINE", "TMP", 5, 5, 6, 6) }, new[] { "0", "TMP" });
        var d = Digest.Compare(exp, act);
        Assert.Contains("LINE: expected 2, got 3", d);
        Assert.Contains("layer DIM missing", d);
        Assert.Contains("layer TMP unexpected", d);
        Assert.Contains("layer TMP: expected 0 entities, got 1", d);
        Assert.DoesNotContain("geometry hash differs", d); // counts already explain it

        // same counts and layers, different geometry: only the hash tells
        var moved = Make(E("LINE", "0", 0, 0, 1, 1), E("LINE", "0", 1, 1, 3, 3));
        Assert.Equal(new[] { "geometry hash differs" }, Digest.Compare(exp, moved));

        // a type missing entirely
        var none = Make();
        Assert.Contains("LINE: expected 2, got 0", Digest.Compare(exp, none));
    }

    [Fact]
    public void KeysIncludeTextBlockAndLength()
    {
        Assert.NotEqual(E("TEXT", "0", 0, 0, 1, 1, "text=A").Item3, E("TEXT", "0", 0, 0, 1, 1, "text=B").Item3);
        Assert.Contains("noext", Digest.Key("XLINE", "0", null, null));
    }

    static string Label(double y = 20, string text = "Room 101") => Digest.PlacedKey("TEXT", "0", $"at={Digest.Pt(15, y, 0)} h=3 rot=0", "text=" + text);

    [Fact]
    public void PlacedKeysJoinPlacementAndTextAndRoundPoints()
    {
        Assert.Equal("TEXT|0|at=15,20,0 h=3 rot=0|text=Room 101", Label());
        Assert.NotEqual(Label(), Label(y: 21));
        Assert.NotEqual(Label(), Label(text: "Room 102"));
        Assert.Equal("15,20,0", Digest.Pt(15.00001, 20, -0.00001));
        Assert.NotEqual(Label(), Digest.Key("TEXT", "0", (15, 20, 0, 35.5, 23, 0), "text=Room 101"));
    }

    [Fact]
    public void ShortHashIgnoresOrderAndMatchesBuild()
    {
        Assert.Equal(Digest.ShortHash(new[] { "b", "a" }), Digest.ShortHash(new[] { "a", "b" }));
        Assert.NotEqual(Digest.ShortHash(new[] { "a" }), Digest.ShortHash(new[] { "b" }));
        var ents = new[] { E("LINE", "0", 0, 0, 1, 1), E("CIRCLE", "0", 0, 0, 2, 2) };
        Assert.Equal(Digest.ShortHash(ents.Select(e => e.Item3)), Make(ents).Hash);
    }

    [Fact]
    public void DigestVersionIsWrittenAndOldDigestsReadAsVersion1()
    {
        var d = Make(E("LINE", "0", 0, 0, 1, 1));
        Assert.Equal(Digest.CurrentVersion, d.Version);
        Assert.Contains("\"v\":2", JsonSerializer.Serialize(d));
        var old = Digest.FromJson(JsonDocument.Parse("{\"count\":1,\"byType\":{\"LINE\":1},\"byLayer\":{\"0\":1},\"layers\":[\"0\"],\"hash\":\"x\"}").RootElement)!;
        Assert.Equal(1, old.Version);
    }

    [Fact]
    public void OldCheckpointWithTextSaysRecordAgain()
    {
        var text = (dxf: "TEXT", layer: "0", key: Label());
        var now = Make(E("LWPOLYLINE", "0", 10, 10, 40, 30), text);
        var oldText = Make(E("LWPOLYLINE", "0", 10, 10, 40, 30), E("TEXT", "0", 15, 20, 35.5, 23, "text=Room 101"));
        oldText.Version = 1;
        Assert.Contains("record the test again", Assert.Single(Digest.Compare(oldText, now)));
        var oldBlock = Make(E("INSERT", "0", 0, 0, 5, 5, "block=MOTOR")); oldBlock.Version = 1;
        Assert.Contains("record the test again", Assert.Single(Digest.Compare(oldBlock, Make((dxf: "INSERT", layer: "0", key: Digest.PlacedKey("INSERT", "0", "at=0,0,0", "block=MOTOR"))))));
        var oldArc = Make(E("ARC_DIMENSION", "0", 0, 0, 5, 5)); oldArc.Version = 1;
        Assert.Contains("record the test again", Assert.Single(Digest.Compare(oldArc, Make((dxf: "ARC_DIMENSION", layer: "0", key: Digest.PlacedKey("ARC_DIMENSION", "0", "measure=5", "text="))))));
        var oldNoText = Make(E("LINE", "0", 0, 0, 1, 1)); oldNoText.Version = 1;
        Assert.Equal(new[] { "geometry hash differs" }, Digest.Compare(oldNoText, Make(E("LINE", "0", 0, 0, 2, 2))));
        Assert.Empty(Digest.Compare(oldNoText, Make(E("LINE", "0", 0, 0, 1, 1))));   // keys without text are unchanged
    }

    [Fact]
    public void SummaryAndJsonRoundTrip()
    {
        var d = Make(E("LINE", "0", 0, 0, 1, 1), E("LINE", "0", 1, 1, 2, 2), E("CIRCLE", "0", 0, 0, 1, 1));
        Assert.StartsWith("2 LINE, 1 CIRCLE; layers: 0", d.Summary());
        var json = JsonSerializer.Serialize(d);
        Assert.Contains("\"byType\"", json);
        var back = Digest.FromJson(JsonDocument.Parse(json).RootElement)!;
        Assert.Equal(d.Hash, back.Hash);
        Assert.Empty(Digest.Compare(d, back));
        Assert.Null(Digest.FromJson(JsonDocument.Parse("3").RootElement));
    }

    [Fact]
    public void PlanCarriesInitialStateAndCheckpointStates()
    {
        var d = Make(E("LINE", "0", 0, 0, 1, 1));
        var js = JsonSerializer.Serialize(d);
        var lines = new[]
        {
            """{"type":"header","schema":1}""",
            "{\"t\":1,\"type\":\"state\",\"state\":" + js + "}",
            """{"t":10,"type":"cmd_start","command":"LINE","origin":"typed/other"}""",
            """{"t":20,"type":"cmd_end","command":"LINE"}""",
            "{\"t\":30,\"type\":\"mark\",\"note\":\"\",\"state\":" + js + "}",
            """{"t":40,"type":"mark","note":"old"}""",
        };
        var path = Path.Combine(_dir, "p.jsonl");
        File.WriteAllLines(path, lines);
        var events = RecPlan.ReadEvents(path);
        Assert.Equal(d.Hash, RecPlan.InitialState(events)!.Hash);
        var steps = RecPlan.Build(events);
        Assert.Equal(new[] { "command", "mark", "mark" }, steps.Select(s => s.Kind));
        Assert.Equal(d.Hash, steps[1].State!.Hash);
        Assert.Equal("Checkpoint", steps[1].Title);
        Assert.Null(steps[2].State);
        Assert.Null(RecPlan.InitialState(RecPlan.ReadEvents(path).Where(e => RecPlan.Str(e, "type") != "state")));
    }

    static RecCheckpoint Cp(bool? pass) => new() { Index = 1, Pass = pass };

    [Fact]
    public void VerdictRules()
    {
        Assert.Equal("PASS", RecResult.ComputeVerdict(true, false, new[] { Cp(true), Cp(true) }));
        Assert.Equal("FAIL", RecResult.ComputeVerdict(true, false, new[] { Cp(true), Cp(false) }));
        Assert.Equal("FAIL", RecResult.ComputeVerdict(false, false, new[] { Cp(false) }));
        Assert.Equal("INCOMPLETE", RecResult.ComputeVerdict(false, false, new[] { Cp(true) }));
        Assert.Equal("INCOMPLETE", RecResult.ComputeVerdict(true, false, new[] { Cp(true), Cp(null) }));
        Assert.Equal("INCOMPLETE", RecResult.ComputeVerdict(true, false, Array.Empty<RecCheckpoint>()));
        Assert.Equal("ERROR", RecResult.ComputeVerdict(false, true, new[] { Cp(false) }));
    }

    [Fact]
    public void WritesResultsIntoTheTestFolderAndSummarises()
    {
        var folder = Path.Combine(_dir, "2026-10-09_1200_demo");
        Directory.CreateDirectory(folder);
        var session = Path.Combine(folder, "recording.jsonl");
        File.WriteAllText(session, "");
        var exp = Make(E("LINE", "0", 0, 0, 1, 1));
        var r = new RecResult
        {
            Session = "2026-10-09_1200_demo", CoreVersion = "0.1.0", StepsTotal = 4, StepsRun = 4, StartStateMatches = true,
            Checkpoints = { new RecCheckpoint { Index = 1, Note = "n", Pass = true, Expected = exp, Actual = exp }, new RecCheckpoint { Index = 2, Pass = true, Expected = exp, Actual = exp } },
        };
        r.Verdict = RecResult.ComputeVerdict(true, false, r.Checkpoints);
        Assert.Equal("RETRACEREPLAY: PASS 2/2 checkpoints (session 2026-10-09_1200_demo)", r.Summary());
        var written = r.Write(session);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}_PASS\.json$", Path.GetFileName(written!));
        Assert.Equal(Path.Combine(folder, "results"), Path.GetDirectoryName(written));
        Assert.Equal(File.ReadAllText(written!), File.ReadAllText(Path.Combine(folder, "results", "latest.json")));
        Assert.False(File.Exists(written + ".tmp"));
        using var doc = JsonDocument.Parse(File.ReadAllText(written!));
        var root = doc.RootElement;
        Assert.Equal("PASS", root.GetProperty("verdict").GetString());
        Assert.Equal(4, root.GetProperty("stepsRun").GetInt32());
        Assert.True(root.GetProperty("startStateMatches").GetBoolean());
        Assert.Equal(2, root.GetProperty("checkpoints").GetArrayLength());
        Assert.Equal(exp.Hash, root.GetProperty("checkpoints")[0].GetProperty("expected").GetProperty("hash").GetString());

        r.StartStateMatches = null;
        Assert.DoesNotContain("startStateMatches", r.ToJson());
    }
}
