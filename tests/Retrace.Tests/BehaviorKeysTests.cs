using Retrace.Core;

namespace Retrace.Tests;

/// <summary>The AutoCAD-free parts of behavior capture: ids, viewport order, xdata groups, NOD filter, formatters.</summary>
public class BehaviorKeysTests
{
    [Fact]
    public void Entity_ids_number_repeats_in_creation_order()
    {
        var ids = BehaviorKeys.EntityIds("MS:", new[] { "LINE|0|x", "CIRCLE|0|y", "LINE|0|x", "LINE|0|x" });
        Assert.Equal(new[] { "MS:LINE|0|x", "MS:CIRCLE|0|y", "MS:LINE|0|x#2", "MS:LINE|0|x#3" }, ids);
        // a single edit of one entity (its key changes) does not renumber the others
        var edited = BehaviorKeys.EntityIds("MS:", new[] { "LINE|0|x", "CIRCLE|0|y", "LINE|0|z", "LINE|0|x" });
        Assert.Equal(new[] { "MS:LINE|0|x", "MS:CIRCLE|0|y", "MS:LINE|0|z", "MS:LINE|0|x#2" }, edited);
        Assert.Empty(BehaviorKeys.EntityIds("MS:", Array.Empty<string>()));
        Assert.Equal(new[] { "PS:A:k", "PS:A:k#2" }, BehaviorKeys.EntityIds(BehaviorKeys.PaperPrefix("A"), new[] { "k", "k" }));
    }

    [Fact]
    public void Dictionary_paths_escape_the_separator()
    {
        Assert.Equal("NOD|A/B", BehaviorKeys.XDictKey("NOD", new[] { "A", "B" }));
        Assert.Equal(@"NOD|A\/B", BehaviorKeys.XDictKey("NOD", new[] { "A/B" }));
        Assert.NotEqual(BehaviorKeys.XDictKey("NOD", new[] { "A", "B" }), BehaviorKeys.XDictKey("NOD", new[] { "A/B" }));
        Assert.Equal(@"NOD|A\\/B", BehaviorKeys.XDictKey("NOD", new[] { @"A\", "B" }));   // name `A\` then `B`
        Assert.NotEqual(BehaviorKeys.XDictKey("NOD", new[] { @"A\", "B" }), BehaviorKeys.XDictKey("NOD", new[] { @"A\/B" }));
    }

    [Fact]
    public void Viewports_order_by_center_y_descending_then_x_ascending_after_rounding()
    {
        var order = BehaviorKeys.ViewportOrder(new (double, double, string)[]
        {
            (10, 5, "low-right"), (1, 20.000000001, "top-right"), (0, 20, "top-left"), (5, 5, "low-left"),
        });
        // top-right and top-left have the same rounded Y: X decides
        Assert.Equal(new[] { "top-left", "top-right", "low-left", "low-right" }, order);
        Assert.Equal("Sheet1|2", BehaviorKeys.ViewportKey("Sheet1", 2));
    }

    [Fact]
    public void Xdata_groups_split_by_application_and_become_a_canonical_value()
    {
        var split = BehaviorKeys.SplitXData(new[] { (1000, "orphan"), (1001, "ZAPP"), (1000, "a;b"), (1040, "1.5"), (1001, "ACAD"), (1070, "3"), (1001, "ZAPP"), (1071, "7") });
        Assert.Equal(new[] { "ACAD", "ZAPP" }, split.Keys);
        Assert.Equal(new[] { "1070:3" }, split["ACAD"]);
        Assert.Equal(new[] { "1000:a;b", "1040:1.5", "1071:7" }, split["ZAPP"]);
        var v = BehaviorKeys.GroupsValue(split["ZAPP"]);
        Assert.Equal(@"0=1000:a\;b;1=1040:1.5;2=1071:7", v);
        Assert.True(CanonicalValue.TryParse(v, out var fields));
        Assert.Equal(3, fields!.Count);
        Assert.Equal("", BehaviorKeys.GroupsValue(Array.Empty<string>()));
    }

    [Fact]
    public void Entry_value_has_type_then_groups()
    {
        Assert.Equal("type=XRECORD;0=1:x;1=70:2", BehaviorKeys.EntryValue("XRECORD", new[] { BehaviorKeys.Group(1, "x"), BehaviorKeys.Group(70, "2") }));
        Assert.Equal("type=DICTIONARY", BehaviorKeys.EntryValue("DICTIONARY", Array.Empty<string>()));
    }

    [Theory]
    [InlineData("ACAD_GROUP", true)]
    [InlineData("acad_layout", true)]
    [InlineData("AcDbVariableDictionary", true)]
    [InlineData("AEC_CLASSIFICATION", true)]
    [InlineData("MYAPP_DATA", false)]
    [InlineData("ACADX", false)]
    public void Nod_entries_of_autocad_are_skipped(string name, bool builtIn) => Assert.Equal(builtIn, BehaviorKeys.IsBuiltInNodEntry(name));

    [Fact]
    public void Small_formatters()
    {
        Assert.Equal("CIRCLE:1,LINE:2", BehaviorKeys.TypeCounts(new[] { "LINE", "CIRCLE", "LINE" }));
        Assert.Equal("", BehaviorKeys.TypeCounts(Array.Empty<string>()));
        Assert.Equal("1", BehaviorKeys.Bool(true));
        Assert.Equal("0", BehaviorKeys.Bool(false));
        Assert.Equal("1.5,0", BehaviorKeys.Pt2(1.50001, -0.00001));
        Assert.Equal("A,B", BehaviorKeys.SortedList(new[] { "B", "A" }));
        Assert.Equal("L|I|TAG", BehaviorKeys.AttributeKey("L", "I", "TAG"));
        Assert.Equal("L|I|TAG#2", BehaviorKeys.AttributeKey("L", "I", "TAG", 2));
        Assert.Equal("MS:k|ACAD", BehaviorKeys.XDataKey("MS:k", "ACAD"));
        Assert.Equal("NOD|A/B/C", BehaviorKeys.XDictKey("NOD", new[] { "A", "B", "C" }));
    }
}
