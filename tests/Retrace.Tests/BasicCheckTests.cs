using System.Text.Json;
using Retrace.Core;

namespace Retrace.Tests;

/// <summary>The model-space check setting (checkModelSpace) and the end-of-test basic check. Synthetic data only.</summary>
public class BasicCheckTests
{
    static DrawingDigest Make(int lines, string[] layers, string geometry = "g")
        => DrawingDigest.Build(Enumerable.Range(0, lines).Select(i => ("LINE", "0", geometry + i)), layers);

    // ---- Compare with checkModelSpace off ----

    [Fact]
    public void Model_space_off_ignores_counts_and_geometry_but_keeps_layer_names()
    {
        var expected = Make(2, new[] { "0", "DIM" });
        var moved = Make(5, new[] { "0", "DIM" }, "other");
        Assert.NotEmpty(DrawingDigest.Compare(expected, moved));
        Assert.Empty(DrawingDigest.Compare(expected, moved, checkModelSpace: false));
        Assert.Equal(new[] { "layer DIM missing" }, DrawingDigest.Compare(expected, Make(5, new[] { "0" }), checkModelSpace: false));
        Assert.Equal(new[] { "layer TMP unexpected" }, DrawingDigest.Compare(expected, Make(2, new[] { "0", "DIM", "TMP" }), checkModelSpace: false));
    }

    [Fact]
    public void Model_space_off_still_compares_layer_state()
    {
        var on = new Dictionary<string, LayerProps> { ["0"] = new() { Frozen = false } };
        var frozen = new Dictionary<string, LayerProps> { ["0"] = new() { Frozen = true } };
        var e = DrawingDigest.Build(new[] { ("LINE", "0", "a") }, new[] { "0" }, on);
        var a = DrawingDigest.Build(new[] { ("LINE", "0", "b"), ("LINE", "0", "c") }, new[] { "0" }, frozen);
        Assert.Equal(new[] { "layer 0: expected thawed, got frozen" }, DrawingDigest.Compare(e, a, checkModelSpace: false));
    }

    [Fact]
    public void Nothing_left_to_verify_is_reported_by_HasNonEntityPart()
    {
        Assert.True(DrawingDigest.HasNonEntityPart(Make(1, new[] { "0" })));
        Assert.False(DrawingDigest.HasNonEntityPart(Make(1, Array.Empty<string>())));
        var withState = DrawingDigest.Build(new[] { ("LINE", "0", "a") }, Array.Empty<string>(), new Dictionary<string, LayerProps> { ["0"] = new() { On = true } });
        Assert.True(DrawingDigest.HasNonEntityPart(withState));
        Assert.False(DrawingDigest.HasNonEntityPart(withState, compareLayerState: false));
    }

    // ---- the result ----

    [Fact]
    public void A_failed_basic_check_fails_the_test_and_a_passing_one_can_pass_it()
    {
        var pass = new CheckpointResult { Pass = true };
        var fail = new BasicCheckResult { Pass = false };
        var ok = new BasicCheckResult { Pass = true };
        var none = new BasicCheckResult { Pass = null };
        Assert.Equal(ReplayResult.Pass, ReplayResult.ComputeVerdict(true, false, new[] { pass }, 0, ok));
        Assert.Equal(ReplayResult.Fail, ReplayResult.ComputeVerdict(true, false, new[] { pass }, 0, fail));
        Assert.Equal(ReplayResult.Pass, ReplayResult.ComputeVerdict(true, false, new[] { pass }, 0, none));
        Assert.Equal(ReplayResult.Pass, ReplayResult.ComputeVerdict(true, false, new[] { new CheckpointResult { Pass = null } }, 0, ok));   // all not verified: the basic check decides
        Assert.Equal(ReplayResult.Incomplete, ReplayResult.ComputeVerdict(true, false, new[] { new CheckpointResult { Pass = null } }, 0, none));
        Assert.Equal(ReplayResult.Incomplete, ReplayResult.ComputeVerdict(true, false, new[] { pass, new CheckpointResult { Pass = null } }, 0, ok));   // one verified, one not: unchanged
        Assert.Equal(ReplayResult.Incomplete, ReplayResult.ComputeVerdict(true, false, Array.Empty<CheckpointResult>(), 0, none));
        Assert.Equal(ReplayResult.Pass, ReplayResult.ComputeVerdict(true, false, Array.Empty<CheckpointResult>(), 0, ok));
        Assert.Equal(ReplayResult.Fail, ReplayResult.ComputeVerdict(true, false, Array.Empty<CheckpointResult>(), 0, fail));
        Assert.Equal(ReplayResult.Error, ReplayResult.ComputeVerdict(true, true, new[] { pass }, 0, fail));
    }

