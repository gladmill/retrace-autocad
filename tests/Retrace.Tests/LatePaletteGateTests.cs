using Retrace.Core;

namespace Retrace.Tests;

/// <summary>The late-palette watch's remaining rules: an auto-hide set that slides out under the mouse is not "opened", the person's inputs that end the watch, a palette at the limit, a stop during the watch, and the bookkeeping that writes
/// the end picture's `shot` event exactly once (first picture, retake, stop, interrupt, exceptions, failed writes).</summary>
public class LatePaletteGateTests
{
    static readonly PxRect Main = new(0, 0, 1920, 1080);
    static readonly PxRect Box = new(0, 100, 360, 500);
    static readonly PxRect EdgeBar = new(0, 100, 22, 700);

    // a retracted anchored set: its pages are not shown, the root window has no size (the state in the report), and an edge bar holds its tab
    static PaletteState[] Anchored() => [new(1, "Page", "Anchored Set", false, null, Diag: "page visible=True rect 3,3,361,399; root Afx visible=True rect -3815,182,-3815,182; failed: the root window is 60 px or less (a rolled-up strip, or hidden)"), new(9, "(auto-hide bar)", "", false, EdgeBar, IsBar: true)];
    static PaletteState[] SlidOut() => [new(1, "Page", "Anchored Set", true, Box), new(9, "(auto-hide bar)", "", false, EdgeBar, IsBar: true)];
    static PaletteState[] ZeroRootNoBar() => [new(1, "Page", "Late Set", false, null, Diag: "page visible=True rect 3,3,361,399; root Afx visible=True rect 0,0,0,0; failed: the root window is 60 px or less (a rolled-up strip, or hidden)")];
    static PaletteState[] Shown() => [new(1, "Page", "Late Set", true, Box)];

    static LatePaletteWatch Watch(IReadOnlyList<PaletteState> before, long endedAt = 1000)
    {
        var w = new LatePaletteWatch();
        w.Begin(7, before, endedAt);
        return w;
    }

    [Fact]
    public void An_anchored_set_that_slides_out_under_the_mouse_on_its_edge_tab_is_a_hover_not_a_retake()
    {
        var w = Watch(Anchored());
        var o = w.Poll(SlidOut(), Main, 1200, (5, 300))!;   // the cursor is on the edge bar
        Assert.Equal(LateKind.Hover, o.Kind);
        Assert.Null(o.Opened);
        Assert.Contains("no retake, no box", o.Line);
    }

    [Fact]
    public void A_cursor_that_passed_over_the_edge_bar_earlier_in_the_watch_is_remembered_as_a_hover()
    {
        var w = Watch(Anchored());
        w.NoteCursor(5, 300);              // mouse moved over the tab (a message hook sees every move) ...
        w.NoteCursor(900, 600);            // ... and away again before the palette is seen
        Assert.Equal(LateKind.Hover, w.Poll(SlidOut(), Main, 1300, (900, 600))!.Kind);
    }

    [Fact]
    public void The_cursor_inside_the_new_palette_while_its_set_was_anchored_or_zero_size_at_the_click_is_a_hover()
    {
        var w = Watch(ZeroRootNoBar());
        Assert.Equal(LateKind.Hover, w.Poll(Shown(), Main, 1200, (100, 300))!.Kind);
    }

    [Fact]
    public void A_set_shown_by_the_add_in_with_the_mouse_left_at_the_toolbar_is_still_boxed_even_when_it_was_anchored()
    {
        var w = Watch(Anchored());
        Assert.Null(w.Poll(SlidOut(), Main, 1200, (300, 14)));      // the user's case: the mouse is at the Quick Access Toolbar
        var o = w.Poll(SlidOut(), Main, 1350, (300, 14))!;
        Assert.Equal(LateKind.Retake, o.Kind);
        Assert.Equal("Page", o.Opened!.Name);
    }

    [Fact]
    public void A_zero_size_root_that_grows_late_is_boxed_when_the_mouse_is_elsewhere()
    {
        var w = Watch(ZeroRootNoBar());
        Assert.Null(w.Poll(ZeroRootNoBar(), Main, 1150, (300, 14)));
        Assert.Null(w.Poll(Shown(), Main, 1300, (300, 14)));
        Assert.Equal(LateKind.Retake, w.Poll(Shown(), Main, 1420, (300, 14))!.Kind);
    }

    [Fact]
    public void A_click_inside_a_palette_that_slid_out_by_hover_gets_no_box()
    {
        var w = Watch(Anchored());
        w.NoteCursor(5, 300);   // the mouse went over the tab, the palette slid out, the person clicks in it
        var o = w.Input(SlidOut(), Main, 1500, 100, 300);
        Assert.Equal(LateKind.Hover, o.Kind);
    }

    [Fact]
    public void A_palette_that_appears_exactly_at_the_limit_after_a_first_sighting_is_retaken()
    {
        var w = Watch(ZeroRootNoBar());
        Assert.Null(w.Poll(Shown(), Main, 1000 + LatePaletteWatch.LimitMs - 120, (300, 14)));   // first seen just before the limit
        var o = w.Poll(Shown(), Main, 1000 + LatePaletteWatch.LimitMs, (300, 14))!;             // seen again at the limit: the palette wins over the time out
        Assert.Equal(LateKind.Retake, o.Kind);
    }

