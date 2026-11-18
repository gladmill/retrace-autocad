using Retrace.Core;

namespace Retrace.Tests;

public class RecordStartTabTests
{
    const string Own = "RETRACE_TAB";

    static TabFact T(string id, string title, bool showing = true, bool contextual = false) => new(id, title, showing, contextual);

    static readonly TabFact[] Normal =
    {
        T("ID_TabHome", "Home"), T(Own, "Retrace"), T("ID_TabInsert", "Insert"), T("test", "test"),
    };

    static RecordStartTab.Choice Go(string? setting, TabFact[] tabs, string? last = null, bool own = true) => RecordStartTab.Choose(setting, tabs, Own, last, own);

    [Theory]
    [InlineData(null, "home")]
    [InlineData("", "home")]
    [InlineData("junk", "home")]
    [InlineData(" LAST ", "last")]
    [InlineData("Stay", "stay")]
    [InlineData("home", "home")]
    public void Normalize(string? v, string expected) => Assert.Equal(expected, RecordStartTab.Normalize(v));

    [Fact] public void Setting_default_is_home() => Assert.Equal("home", new RetraceSettings().RecordStartTab);

    [Fact] public void Stay_switches_nothing() => Assert.Null(Go("stay", Normal, "test").TabId);

    [Fact]
    public void Not_started_from_the_Retrace_tab_switches_nothing()
    {
        Assert.Null(Go("home", Normal, "test", own: false).TabId);
        Assert.Null(Go("last", Normal, "test", own: false).TabId);
    }

    [Fact] public void Home_by_known_id() => Assert.Equal("ID_TabHome", Go("home", Normal).TabId);

    [Fact]
    public void Home_by_known_id_of_another_product()
    {
        var c = Go("home", new[] { T(Own, "Retrace"), T("ID_TabInsert", "Insert"), T("ID_TabHome3D", "Home - 3D") });
        Assert.Equal("ID_TabHome3D", c.TabId);
        Assert.Contains("known Home id", c.Rule);
    }

    [Fact]
    public void Home_by_known_id_with_a_product_prefix()
    {
        var c = Go("home", new[] { T(Own, "Retrace"), T("ACAD.ID_TabInsert", "Insert"), T("ACAD.ID_TabHome", "Start") });
        Assert.Equal("ACAD.ID_TabHome", c.TabId);
        Assert.Contains("known Home id", c.Rule);
    }

    [Fact]
    public void Home_by_title_when_the_id_is_unknown()
    {
        var c = Go("home", new[] { T(Own, "Retrace"), T("X1", "Draw"), T("X2", " home ") });
        Assert.Equal("X2", c.TabId);
        Assert.Contains("titled Home", c.Rule);
    }

    [Fact]
    public void Home_falls_back_to_the_first_visible_non_contextual_tab_that_is_not_Retrace()
    {
        var c = Go("home", new[] { T(Own, "Retrace"), T("C1", "Ctx", contextual: true), T("H1", "Hidden", showing: false), T("A", "Alpha"), T("B", "Beta") });
        Assert.Equal("A", c.TabId);
        Assert.Contains("first visible", c.Rule);
    }

    [Fact]
    public void Home_skips_a_hidden_Home_tab()
    {
        var c = Go("home", new[] { T("ID_TabHome", "Home", showing: false), T(Own, "Retrace"), T("A", "Alpha") });
        Assert.Equal("A", c.TabId);
    }

    [Fact]
    public void Home_never_picks_a_contextual_tab_by_title()
    {
        var c = Go("home", new[] { T(Own, "Retrace"), T("K", "Home", contextual: true) });
        Assert.Null(c.TabId);
    }

    [Fact] public void No_usable_tab_switches_nothing() => Assert.Null(Go("home", new[] { T(Own, "Retrace") }).TabId);

    [Fact]
    public void Last_returns_the_remembered_tab()
    {
        var c = Go("last", Normal, "test");
        Assert.Equal("test", c.TabId);
        Assert.StartsWith("last", c.Rule);
    }

    [Fact] public void Last_matches_the_id_without_case() => Assert.Equal("test", Go("last", Normal, "TEST").TabId);

    [Fact]
    public void Last_that_is_gone_falls_back_to_home()
    {
        var c = Go("last", Normal, "ID_TabGone");
        Assert.Equal("ID_TabHome", c.TabId);
        Assert.Contains("gone, hidden", c.Rule);
    }