    [Fact]
    public void Result_json_has_basicCheck_and_checkModelSpace_only_when_set()
    {
        var plain = JsonDocument.Parse(new ReplayResult().ToJson()).RootElement;
        Assert.False(plain.TryGetProperty("basicCheck", out _));
        Assert.False(plain.TryGetProperty("checkModelSpace", out _));
        var r = new ReplayResult { CheckModelSpace = false, BasicCheck = BasicCheckRules.Evaluate(BasicCheckRules.AllItems, new(3, 2, 1), new(3, 4, 1), BasicCheckRules.FromEndCounts) };
        var j = JsonDocument.Parse(r.ToJson()).RootElement;
        Assert.False(j.GetProperty("checkModelSpace").GetBoolean());
        var b = j.GetProperty("basicCheck");
        Assert.False(b.GetProperty("pass").GetBoolean());
        Assert.Equal("end_counts", b.GetProperty("source").GetString());
        var layers = b.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("item").GetString() == "layers");
        Assert.Equal(2, layers.GetProperty("expected").GetInt32());
        Assert.Equal(4, layers.GetProperty("actual").GetInt32());
        Assert.False(layers.GetProperty("pass").GetBoolean());
    }

    // ---- the comparison ----

    [Fact]
    public void Matching_counts_pass_and_the_line_reads_like_the_console_line()
    {
        var r = BasicCheckRules.Evaluate(BasicCheckRules.AllItems, new(12, 5, 2), new(12, 5, 2), BasicCheckRules.FromEndCounts);
        Assert.True(r.Pass);
        Assert.Equal("Basic check: model space 12 = 12, layers 5 = 5, layouts 2 = 2", r.Line());
    }

    [Fact]
    public void A_different_count_fails_with_the_right_line()
    {
        var r = BasicCheckRules.Evaluate(BasicCheckRules.AllItems, new(12, 5, 2), new(12, 3, 2), BasicCheckRules.FromEndCounts);
        Assert.False(r.Pass);
        Assert.Equal("Basic check: model space 12 = 12, layers 5 ≠ 3, layouts 2 = 2", r.Line());
        Assert.Equal(new bool?[] { true, false, true }, r.Items.Select(i => i.Pass).ToArray());
    }

    [Fact]
    public void Only_the_chosen_items_are_compared_in_a_fixed_order()
    {
        var r = BasicCheckRules.Evaluate(new[] { "LAYOUTS", "modelspace", "bogus", "layouts" }, new(1, 1, 1), new(2, 9, 1), BasicCheckRules.FromEndCounts);
        Assert.Equal(new[] { "modelspace", "layouts" }, r.Items.Select(i => i.Item).ToArray());
        Assert.False(r.Pass);
    }

    [Fact]
    public void An_item_with_an_unknown_side_is_not_verified()
    {
        var r = BasicCheckRules.Evaluate(BasicCheckRules.AllItems, new(12, 5, null), new(12, 5, 2), BasicCheckRules.FromLastCheckpoint);
        Assert.True(r.Pass);
        Assert.Null(r.Items.Single(i => i.Item == "layouts").Pass);
        Assert.Equal("Basic check: model space 12 = 12, layers 5 = 5, layouts not verified", r.Line());
        var nothing = BasicCheckRules.Evaluate(BasicCheckRules.AllItems, new(null, null, null), new(1, 1, 1), BasicCheckRules.FromNothing);
        Assert.Null(nothing.Pass);
        var unread = BasicCheckRules.Evaluate(BasicCheckRules.AllItems, new(1, 1, 1), null, BasicCheckRules.FromEndCounts);
        Assert.Null(unread.Pass);
    }

    // ---- the expected counts of a recording ----

    static List<JsonElement> Events(params string[] lines) => lines.Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();

    [Fact]
    public void The_end_counts_event_is_the_expected_state()
    {
        var (c, source, _) = BasicCheckRules.ExpectedFrom(Events(
            """{"type":"state","state":{"count":1,"layers":["0"]}}""",
            """{"type":"mark","state":{"count":7,"layers":["0","A"]}}""",
            """{"type":"end_counts","modelspace":12,"layers":5,"layouts":2}""",
            """{"type":"session_end"}"""));
        Assert.Equal("end_counts", source);
        Assert.Equal(new BasicCheckRules.Counts(12, 5, 2), c);
    }

    [Fact]
    public void An_older_recording_falls_back_to_the_last_checkpoint_and_layouts_are_unknown()
    {
        var (c, source, note) = BasicCheckRules.ExpectedFrom(Events(
            """{"type":"state","state":{"count":1,"layers":["0"]}}""",
            """{"type":"cmd_start","command":"LINE"}""",
            """{"type":"mark","state":{"count":4,"layers":["0"]}}""",
            """{"type":"cmd_start","command":"CIRCLE"}""",
            """{"type":"mark","state":{"count":7,"layers":["0","A","XR|WALLS","B"]}}""",
            """{"type":"cmd_start","command":"-RETRACEMARK"}""",
            """{"type":"ribbon","id":"RETRACE_STOP","tabId":"RETRACE_TAB"}""",
            """{"type":"cmd_start","command":"-RETRACESTOP"}""",
            """{"type":"session_end"}"""));
        Assert.Null(note);
        Assert.Equal("lastCheckpoint", source);
        Assert.Equal(new BasicCheckRules.Counts(7, 3, null), c);
        var r = BasicCheckRules.Evaluate(BasicCheckRules.AllItems, c, new(7, 3, 2), source);
        Assert.True(r.Pass);
        Assert.Null(r.Items.Single(i => i.Item == "layouts").Pass);
    }

    [Fact]
    public void A_recording_with_neither_knows_nothing_and_the_start_state_is_not_used()
    {
        var (c, source, _) = BasicCheckRules.ExpectedFrom(Events("""{"type":"state","state":{"count":1,"layers":["0"]}}""", """{"type":"mark","note":"x"}"""));
        Assert.Equal("none", source);
        Assert.Equal(new BasicCheckRules.Counts(null, null, null), c);
    }

    [Theory]
    [InlineData("""{"type":"cmd_start","command":"ERASE"}""")]
    [InlineData("""{"type":"lisp_start"}""")]
    [InlineData("""{"type":"ribbon","id":"ID_LINE","tabId":"HOME"}""")]
    [InlineData("""{"type":"palette_click","button":"Go"}""")]
    [InlineData("""{"type":"dialog_click","button":"OK"}""")]
    [InlineData("""{"type":"doc_created","path":"a.dwg"}""")]
    public void Steps_after_the_last_checkpoint_make_the_old_fallback_not_verified(string after)
    {
        var (c, source, note) = BasicCheckRules.ExpectedFrom(Events("""{"type":"mark","state":{"count":4,"layers":["0"]}}""", after, """{"type":"session_end"}"""));
        Assert.Equal("none", source);
        Assert.Equal(BasicCheckRules.StepsAfterLastCheckpoint, note);
        var r = BasicCheckRules.Evaluate(BasicCheckRules.AllItems, c, new(9, 9, 9), source, note);
        Assert.Null(r.Pass);
        Assert.All(r.Items, i => Assert.Null(i.Pass));
        Assert.Contains("recorded before end counts existed; steps after the last checkpoint", r.Line());
    }

    [Fact]
    public void Steps_before_a_later_checkpoint_do_not_matter()
    {
        var (c, source, note) = BasicCheckRules.ExpectedFrom(Events("""{"type":"cmd_start","command":"LINE"}""", """{"type":"mark","state":{"count":2,"layers":["0"]}}""", """{"type":"cmd_start","command":"RETRACESTOP"}"""));
        Assert.Equal("lastCheckpoint", source);
        Assert.Null(note);
        Assert.Equal(2, c.ModelSpace);
    }

    // ---- settings ----

    [Fact]
    public void New_settings_default_load_and_save()
    {
        var d = new RetraceSettings();
        Assert.True(d.CheckModelSpace);
        Assert.False(d.BasicCheck);
        Assert.Equal(new[] { "modelspace", "layers", "layouts" }, d.EffectiveBasicCheckItems());
        var root = Path.Combine(Path.GetTempPath(), "retrace-basic-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Paths.SettingsFile(root), "{ \"screenshots\": false }");   // keys missing: the defaults
            var old = RetraceSettings.Load(root);
            Assert.True(old.CheckModelSpace);
            Assert.False(old.BasicCheck);
            Assert.Equal(3, old.EffectiveBasicCheckItems().Count);
            var s = new RetraceSettings { RootDir = root, CheckModelSpace = false, BasicCheck = true, BasicCheckItems = new() { "Layers", "layers", "nope", "layouts" } };
            Assert.True(s.Save(out var err), err);
            var back = RetraceSettings.Load(root);
            Assert.False(back.CheckModelSpace);
            Assert.True(back.BasicCheck);
            Assert.Equal(new[] { "layers", "layouts" }, back.EffectiveBasicCheckItems());
            var clone = back.Clone();
            clone.BasicCheckItems.Clear();
            Assert.NotEmpty(back.BasicCheckItems);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
