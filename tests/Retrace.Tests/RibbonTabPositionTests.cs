using Retrace.Core;

namespace Retrace.Tests;

public class RibbonTabPositionTests
{
    const string Own = "RETRACE_TAB";

    static readonly (string Id, string Title)[] Tabs =
    {
        ("ID_TabHome", "Home"), ("ID_TabInsert", "Insert"), ("ID_TabAnnotate", "Annotate"), ("ID_TabView", "View"),
    };

    static int Idx(string? setting, params (string Id, string Title)[] tabs) => RibbonTabPosition.ChooseIndex(setting, tabs, Own);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("afterHome")]
    [InlineData("garbage")]
    [InlineData("after:")]
    public void Default_is_right_after_Home(string? setting) => Assert.Equal(1, Idx(setting, Tabs));

    [Fact] public void First() => Assert.Equal(0, Idx("first", Tabs));
    [Fact] public void Last() => Assert.Equal(4, Idx("LAST", Tabs));

    [Fact] public void After_tab_by_id() => Assert.Equal(3, Idx("after:ID_TabAnnotate", Tabs));
    [Fact] public void After_tab_by_id_is_case_insensitive() => Assert.Equal(3, Idx("after:id_tabannotate", Tabs));
    [Fact] public void After_tab_by_title_is_case_insensitive() => Assert.Equal(4, Idx("After:  view ", Tabs));

    [Fact] public void Missing_anchor_falls_back_to_after_Home() => Assert.Equal(1, Idx("after:Nope", Tabs));

    [Fact]
    public void Missing_anchor_and_no_Home_falls_back_to_last() =>
        Assert.Equal(2, Idx("after:Nope", ("ID_TabInsert", "Insert"), ("ID_TabView", "View")));

    [Fact]
    public void Home_found_by_title_when_id_differs() =>
        Assert.Equal(1, Idx(null, ("X", "Home"), ("Y", "Insert")));

    [Fact]
    public void Home_not_first_still_goes_right_after_it() =>
        Assert.Equal(3, Idx(null, ("A", "Start"), ("B", "Other"), ("ID_TabHome", "Home"), ("C", "Insert")));

    [Fact]
    public void Existing_Retrace_tab_is_not_counted()
    {
        var withOwn = new[] { Tabs[0], (Own, "Retrace"), Tabs[1], Tabs[2], Tabs[3] };
        Assert.Equal(1, Idx(null, withOwn));
        Assert.Equal(0, Idx("first", withOwn));
        Assert.Equal(4, Idx("last", withOwn));
        Assert.Equal(3, Idx("after:Annotate", withOwn));
        var beforeHome = new[] { (Own, "Retrace"), Tabs[0], Tabs[1] };
        Assert.Equal(1, Idx(null, beforeHome));
    }

    [Fact]
    public void Empty_list_gives_zero()
    {
        foreach (var s in new[] { null, "first", "last", "after:Home" }) Assert.Equal(0, Idx(s));
    }

    [Fact]
    public void Anchor_on_Retrace_itself_is_ignored() => Assert.Equal(1, Idx("after:Retrace", new[] { Tabs[0], (Own, "Retrace"), Tabs[1] }));

    [Theory]
    [InlineData("{\"ribbonTabPosition\":\"last\"}", "last")]
    [InlineData("{\"sourceFolder\":\"x\"}", "")]
    [InlineData("{ // c\n \"ribbonTabPosition\": \"after:View\", }", "after:View")]
    [InlineData("{\"ribbonTabPosition\": 5}", "")]
    [InlineData("not json", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ReadSetting_is_tolerant(string? json, string expected) => Assert.Equal(expected, RibbonTabPosition.ReadSetting(json));

    [Fact]
    public void Normalize_keeps_known_values()
    {
        Assert.Equal("first", RibbonTabPosition.Normalize(" First "));
        Assert.Equal("after:View", RibbonTabPosition.Normalize("AFTER: View"));
        Assert.Equal("afterHome", RibbonTabPosition.Normalize("after:"));
    }
}
