using System.Text.Json;
using Retrace.Core;
using Xunit;

namespace Retrace.Tests;

public class RecordZoomTests
{
    [Theory]
    [InlineData(null, "none")]
    [InlineData("", "none")]
    [InlineData("off", "none")]
    [InlineData("no", "none")]
    [InlineData("n", "none")]
    [InlineData("0", "none")]
    [InlineData("1", "extents")]
    [InlineData("Extents", "extents")]
    [InlineData("2", "lastEntity")]
    [InlineData("last", "lastEntity")]
    [InlineData("LASTENTITY", "lastEntity")]
    [InlineData("bogus", "none")]
    public void Normalize_is_as_lenient_as_the_post_test_zoom(string? text, string expected) =>
        Assert.Equal(expected, RecordZoomRules.Normalize(text));

    [Fact]
    public void Setting_defaults_to_none_and_round_trips()
    {
        Assert.Equal("none", new RetraceSettings().RecordZoom);
        var s = JsonSerializer.Deserialize<RetraceSettings>("{\"recordZoom\":\"last\"}")!;
        Assert.Equal("lastEntity", s.RecordZoom);
        Assert.Contains("\"recordZoom\":\"lastEntity\"", JsonSerializer.Serialize(s));
        Assert.Equal("none", JsonSerializer.Deserialize<RetraceSettings>("{\"recordZoom\":\"junk\"}")!.RecordZoom);
        Assert.Equal("none", JsonSerializer.Deserialize<RetraceSettings>("{}")!.RecordZoom);
    }

    [Fact]
    public void Created_means_a_live_handle_at_or_above_the_seed()
    {
        Assert.False(RecordZoomRules.Created(100, new long[] { 1, 99 }));
        Assert.True(RecordZoomRules.Created(100, new long[] { 1, 100 }));
        Assert.True(RecordZoomRules.Created(100, new long[] { 250 }));
        Assert.False(RecordZoomRules.Created(100, Array.Empty<long>()));   // a command that only erased
        Assert.False(RecordZoomRules.Created(0, new long[] { 5 }));        // unreadable seed
    }

    [Fact]
    public void Newest_is_the_highest_handle_made_since_the_seed()
    {
        Assert.Equal(300, RecordZoomRules.Newest(100, new long[] { 5, 300, 120, 200 }));
        Assert.Equal(-1, RecordZoomRules.Newest(100, new long[] { 5, 99 }));
        Assert.Equal(-1, RecordZoomRules.Newest(-1, new long[] { 5 }));
    }

    [Fact]
    public void Tracker_one_command_is_ready_after_its_end_with_its_seed()
    {
        var t = new ZoomTracker<string>();
        t.Start("a", () => 40, 0);
        Assert.Empty(t.Ready());
        t.End("a", 10);
        Assert.Equal(new[] { ("a", 40L) }, t.Ready());
        t.Remove("a");
        Assert.Equal(0, t.Count);
    }

    [Fact]
    public void Tracker_a_chain_keeps_the_first_seed_and_is_ready_once_all_ended()
    {
        var t = new ZoomTracker<string>();
        int reads = 0;
        t.Start("a", () => { reads++; return 40; }, 0);
        t.End("a", 5);
        t.Start("a", () => { reads++; return 90; }, 6);   // the next command of the chain, before the zoom was done
        Assert.Empty(t.Ready());
        t.End("a", 9);
        Assert.Equal(new[] { ("a", 40L) }, t.Ready());
        Assert.Equal(1, reads);
    }

    [Fact]
    public void Tracker_a_transparent_command_nests()
    {
        var t = new ZoomTracker<string>();
        t.Start("a", () => 10, 0);
        t.Start("a", () => 10, 1);   // 'ZOOM inside LINE
        t.End("a", 2);
        Assert.Empty(t.Ready());
        t.End("a", 3);
        Assert.Single(t.Ready());
    }

    [Fact]
    public void Tracker_documents_are_independent_and_an_unreadable_seed_tracks_nothing()
    {
        var t = new ZoomTracker<string>();
        t.Start("a", () => 10, 0); t.Start("b", () => 0, 0);
        t.End("a", 1); t.End("b", 1); t.End("c", 1);
        Assert.Equal(new[] { ("a", 10L) }, t.Ready());
    }

