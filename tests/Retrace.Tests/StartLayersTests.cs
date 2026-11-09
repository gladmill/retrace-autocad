using Retrace.Core;

namespace Retrace.Tests;

/// <summary>The start drawing of a RETRACERUN gets the layers the recording's start state held (<see cref="StartLayers"/>), and what a checkpoint says about a
/// layer table with and without them. Synthetic names only.</summary>
public class StartLayersTests
{
    static DrawingDigest Digest(string[] layers, params (string Layer, string Key)[] ents) =>
        DrawingDigest.Build(ents.Select(e => ("LINE", e.Layer, e.Key)), layers);

    static BehaviorSnapshot Snap(params (string Name, string Value)[] layers)
    {
        var s = new BehaviorSnapshot();
        s.Set(BehaviorCatalog.Layers, layers.Select(l => new KeyValuePair<string, string>(l.Name, l.Value)));
        return s;
    }

    static string Value(string on = "1", string frozen = "0", string locked = "0", string color = "7", string linetype = "Continuous", string lw = "Default", string plot = "1", string tr = "ByLayer", string desc = "") =>
        CanonicalValue.Join(("on", on), ("frozen", frozen), ("locked", locked), ("plot", plot), ("color", color), ("linetype", linetype), ("lineweight", lw), ("transparency", tr), ("description", desc));

    static readonly string[] Template = { "0", "Defpoints" };
    static bool NoLinetypes(string _) => false;

