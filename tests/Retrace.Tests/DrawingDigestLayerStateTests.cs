using System.Text.Json;
using Retrace.Core;

namespace Retrace.Tests;

/// <summary>Layer state in the drawing digest (schema 2) and its backwards compatibility. Synthetic data only.</summary>
public class DrawingDigestLayerStateTests
{
    static readonly (string, string, string)[] Ents = { ("LINE", "0", "k1"), ("LINE", "DIM", "k2") };

    static LayerProps Layer(bool on = true, bool frozen = false, bool locked = false, string color = "7", string linetype = "Continuous", string lineweight = "ByLayer", bool plot = true)
        => new() { On = on, Frozen = frozen, Locked = locked, Color = color, Linetype = linetype, Lineweight = lineweight, Plot = plot };

    static DrawingDigest Make(Dictionary<string, LayerProps>? state, string? current = "0")
        => DrawingDigest.Build(Ents, new[] { "0", "DIM" }, state, current);

    static Dictionary<string, LayerProps> Base() => new() { ["0"] = Layer(), ["DIM"] = Layer() };

    const string OldV2 = """{"count":2,"byType":{"LINE":2},"byLayer":{"0":1,"DIM":1},"layers":["0","DIM"],"hash":"HASH","v":2}""";
    const string OldV1 = """{"count":2,"byType":{"LINE":2},"byLayer":{"0":1,"DIM":1},"layers":["0","DIM"],"hash":"HASH"}""";

    static DrawingDigest Old(string json, DrawingDigest like) => DrawingDigest.FromJson(JsonDocument.Parse(json.Replace("HASH", like.Hash)).RootElement)!;

    [Fact]
    public void Old_digests_have_no_layer_state_and_compare_equal_to_new_ones()
    {
        var now = Make(Base());
        foreach (var json in new[] { OldV1, OldV2 })
        {
            var old = Old(json, now);
            Assert.Null(old.LayerState);
            Assert.Null(old.CurrentLayer);
            Assert.Empty(DrawingDigest.Compare(old, now));
        }
    }

    [Fact]
    public void Old_digest_as_actual_skips_layer_state_too()
    {
        var expected = Make(Base());
        Assert.Empty(DrawingDigest.Compare(expected, Old(OldV2, expected)));
    }

    [Fact]
    public void Entity_hash_does_not_depend_on_layer_state()
    {
        var changed = Base();
        changed["DIM"] = Layer(frozen: true);
        Assert.Equal(Make(Base()).Hash, Make(changed, "DIM").Hash);
        Assert.Equal(DrawingDigest.CurrentVersion, Make(Base()).Version);
    }

    [Fact]
    public void Layer_state_round_trips_through_json_and_old_json_has_no_null_fields()
    {
        var d = Make(new() { ["DIM"] = Layer(color: "rgb:10,20,30", lineweight: "0.25"), ["0"] = Layer() }, "DIM");
        var json = JsonSerializer.Serialize(d);
        Assert.Contains("\"layerState\":{\"0\":{", json);   // sorted
        Assert.Contains("\"currentLayer\":\"DIM\"", json);
        var back = DrawingDigest.FromJson(JsonDocument.Parse(json).RootElement)!;
        Assert.Equal("DIM", back.CurrentLayer);
        Assert.Equal("rgb:10,20,30", back.LayerState!["DIM"].Color);
        Assert.Equal("0.25", back.LayerState["DIM"].Lineweight);
        Assert.True(back.LayerState["0"].On);
        Assert.Empty(DrawingDigest.Compare(d, back));

        var plain = JsonSerializer.Serialize(DrawingDigest.Build(Ents, new[] { "0" }));
        Assert.DoesNotContain("layerState", plain);
        Assert.DoesNotContain("currentLayer", plain);
        Assert.DoesNotContain("null", plain);
        var partial = JsonSerializer.Serialize(new LayerProps { Frozen = true });
        Assert.Equal("""{"frozen":true}""", partial);
    }

    [Theory]
    [InlineData("frozen", "layer DIM: expected frozen, got thawed")]
    [InlineData("thawed", "layer DIM: expected thawed, got frozen")]
    [InlineData("locked", "layer DIM: expected locked, got unlocked")]
    [InlineData("off", "layer DIM: expected off, got on")]
    [InlineData("noplot", "layer DIM: expected not plottable, got plottable")]
    [InlineData("color", "layer DIM color: expected 1, got 7")]
    [InlineData("linetype", "layer DIM linetype: expected DASHED, got Continuous")]
    [InlineData("lineweight", "layer DIM lineweight: expected 0.25, got ByLayer")]
    public void Each_layer_state_difference_is_reported(string what, string line)
    {
        var exp = Base();
        var act = Base();
        switch (what)
        {
            case "frozen": exp["DIM"] = Layer(frozen: true); break;
            case "thawed": act["DIM"] = Layer(frozen: true); break;
            case "locked": exp["DIM"] = Layer(locked: true); break;
            case "off": exp["DIM"] = Layer(on: false); break;
            case "noplot": exp["DIM"] = Layer(plot: false); break;
            case "color": exp["DIM"] = Layer(color: "1"); break;
            case "linetype": exp["DIM"] = Layer(linetype: "DASHED"); break;
            case "lineweight": exp["DIM"] = Layer(lineweight: "0.25"); break;
        }
        Assert.Equal(new[] { line }, DrawingDigest.Compare(Make(exp), Make(act)));
    }