    [Fact]
    public void Tracker_fresh_ready_holds_the_picture_a_moment_and_stale_entries_expire()
    {
        var t = new ZoomTracker<string>();
        t.Start("a", () => 10, 0); t.End("a", 100);
        t.Start("b", () => 10, 0);   // never ends
        Assert.True(t.HasFreshReady(500, 1000));
        Assert.False(t.HasFreshReady(1200, 1000));
        Assert.Empty(t.Expire(1000, 15000, 600000));
        Assert.Equal(new[] { "a" }, t.Expire(16000, 15000, 600000));
        Assert.Equal(new[] { "b" }, t.Expire(700000, 15000, 600000));
        Assert.Equal(0, t.Count);
    }

    [Fact]
    public void Tracker_double_end_and_end_before_start_never_leave_it_stuck_or_negative()
    {
        var t = new ZoomTracker<string>();
        t.End("a", 0);                        // an end with no start
        Assert.Equal(0, t.Count);
        t.Start("a", () => 10, 1); t.End("a", 2); t.End("a", 3);   // a second end for one start
        Assert.Single(t.Ready());
        t.Start("a", () => 99, 4);            // the next command: depth 1 again, not 0 or -1
        Assert.Empty(t.Ready());
        t.End("a", 5);
        Assert.Equal(new[] { ("a", 10L) }, t.Ready());
    }

    [Fact]
    public void Tracker_a_missed_end_is_recovered_when_the_drawing_stays_quiet()
    {
        var t = new ZoomTracker<string>();
        t.Start("a", () => 10, 0);            // its end never comes
        Assert.False(t.NoteQuiet("a", true, 100, 2000));     // quiet just began
        Assert.False(t.NoteQuiet("a", false, 1000, 2000));   // busy again: the quiet time starts over
        Assert.False(t.NoteQuiet("a", true, 1500, 2000));
        Assert.False(t.NoteQuiet("a", true, 3000, 2000));
        Assert.True(t.NoteQuiet("a", true, 3600, 2000));     // quiet for 2 s: counted as ended
        Assert.Equal(new[] { ("a", 10L) }, t.Ready());
        Assert.False(t.NoteQuiet("a", true, 9000, 2000));    // nothing running: nothing to reset
    }

    [Fact]
    public void Tracker_running_entry_expires_from_its_first_start_not_from_the_last_event()
    {
        var t = new ZoomTracker<string>();
        t.Start("a", () => 10, 0);
        t.Start("a", () => 10, 599000); t.End("a", 599500); t.Start("a", () => 10, 600000);   // events keep coming
        Assert.Equal(new[] { "a" }, t.Expire(601000, 15000, 600000));
        Assert.Equal(0, t.Count);
    }

    [Fact]
    public void Tracker_has_and_keys_and_an_unreadable_seed_adds_no_entry()
    {
        var t = new ZoomTracker<string>();
        t.Start("a", () => 0, 0);
        Assert.False(t.Has("a"));
        t.Start("b", () => 5, 0);
        Assert.True(t.Has("b"));
        Assert.Equal(new[] { "b" }, t.Keys());
    }

    [Fact]
    public void Header_field_is_read_and_a_difference_is_noted()
    {
        static List<System.Text.Json.JsonElement> Ev(params string[] lines) => lines.Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement.Clone()).ToList();
        Assert.Equal("lastEntity", RecordZoomRules.FromHeader(Ev("{\"type\":\"header\",\"recordZoom\":\"last\"}")));
        Assert.Null(RecordZoomRules.FromHeader(Ev("{\"type\":\"header\",\"name\":\"x\"}")));   // an older recording
        Assert.Null(RecordZoomRules.FromHeader(Ev("{\"type\":\"state\"}")));
        Assert.Null(RecordZoomRules.DifferenceNote(null, "extents"));
        Assert.Null(RecordZoomRules.DifferenceNote("extents", "extents"));
        Assert.Contains("made with \"lastEntity\" and is replayed with \"none\"", RecordZoomRules.DifferenceNote("lastEntity", "none"));
    }
}