    [Fact]
    public void Missing_layers_are_planned_in_name_order_and_present_ones_skipped_case_insensitively()
    {
        var initial = Digest(new[] { "0", "defpoints", "L_B", "L_A" });
        var plan = StartLayers.Plan(initial, null, Template, NoLinetypes);
        Assert.Equal(new[] { "L_A", "L_B" }, plan.Create.Select(l => l.Name));
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Nothing_to_create_when_the_template_has_every_layer()
    {
        var plan = StartLayers.Plan(Digest(new[] { "0", "Defpoints" }), null, Template, NoLinetypes);
        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void Name_only_recording_creates_default_layers()
    {
        var l = Assert.Single(StartLayers.Plan(Digest(new[] { "0", "L_A" }), null, Template, NoLinetypes).Create);
        Assert.False(l.Described);
        Assert.Null(l.Color); Assert.Null(l.On); Assert.Null(l.Linetype);
    }

    [Fact]
    public void Snapshot_properties_are_carried_over()
    {
        var snap = Snap(("L_A", Value(on: "0", frozen: "1", locked: "1", plot: "0", color: "rgb:1,2,3", linetype: "Continuous", lw: "0.25", tr: "50", desc: "a; b = c")));
        var l = Assert.Single(StartLayers.Plan(Digest(new[] { "0", "L_A" }), snap, Template, NoLinetypes).Create);
        Assert.True(l.Described);
        Assert.False(l.On); Assert.True(l.Frozen); Assert.True(l.Locked); Assert.False(l.Plot);
        Assert.Equal("rgb:1,2,3", l.Color);
        Assert.Equal(25, l.Lineweight);
        Assert.Equal(50, l.Transparency);
        Assert.Equal("a; b = c", l.Description);
        Assert.Null(l.Linetype);   // Continuous = the default
    }

    [Fact]
    public void Snapshot_that_lacks_a_layer_falls_back_to_name_only_for_it()
    {
        var snap = Snap(("L_A", Value(color: "3")));
        var plan = StartLayers.Plan(Digest(new[] { "0", "L_A", "L_B" }), snap, Template, NoLinetypes);
        Assert.True(plan.Create.Single(l => l.Name == "L_A").Described);
        Assert.False(plan.Create.Single(l => l.Name == "L_B").Described);
    }

    [Fact]
    public void Linetype_in_the_drawing_is_kept_and_a_missing_one_becomes_continuous_with_one_warning()
    {
        var snap = Snap(("L_A", Value(linetype: "DASHED")), ("L_B", Value(linetype: "DASHED")), ("L_C", Value(linetype: "HIDDEN")));
        var have = StartLayers.Plan(Digest(new[] { "0", "L_A" }), snap, Template, n => n == "DASHED");
        Assert.Equal("DASHED", have.Create[0].Linetype);
        Assert.Empty(have.Warnings);

        var lost = StartLayers.Plan(Digest(new[] { "0", "L_A", "L_B", "L_C" }), snap, Template, n => n == "HIDDEN");
        Assert.All(lost.Create.Where(l => l.Name != "L_C"), l => Assert.Null(l.Linetype));
        Assert.Equal("HIDDEN", lost.Create.Single(l => l.Name == "L_C").Linetype);
        var w = Assert.Single(lost.Warnings);
        Assert.Contains("DASHED", w); Assert.Contains("L_A, L_B", w); Assert.Contains("Continuous", w);
    }

    [Fact]
    public void Xref_layers_and_unusable_names_are_not_created_and_both_are_reported()
    {
        var plan = StartLayers.Plan(Digest(new[] { "0", "XREF|WALLS", "A<B", "L_A" }), null, Template, NoLinetypes);
        Assert.Equal(new[] { "L_A" }, plan.Create.Select(l => l.Name));
        Assert.Contains(plan.Warnings, w => w.Contains("A<B"));
        Assert.Contains(plan.Warnings, w => w.Contains("XREF|WALLS") && w.Contains("external reference"));
    }

    [Fact]
    public void A_layer_with_start_entities_is_created_empty_with_a_warning()
    {
        var initial = Digest(new[] { "0", "L_A", "L_B" }, ("L_A", "k1"), ("L_A", "k2"));
        var plan = StartLayers.Plan(initial, null, Template, NoLinetypes);
        Assert.Equal(2, plan.Create.Single(l => l.Name == "L_A").StartEntities);
        Assert.Equal(0, plan.Create.Single(l => l.Name == "L_B").StartEntities);
        var w = Assert.Single(plan.Warnings);
        Assert.Contains("L_A", w); Assert.DoesNotContain("L_B", w); Assert.Contains("PURGE", w);
    }

    [Fact]
    public void The_start_current_layer_is_made_current_again_when_it_was_created_and_can_be()
    {
        BehaviorSnapshot WithClayer(string clayer, string layerValue)
        {
            var s = Snap(("L_A", layerValue));
            s.Set(BehaviorCatalog.SysVars, new[] { new KeyValuePair<string, string>("CLAYER", clayer) });
            return s;
        }
        var initial = Digest(new[] { "0", "L_A" });
        Assert.Equal("L_A", StartLayers.Plan(initial, WithClayer("L_A", Value()), Template, NoLinetypes).CurrentLayer);
        Assert.Null(StartLayers.Plan(initial, WithClayer("L_A", Value(frozen: "1")), Template, NoLinetypes).CurrentLayer);   // a frozen layer cannot be current
        Assert.Equal("0", StartLayers.Plan(initial, WithClayer("0", Value()), Template, NoLinetypes).CurrentLayer);         // pre-existing layer: the caller restores it when it differs
        Assert.Null(StartLayers.Plan(initial, Snap(("L_A", Value())), Template, NoLinetypes).CurrentLayer);                 // no sysvars captured, no digest layer
    }

    [Fact]
    public void Start_current_layer_comes_from_the_snapshot_else_the_digest()
    {
        var withDigest = DrawingDigest.Build(Array.Empty<(string, string, string)>(), new[] { "0", "L_A" }, null, "L_A");
        Assert.Equal("L_A", StartLayers.StartClayer(null, withDigest));
        var s = new BehaviorSnapshot();
        s.Set(BehaviorCatalog.SysVars, new[] { new KeyValuePair<string, string>("CLAYER", "0") });
        Assert.Equal("0", StartLayers.StartClayer(s, withDigest));
        Assert.Null(StartLayers.StartClayer(null, Digest(new[] { "0" })));
        Assert.Equal("L_A", StartLayers.Plan(withDigest, null, Template, NoLinetypes).CurrentLayer);
    }

    [Fact]
    public void Digest_layer_state_is_used_when_start_json_lacks_the_layer()
    {
        var state = new[] { new KeyValuePair<string, LayerProps>("L_A", new LayerProps { On = false, Frozen = true, Locked = true, Plot = false, Color = "rgb:1,2,3", Linetype = "Continuous", Lineweight = "0.25" }) };
        var initial = DrawingDigest.Build(Array.Empty<(string, string, string)>(), new[] { "0", "L_A", "L_B" }, state, null);
        var plan = StartLayers.Plan(initial, Snap(("L_B", Value(color: "3"))), Template, NoLinetypes);
        var a = plan.Create.Single(l => l.Name == "L_A");
        Assert.True(a.Described);
        Assert.False(a.On); Assert.True(a.Frozen); Assert.True(a.Locked); Assert.False(a.Plot);
        Assert.Equal("rgb:1,2,3", a.Color); Assert.Equal(25, a.Lineweight);
        Assert.Equal("3", plan.Create.Single(l => l.Name == "L_B").Color);   // start.json wins where it has the layer
        Assert.False(StartLayers.Plan(Digest(new[] { "0", "L_C" }), null, Template, NoLinetypes).Create.Single().Described);   // neither: name only
    }

    [Fact]
    public void Extra_layers_are_the_drawings_not_in_the_start_state_without_0_defpoints_and_xref_layers()
    {
        var initial = Digest(new[] { "0", "L_A" });
        Assert.Equal(new[] { "T_1", "T_2" }, StartLayers.ExtraLayers(initial, new[] { "0", "defpoints", "l_a", "T_1", "X|Y", "T_2" }));
        Assert.Empty(StartLayers.ExtraLayers(initial, new[] { "0", "L_A" }));
    }

    [Fact]
    public void Start_json_is_only_needed_when_a_start_layer_is_missing()
    {
        var initial = Digest(new[] { "0", "L_A" });
        Assert.False(StartLayers.AnyMissing(initial, new[] { "0", "l_a", "T_1" }));
        Assert.True(StartLayers.AnyMissing(initial, new[] { "0" }));
    }

    [Fact]
    public void Describe_shortens_long_lists()
    {
        Assert.Equal("", StartLayers.Describe(Array.Empty<string>()));
        Assert.Equal("created 1 layer the recording's start drawing had (L_A)", StartLayers.Describe(new[] { "L_A" }));
        var many = Enumerable.Range(1, 12).Select(i => "L" + i).ToList();
        var text = StartLayers.Describe(many);
        Assert.Contains("created 12 layers", text); Assert.Contains("L8", text); Assert.DoesNotContain("L9,", text); Assert.EndsWith(", ...)", text);
    }

    [Theory]
    [InlineData("0.25", 25)] [InlineData("0.3", 30)] [InlineData("Default", -3)] [InlineData("0", 0)]
    [InlineData("ByLayer", null)] [InlineData("ByBlock", null)] [InlineData("x", null)] [InlineData("", null)] [InlineData("9", null)] [InlineData("-1", null)]
    public void Lineweight_text_back_to_hundredths(string text, int? expected) => Assert.Equal(expected, StartLayers.LineweightHundredths(text));

    [Fact]
    public void Color_text_parses_the_three_forms()
    {
        Assert.Equal(new ColorSpec("aci", 5, 0, 0, 0, "", ""), StartLayers.ParseColor("5"));
        Assert.Equal(new ColorSpec("rgb", 0, 10, 20, 30, "", ""), StartLayers.ParseColor("rgb:10,20,30"));
        Assert.Equal(new ColorSpec("book", 0, 0, 0, 0, "BOOK", "Name 1"), StartLayers.ParseColor("BOOK$Name 1"));
        Assert.Null(StartLayers.ParseColor("0")); Assert.Null(StartLayers.ParseColor("256")); Assert.Null(StartLayers.ParseColor("rgb:1,2"));
        Assert.Null(StartLayers.ParseColor("rgb:300,0,0")); Assert.Null(StartLayers.ParseColor("")); Assert.Null(StartLayers.ParseColor(null));
    }

    // ---- the checkpoint that motivated it ----

    [Fact]
    public void Checkpoint_after_a_purge_fails_on_a_start_layer_the_replay_drawing_lacks_and_passes_once_it_is_created()
    {
        // recorded: L_KEEP existed at the start with an entity, so the PURGE kept it; L_EMPTY existed and was purged; L_NEW was added by the test
        var recordedEnd = Digest(new[] { "0", "L_KEEP", "L_NEW" }, ("L_KEEP", "k1"), ("L_NEW", "k2"));
        // replay in the bare template, as before the fix: the test adds L_NEW, the start entity on L_KEEP is not there
        var bare = Digest(new[] { "0", "L_NEW" }, ("L_NEW", "k2"));
        Assert.Contains("layer L_KEEP missing", DrawingDigest.Compare(recordedEnd, bare));
        // L_EMPTY: gone in both, no difference
        Assert.DoesNotContain(DrawingDigest.Compare(recordedEnd, bare), d => d.Contains("L_EMPTY"));
    }

    [Fact]
    public void Checkpoint_with_start_layers_created_matches_when_no_purge_ran_in_between()
    {
        var recorded = Digest(new[] { "0", "L_A", "L_B", "L_NEW" }, ("L_NEW", "k2"));
        var replay = Digest(new[] { "0", "L_A", "L_B", "L_NEW" }, ("L_NEW", "k2"));   // L_A and L_B created before step 1
        Assert.Empty(DrawingDigest.Compare(recorded, replay));
        var withoutFix = Digest(new[] { "0", "L_NEW" }, ("L_NEW", "k2"));
        var diffs = DrawingDigest.Compare(recorded, withoutFix);
        Assert.Contains("layer L_A missing", diffs); Assert.Contains("layer L_B missing", diffs);
    }

    [Fact]
    public void Start_state_difference_is_gone_once_the_layers_exist()
    {
        var initial = Digest(new[] { "0", "L_A", "L_B" });
        var template = Digest(new[] { "0" });
        Assert.Equal(new[] { "layer L_A missing", "layer L_B missing" }, DrawingDigest.Compare(initial, template));
        var plan = StartLayers.Plan(initial, null, new[] { "0" }, NoLinetypes);
        var after = Digest(new[] { "0" }.Concat(plan.Create.Select(l => l.Name)).ToArray());
        Assert.Empty(DrawingDigest.Compare(initial, after));
    }
}