    [Fact]
    public void A_stop_during_the_watch_ends_it_and_says_so()
    {
        var w = Watch(ZeroRootNoBar());
        var o = w.Interrupted("the recording stopped", 1700);
        Assert.Equal(LateKind.ActedFirst, o.Kind);
        Assert.Contains("the recording stopped", o.Line);
        Assert.False(w.Active);
    }

    [Theory]
    [InlineData(0x0201u, 1L, true)]    // left button down
    [InlineData(0x0204u, 1L, true)]    // right
    [InlineData(0x0207u, 1L, true)]    // middle
    [InlineData(0x020Bu, 1L, true)]    // X button
    [InlineData(0x020Au, 0L, true)]    // wheel
    [InlineData(0x020Eu, 0L, true)]    // horizontal wheel
    [InlineData(0x00A1u, 2L, true)]    // non-client left down (title-bar drag)
    [InlineData(0x00A4u, 2L, true)]
    [InlineData(0x00A7u, 2L, true)]
    [InlineData(0x0100u, 0x41L, true)] // a key
    [InlineData(0x0104u, 0x41L, true)] // a system key (Alt+letter)
    [InlineData(0x0100u, 0x10L, false)] // Shift alone
    [InlineData(0x0100u, 0x11L, false)] // Ctrl alone
    [InlineData(0x0104u, 0x12L, false)] // Alt alone
    [InlineData(0x0100u, 0xA2L, false)] // left Ctrl
    [InlineData(0x0202u, 1L, false)]   // a button-up: a fast command's own click-up
    [InlineData(0x0200u, 0L, false)]   // a mouse move
    [InlineData(0x0101u, 0x41L, false)] // a key-up
    public void Only_the_persons_next_action_counts(uint message, long wParam, bool counts) => Assert.Equal(counts, LateInputFilter.Counts(message, wParam));

    [Fact]
    public void Only_a_client_button_down_can_be_a_click_in_the_palette()
    {
        Assert.True(LateInputFilter.IsClient(0x0201));
        Assert.False(LateInputFilter.IsClient(0x00A1));
        Assert.False(LateInputFilter.IsClient(0x020A));
    }

    // ---- the one `shot` event of an end picture ----

    static ShotAnnounce? Seq(Func<EndShotGate, ShotAnnounce?>[] steps, out int events, out ShotAnnounce? last, EndShotGate? g = null)
    {
        g ??= new EndShotGate();
        events = 0; last = null;
        foreach (var s in steps)
            if (s(g) is { } a) { events++; last = a; }
        return last;
    }

    [Fact]
    public void A_retake_gives_one_event_with_opened_after_both_writes_and_the_release()
    {
        var g = new EndShotGate(); g.Hold(5);
        Assert.True(g.EarlyStarted(5));
        Assert.Null(g.EarlyFinished(5, true, "screenshots/005-cmd-end.png", "window"));   // not released yet
        Assert.True(g.RetakeStarted(5, "screenshots/005-cmd-end.png", "window"));
        Assert.Null(g.Release(5));                                                          // retake still being written
        var a = g.RetakeFinished(5, true)!.Value;
        Assert.True(a.Opened);
        Assert.Equal(5, a.N);
        Assert.False(g.Holds(5));
    }

    [Fact]
    public void No_retake_gives_one_event_without_opened_at_the_release()
    {
        var g = new EndShotGate(); g.Hold(5); g.EarlyStarted(5); g.EarlyFinished(5, true, "f.png", "window");
        var a = g.Release(5)!.Value;
        Assert.False(a.Opened);
        Assert.Null(g.Release(5));   // never twice
    }

    [Fact]
    public void A_release_before_the_first_write_has_finished_waits_for_it()
    {
        var g = new EndShotGate(); g.Hold(5); g.EarlyStarted(5);
        Assert.Null(g.Release(5));                       // stop or an interrupt while the picture is still being encoded
        var a = g.EarlyFinished(5, true, "f.png", "window")!.Value;
        Assert.False(a.Opened);
    }

    [Fact]
    public void A_retake_that_starts_while_the_first_write_is_running_is_written_after_it_and_the_event_waits_for_both()
    {
        var g = new EndShotGate(); g.Hold(5); g.EarlyStarted(5);
        Assert.True(g.RetakeStarted(5, "f.png", "window"));
        Assert.Null(g.Release(5));
        Assert.Null(g.EarlyFinished(5, true, "f.png", "window"));
        Assert.True(g.RetakeFinished(5, true)!.Value.Opened);
    }

    [Fact]
    public void A_failed_first_write_with_a_good_retake_still_gets_its_event_with_opened()
    {
        var g = new EndShotGate(); g.Hold(5); g.EarlyStarted(5); g.RetakeStarted(5, "f.png", "window");
        Assert.Null(g.EarlyFinished(5, false, "f.png", "window"));
        g.Release(5);
        var a = g.RetakeFinished(5, true)!.Value;
        Assert.True(a.Opened);
    }