    [Fact]
    public void Last_that_is_hidden_falls_back_to_home() =>
        Assert.Equal("ID_TabHome", Go("last", new[] { T("ID_TabHome", "Home"), T(Own, "Retrace"), T("test", "test", showing: false) }, "test").TabId);

    [Fact]
    public void Last_that_is_contextual_and_not_showing_falls_back_to_home() =>
        Assert.Equal("ID_TabHome", Go("last", new[] { T("ID_TabHome", "Home"), T(Own, "Retrace"), T("ctx", "Table", showing: false, contextual: true) }, "ctx").TabId);

    [Fact]
    public void Last_that_is_contextual_and_showing_is_used() =>
        Assert.Equal("ctx", Go("last", new[] { T("ID_TabHome", "Home"), T(Own, "Retrace"), T("ctx", "Table", showing: true, contextual: true) }, "ctx").TabId);

    [Fact] public void Last_unknown_falls_back_to_home() => Assert.Equal("ID_TabHome", Go("last", Normal, null).TabId);

    [Fact] public void Last_is_never_the_Retrace_tab() => Assert.Equal("ID_TabHome", Go("last", Normal, Own).TabId);

    [Fact]
    public void Tracker_remembers_the_last_tab_that_is_not_Retrace()
    {
        var t = new LastTabTracker();
        Assert.Null(t.Id);
        t.Note("ID_TabInsert", Own);
        t.Note(Own, Own);
        Assert.Equal("ID_TabInsert", t.Id);
        t.Note("test", Own);
        t.Note(Own, Own);
        Assert.Equal("test", t.Id);
    }

    [Fact]
    public void Settings_round_trip_through_json()
    {
        var s = System.Text.Json.JsonSerializer.Deserialize<RetraceSettings>("{\"recordStartTab\":\"STAY\"}")!;
        Assert.Equal("stay", s.RecordStartTab);
        Assert.Equal("stay", SettingsHeader.Snapshot(s)["recordStartTab"]);
    }

    [Fact]
    public void Tracker_forgets_when_the_active_tab_has_no_id_so_last_falls_back_to_home()
    {
        var t = new LastTabTracker();
        t.Note("test", Own);
        t.Note("", Own);   // an add-in tab without an id
        Assert.Null(t.Id);
        t.Note("test", Own); t.Note(null, Own);
        Assert.Null(t.Id);
        Assert.Equal("ID_TabHome", Go("last", Normal, t.Id).TabId);
    }

    [Fact]
    public void Several_known_Home_ids_at_once_take_the_first_in_the_list()
    {
        var c = Go("home", new[] { T(Own, "Retrace"), T("ID_TabHome3D", "Home - 3D"), T("ID_TabHome", "Home 2") });
        Assert.Equal("ID_TabHome", c.TabId);   // KnownHomeIds order: ID_TabHome before ID_TabHome3D, whatever the tab order
    }

    [Fact]
    public void Switch_only_while_the_Retrace_tab_is_still_active()
    {
        Assert.True(RecordStartTab.StillOnOwnTab(Own, Own));
        Assert.True(RecordStartTab.StillOnOwnTab("retrace_tab", Own));
        Assert.False(RecordStartTab.StillOnOwnTab("ID_TabInsert", Own));   // the person clicked another tab: their choice
        Assert.False(RecordStartTab.StillOnOwnTab(null, Own));
        Assert.False(RecordStartTab.StillOnOwnTab("", Own));
    }

    [Fact]
    public void Recorder_sync_id_is_the_real_active_tab()
    {
        Assert.Equal("ID_TabInsert", RecordStartTab.RecorderSyncId("ID_TabInsert"));
        Assert.Null(RecordStartTab.RecorderSyncId(null));
        Assert.Null(RecordStartTab.RecorderSyncId(""));
    }

    [Fact]
    public void IsKnown_accepts_the_three_values_only()
    {
        Assert.True(RecordStartTab.IsKnown(" Last ")); Assert.True(RecordStartTab.IsKnown("STAY")); Assert.True(RecordStartTab.IsKnown("home"));
        Assert.False(RecordStartTab.IsKnown("")); Assert.False(RecordStartTab.IsKnown("junk")); Assert.False(RecordStartTab.IsKnown(null));
    }
}