    [Fact]
    public void Current_layer_is_never_compared()
    {
        Assert.Empty(DrawingDigest.Compare(Make(Base(), "DIM"), Make(Base(), "0")));
        Assert.Empty(DrawingDigest.Compare(Make(Base(), null), Make(Base(), "0")));
        Assert.Empty(DrawingDigest.Compare(Make(Base(), "DIM"), Make(Base(), null)));
    }

    [Fact]
    public void Layer_state_is_not_compared_when_the_caller_says_the_mark_has_behavior_checks()
    {
        var exp = Base();
        exp["DIM"] = Layer(frozen: true);
        Assert.Single(DrawingDigest.Compare(Make(exp), Make(Base())));                                   // default: compared (schema 1/2 marks)
        Assert.Empty(DrawingDigest.Compare(Make(exp), Make(Base()), compareLayerState: false));          // mark with checks: the layers behavior owns it
    }

    [Fact]
    public void A_property_missing_on_one_side_is_not_compared()
    {
        var exp = Base();
        exp["DIM"] = new LayerProps { Frozen = true };                 // only frozen was readable
        var act = Base();
        act["DIM"] = new LayerProps { Locked = true, Color = "1" };    // frozen unreadable here
        Assert.Empty(DrawingDigest.Compare(Make(exp), Make(act)));

        act["DIM"] = new LayerProps { Frozen = false };
        Assert.Equal(new[] { "layer DIM: expected frozen, got thawed" }, DrawingDigest.Compare(Make(exp), Make(act)));
    }

    [Fact]
    public void Missing_layers_are_not_reported_twice_and_state_without_layers_on_one_side_is_skipped()
    {
        var exp = Base();
        var actState = new Dictionary<string, LayerProps> { ["0"] = Layer() };
        var act = DrawingDigest.Build(new[] { ("LINE", "0", "k1"), ("LINE", "0", "k2") }, new[] { "0" }, actState, "0");
        var diffs = DrawingDigest.Compare(Make(exp), act);
        Assert.Contains("layer DIM missing", diffs);
        Assert.DoesNotContain(diffs, d => d.StartsWith("layer DIM:") && d.Contains("frozen"));
        Assert.DoesNotContain(diffs, d => d.Contains("color") || d.Contains("linetype"));

        Assert.Empty(DrawingDigest.Compare(Make(exp), Make(null)));   // actual has no layer state at all
    }

    [Fact]
    public void Layer_state_differences_do_not_hide_a_geometry_hash_difference()
    {
        var exp = Make(Base());
        var act = DrawingDigest.Build(new[] { ("LINE", "0", "k1"), ("LINE", "DIM", "OTHER") }, new[] { "0", "DIM" },
            new Dictionary<string, LayerProps> { ["0"] = Layer(), ["DIM"] = Layer(locked: true) }, "0");
        Assert.Equal(new[] { "geometry hash differs", "layer DIM: expected unlocked, got locked" }, DrawingDigest.Compare(exp, act));
    }

    [Fact]
    public void Lineweight_text()
    {
        Assert.Equal("0.25", DrawingDigest.LineweightText(25));
        Assert.Equal("0.3", DrawingDigest.LineweightText(30));
        Assert.Equal("0", DrawingDigest.LineweightText(0));
        Assert.Equal("2.11", DrawingDigest.LineweightText(211));
        Assert.Equal("ByLayer", DrawingDigest.LineweightText(-1));
        Assert.Equal("ByBlock", DrawingDigest.LineweightText(-2));
        Assert.Equal("Default", DrawingDigest.LineweightText(-3));
    }

    [Fact]
    public void Malformed_layer_state_never_invalidates_the_rest_of_the_digest()
    {
        var good = Make(Base());
        foreach (var bad in new[] {
            ""","layerState":[],"currentLayer":5""",
            ""","layerState":{"0":7,"DIM":{"color":7,"on":"yes","frozen":false}},"currentLayer":{}""",
            ""","layerState":"x","currentLayer":null""" })
        {
            var json = OldV2.Replace("HASH", good.Hash).TrimEnd('}') + bad + "}";
            var d = DrawingDigest.FromJson(JsonDocument.Parse(json).RootElement);
            Assert.NotNull(d);
            Assert.Equal(good.Hash, d!.Hash);
            Assert.Equal(2, d.Count);
            Assert.Null(d.CurrentLayer);
            Assert.Empty(DrawingDigest.Compare(d, good));
        }
        var partial = DrawingDigest.FromJson(JsonDocument.Parse(OldV2.Replace("HASH", good.Hash).TrimEnd('}') + ""","layerState":{"0":7,"DIM":{"color":7,"on":"yes","frozen":false}}}""").RootElement)!;
        Assert.Equal(new[] { "DIM" }, partial.LayerState!.Keys);
        Assert.Null(partial.LayerState["DIM"].Color);
        Assert.Null(partial.LayerState["DIM"].On);
        Assert.False(partial.LayerState["DIM"].Frozen);
    }

    [Fact]
    public void Layer_state_keys_stay_ordinal_after_from_json()
    {
        var json = """{"count":0,"layerState":{"b":{},"B":{},"a":{},"_":{}}}""";
        var d = DrawingDigest.FromJson(JsonDocument.Parse(json).RootElement)!;
        Assert.Equal(new[] { "B", "_", "a", "b" }, d.LayerState!.Keys);
        Assert.Equal(new[] { "B", "_", "a", "b" }, d.LayerState.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void Writer_schema_is_3()
    {
        Assert.Equal(3, SessionWriter.SchemaVersion);
    }
}