    [Fact]
    public void A_failed_retake_write_keeps_the_first_picture_and_its_event_has_no_opened()
    {
        var g = new EndShotGate(); g.Hold(5); g.EarlyStarted(5); g.EarlyFinished(5, true, "f.png", "window"); g.RetakeStarted(5, "f.png", "window");
        g.Release(5);
        var a = g.RetakeFinished(5, false)!.Value;
        Assert.False(a.Opened);
    }

    [Fact]
    public void Both_writes_failing_gives_no_event()
    {
        var g = new EndShotGate(); g.Hold(5); g.EarlyStarted(5); g.RetakeStarted(5, "f.png", "window");
        g.EarlyFinished(5, false, "f.png", "window"); g.Release(5);
        Assert.Null(g.RetakeFinished(5, false));
        Assert.False(g.Holds(5));
    }

    [Fact]
    public void A_retake_after_the_release_or_a_second_retake_is_refused()
    {
        var g = new EndShotGate(); g.Hold(5); g.EarlyStarted(5); g.EarlyFinished(5, true, "f.png", "window");
        Assert.True(g.RetakeStarted(5, "f.png", "window"));
        Assert.False(g.RetakeStarted(5, "f.png", "window"));
        g.RetakeFinished(5, true); g.Release(5);
        Assert.False(g.RetakeStarted(5, "f.png", "window"));
        Assert.False(g.RetakeStarted(99, "x.png", "window"));   // not a held picture
    }

    [Fact]
    public void Stopping_releases_every_held_picture_once()
    {
        var g = new EndShotGate();
        g.Hold(5); g.EarlyStarted(5); g.EarlyFinished(5, true, "a.png", "window");
        g.Hold(9); g.EarlyStarted(9); g.EarlyFinished(9, true, "b.png", "window");
        var due = g.HeldNumbers().Select(n => g.Release(n)).Where(x => x != null).ToList();
        Assert.Equal(2, due.Count);
        Assert.Empty(g.HeldNumbers());
        Assert.Null(g.Release(5));
    }

    [Fact]
    public void A_picture_that_is_not_held_is_not_counted()
    {
        var g = new EndShotGate();
        Assert.False(g.EarlyStarted(3));
        Assert.Null(g.EarlyFinished(3, true, "f.png", "window"));
        Assert.Null(g.Release(3));
    }

    [Fact]
    public void Writing_a_png_twice_to_one_name_leaves_one_complete_file_and_no_temp_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rt-png-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "005-cmd-end.png");
            var px = new byte[4 * 4 * 4];
            Parallel.For(0, 8, _ => { for (int i = 0; i < 5; i++) Png.Write(path, 4, 4, px); });
            Assert.True(File.Exists(path));
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, File.ReadAllBytes(path).Take(4).ToArray());
            Assert.Equal(new[] { path }, Directory.GetFiles(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Heavy_contention_on_one_name_leaves_one_decodable_png_and_no_tmp_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rt-png-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "007-cmd-end.png");
            var px = new byte[16 * 8 * 4];
            for (int i = 0; i < px.Length; i++) px[i] = (byte)i;
            var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
            Parallel.For(0, 16, new ParallelOptions { MaxDegreeOfParallelism = 16 }, _ =>
            {
                for (int i = 0; i < 25; i++)
                {
                    try { Png.Write(path, 16, 8, px); } catch (Exception ex) { errors.Enqueue(ex); }
                }
            });
            Assert.Empty(errors);
            Assert.Equal(new[] { path }, Directory.GetFiles(dir));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
            // decodable: signature, chunks with good CRC, IHDR 16x8, the inflated IDAT has 8 rows of 1 + 16*3 bytes
            var b = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, b.Take(8).ToArray());
            int o = 8, w = 0, h = 0; bool end = false;
            var idat = new MemoryStream();
            while (o < b.Length)
            {
                int len = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o)); o += 4;
                var type = System.Text.Encoding.ASCII.GetString(b, o, 4);
                var body = b.AsSpan(o + 4, len);
                uint crc = 0xFFFFFFFF;
                foreach (var x in b.AsSpan(o, 4 + len)) { crc ^= x; for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1; }
                Assert.Equal(crc ^ 0xFFFFFFFF, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o + 4 + len)));
                if (type == "IHDR") { w = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(body); h = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(body[4..]); }
                if (type == "IDAT") idat.Write(body);
                if (type == "IEND") end = true;
                o += 4 + len + 4;
            }
            Assert.True(end);
            Assert.Equal((16, 8), (w, h));
            idat.Position = 2;   // after the 2-byte zlib header
            var raw = new MemoryStream();
            using (var z = new System.IO.Compression.DeflateStream(idat, System.IO.Compression.CompressionMode.Decompress)) z.CopyTo(raw);
            Assert.Equal(8 * (1 + 16 * 3), raw.Length);
        }
        finally { Directory.Delete(dir, true); }
    }
}
