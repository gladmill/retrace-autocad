using Retrace.Core;
using Retrace.Engine.Recordings;

namespace Retrace.Tests;

/// <summary>The test document's pictures of clicks inside a dialog (`dialog_pick`, the `shot` of a `dialog_click`): one bullet and picture per click in click order, old recordings as before,
/// and "#id" controls read "the tree" / "the list". Synthetic data only.</summary>
public class DialogClickPictureTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("retrace-test-dpic").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    const string Header = """{"type":"header","schema":1,"name":"t","started":"2026-11-06T10:00:00"}""";
    const string Tree = """{"key":"id:1012","label":"#1012","kind":"tree","before":"","after":"Root › Child"}""";
    const string List = """{"key":"id:1007","label":"#1007","kind":"listview","before":"","after":"Base 2"}""";

    string Script(params string[] lines)
    {
        var dir = Path.Combine(_dir, "s");
        Directory.CreateDirectory(Path.Combine(dir, "screenshots"));
        foreach (var f in new[] { "002-dialog.png", "003-dialog-click.png", "004-dialog-click.png", "005-dialog-click.png" }) File.WriteAllBytes(Path.Combine(dir, "screenshots", f), new byte[] { 1 });
        var p = Path.Combine(dir, "recording.jsonl");
        File.WriteAllLines(p, new[] { Header }.Concat(lines));
        return ScriptWriter.Build(RecordingReader.Load(p), null, dir, new List<string>());
    }

    static string[] Open() => new[]
    {
        """{"t":10,"n":1,"type":"modal_enter","title":"Quick add"}""",
        """{"t":11,"n":2,"type":"modal_title","title":"Quick add","windowClass":"WindowsForms10.Window.8.app"}""",
        """{"t":12,"n":3,"type":"shot","for":2,"file":"screenshots/002-dialog.png","what":"dialog"}""",
    };

    static string[] Close(string last) => new[]
    {
        last,
        """{"t":80,"n":9,"type":"dialog_values","for":2,"title":"Quick add","fields":[],"read":3,"closedWith":"click","button":"Place"}""",
        """{"t":81,"n":10,"type":"modal_leave"}""",
    };

    [Fact]
    public void One_bullet_and_picture_per_click_in_click_order()
    {
        var md = Script(Open().Concat(new[]
        {
            """{"t":30,"n":4,"type":"dialog_pick","for":2,"kind":"tree","label":"#1012","item":"Root › Child"}""",
            """{"t":31,"n":5,"type":"shot","for":4,"file":"screenshots/003-dialog-click.png","what":"dialog-click"}""",
            """{"t":40,"n":6,"type":"dialog_pick","for":2,"kind":"listview","label":"#1007","item":"Base 2"}""",
            """{"t":41,"n":7,"type":"shot","for":6,"file":"screenshots/004-dialog-click.png","what":"dialog-click"}""",
        }).Concat(Close($$"""{"t":70,"n":8,"type":"dialog_click","for":2,"button":"Place","id":1024,"index":1,"fields":[{{Tree}},{{List}}],"read":3}""")).Concat(new string[0]).ToArray());
        // the shot of the button click is added below in its own test; here the button has none
        var lines = md.Split('\n');
        int tree = Array.FindIndex(lines, l => l.Contains("Select `Root › Child` in the tree"));
        int list = Array.FindIndex(lines, l => l.Contains("Select `Base 2` in the list"));
        int button = Array.FindIndex(lines, l => l.Contains("Click **Place**"));
        Assert.True(tree > 0 && list > tree && button > list, md);
        Assert.Contains("003-dialog-click.png", string.Join("\n", lines.Skip(tree + 1).Take(3)));
        Assert.Contains("004-dialog-click.png", string.Join("\n", lines.Skip(list + 1).Take(3)));
        Assert.DoesNotContain("#1012", md);
        Assert.DoesNotContain("#1007", md);
        Assert.False(lines[button].Contains("Select"), "the picked values are not written again with the button: " + lines[button]);
    }

    [Fact]
    public void The_button_click_has_its_own_picture()
    {
        var md = Script(Open().Concat(new[]
        {
            """{"t":30,"n":4,"type":"dialog_pick","for":2,"kind":"tree","label":"#1012","item":"Root › Child"}""",
            """{"t":31,"n":5,"type":"shot","for":4,"file":"screenshots/003-dialog-click.png","what":"dialog-click"}""",
            """{"t":70,"n":6,"type":"dialog_click","for":2,"button":"Place","id":1024,"index":1,"fields":[""" + Tree + """],"read":3}""",
            """{"t":71,"n":7,"type":"shot","for":6,"file":"screenshots/005-dialog-click.png","what":"dialog-click"}""",
            """{"t":80,"n":9,"type":"dialog_values","for":2,"title":"Quick add","fields":[],"read":3,"closedWith":"click","button":"Place"}""",
            """{"t":81,"n":10,"type":"modal_leave"}""",
        }).ToArray());
        var lines = md.Split('\n');
        int button = Array.FindIndex(lines, l => l.Contains("Click **Place**"));
        Assert.True(button > 0, md);
        Assert.Contains("005-dialog-click", lines[button + 2]);
    }

    [Fact]
    public void An_old_recording_without_pictures_of_clicks_is_written_as_before()
    {
        var md = Script(Open().Concat(Close($$"""{"t":70,"n":8,"type":"dialog_click","for":2,"button":"Place","id":1024,"index":1,"fields":[{{Tree}},{{List}}],"read":3}""")).ToArray());
        // one combined bullet, the values named by their control ("in the tree": a control with no label of its own is not "#1012")
        Assert.Contains("Select `Root › Child` in the tree, select `Base 2` in the list, click **Place**", md);
    }

    [Fact]
    public void A_pick_with_no_picture_does_not_change_the_wording()
    {
        var md = Script(Open().Concat(new[]
        {
            """{"t":30,"n":4,"type":"dialog_pick","for":2,"kind":"tree","label":"#1012","item":"Root › Child"}""",
        }).Concat(Close($$"""{"t":70,"n":8,"type":"dialog_click","for":2,"button":"Place","id":1024,"index":1,"fields":[{{Tree}}],"read":3}""")).ToArray());
        Assert.Contains("Select `Root › Child` in the tree, click **Place**", md);
    }

    [Fact]
    public void A_value_that_is_not_the_picked_item_stays_with_the_button()
    {
        // clicked Base, then an arrow key moved the selection to Wall before the button: the value is Wall, not the picked item
        const string TreeWall = """{"key":"id:1012","label":"#1012","kind":"tree","before":"","after":"Root › Wall"}""";
        var md = Script(Open().Concat(new[]
        {
            """{"t":30,"n":4,"type":"dialog_pick","for":2,"kind":"tree","label":"#1012","item":"Root › Base"}""",
            """{"t":31,"n":5,"type":"shot","for":4,"file":"screenshots/003-dialog-click.png","what":"dialog-click"}""",
        }).Concat(Close($$"""{"t":70,"n":8,"type":"dialog_click","for":2,"button":"Place","id":1024,"index":1,"fields":[{{TreeWall}}],"read":3}""")).ToArray());
        Assert.Contains("Select `Root › Base` in the tree", md);
        Assert.Contains("Select `Root › Wall` in the tree, click **Place**", md);
    }

    [Fact]
    public void An_unreadable_item_is_named_by_its_row()
    {
        var md = Script(Open().Concat(new[]
        {
            """{"t":40,"n":4,"type":"dialog_pick","for":2,"kind":"listview","label":"#1007","item":"","row":3}""",
            """{"t":41,"n":5,"type":"shot","for":4,"file":"screenshots/003-dialog-click.png","what":"dialog-click"}""",
        }).Concat(Close($$"""{"t":70,"n":8,"type":"dialog_click","for":2,"button":"Place","id":1024,"index":1,"fields":[],"read":3}""")).ToArray());
        Assert.Contains("Select row 3 in the list", md);
        Assert.DoesNotContain("``", md);
    }

    [Fact]
    public void A_pick_after_the_last_click_of_a_cancelled_dialog_is_not_written_as_done()
    {
        var md = Script(Open().Concat(new[]
        {
            """{"t":40,"n":4,"type":"dialog_pick","for":2,"kind":"tree","label":"#1012","item":"Root › Base"}""",
            """{"t":41,"n":5,"type":"shot","for":4,"file":"screenshots/003-dialog-click.png","what":"dialog-click"}""",
            """{"t":80,"n":9,"type":"dialog_values","for":2,"title":"Quick add","fields":[],"read":3,"closedWith":"cancel"}""",
            """{"t":81,"n":10,"type":"modal_leave"}""",
        }).ToArray());
        Assert.DoesNotContain("Select `Root › Base`", md);
        Assert.Contains("Cancel", md);
    }

    [Fact]
    public void The_arrow_comes_from_the_quiet_side()
    {
        // a busy band (text) in the upper left of the target, nothing on the right: the tail goes right
        int w = 200, h = 120;
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = px[i + 1] = px[i + 2] = 255; px[i + 3] = 255; }
        for (int y = 10; y < 60; y += 4) for (int x = 20; x < 70; x++) { int i = (y * w + x) * 4; px[i] = px[i + 1] = px[i + 2] = 0; }
        var a = ClickArrow.ForImageQuiet(60, 70, new PxRect(0, 0, w, h), w, h, px)!.Value;
        Assert.True(a.StartX > a.TipX, "the tail is on the right: " + a);
        // nothing anywhere: the old direction, upper left
        var blank = new byte[w * h * 4]; Array.Fill(blank, (byte)255);
        var b = ClickArrow.ForImageQuiet(100, 80, new PxRect(0, 0, w, h), w, h, blank)!.Value;
        Assert.True(b.StartX < b.TipX && b.StartY < b.TipY, b.ToString());
    }

    [Theory]
    [InlineData(50, 50, 40, 16)]    // a tree row
    [InlineData(500, 20, 80, 28)]   // a button
    [InlineData(10, 90, 40, 16)]    // near the picture's edge
    public void The_arrow_for_a_rectangle_ends_at_its_corner_and_leaves_the_text_free(int l, int t, int w, int h)
    {
        int iw = 622, ih = 362;
        var px = new byte[iw * ih * 4]; Array.Fill(px, (byte)255);
        var box = new PxRect(l, t, l + w, t + h);
        var a = ClickArrow.ForBoxQuiet(box, new PxRect(0, 0, iw, ih), iw, ih, px)!.Value;
        Assert.True(a.TipX >= box.Left && a.TipX < box.Right && a.TipY >= box.Top && a.TipY < box.Bottom, "the tip is in the box: " + a);
        Assert.True(Math.Min(a.TipX - box.Left, box.Right - 1 - a.TipX) <= 1 && Math.Min(a.TipY - box.Top, box.Bottom - 1 - a.TipY) <= 1, "at a corner: " + a);
        // the shaft and the head are outside the rectangle: nothing but the tip is over the label area
        int inner = 0;
        for (int i = 0; i <= 100; i++)
        {
            int x = a.TipX + (a.StartX - a.TipX) * i / 100, y = a.TipY + (a.StartY - a.TipY) * i / 100;
            if (x >= box.Left + 3 && x < box.Right - 3 && y >= box.Top + 3 && y < box.Bottom - 3) inner++;
        }
        Assert.Equal(0, inner);
        Assert.InRange(a.StartX, 0, iw - 1); Assert.InRange(a.StartY, 0, ih - 1);
    }

    [Theory]
    [InlineData("#1012", true)]
    [InlineData(" #7 ", true)]
    [InlineData("#", false)]
    [InlineData("Parts", false)]
    [InlineData("#1012 Parts", false)]
    [InlineData(null, false)]
    public void An_id_only_label_is_detected(string? label, bool expected) => Assert.Equal(expected, ControlWords.IsIdOnly(label));

    [Theory]
    [InlineData("tree", "the tree")]
    [InlineData("listview", "the list")]
    [InlineData("list", "the list")]
    [InlineData("combo", "the drop-down list")]
    [InlineData("check", "the check box")]
    [InlineData("edit", "the field")]
    public void An_id_only_control_is_named_by_its_kind(string kind, string expected) => Assert.Equal(expected, ControlWords.Name("#1012", kind, l => "**" + l + "**"));

    [Fact]
    public void A_named_control_keeps_its_label() => Assert.Equal("**Parts**", ControlWords.Name("Parts", "tree", l => "**" + l + "**"));

    [Fact]
    public void The_settings_split_the_test_document_arrows_from_the_training_ones()
    {
        var s = new RetraceSettings();
        Assert.True(s.TestDocArrows);
        Assert.False(s.ShowClickArrows);
        var back = System.Text.Json.JsonSerializer.Deserialize<RetraceSettings>("""{"showClickArrows":true}""")!;
        Assert.True(back.ShowClickArrows);
        Assert.True(back.TestDocArrows);   // an old settings file: the test document gets its arrows, the training document keeps its value
        Assert.False(System.Text.Json.JsonSerializer.Deserialize<RetraceSettings>("""{"testDocArrows":false}""")!.TestDocArrows);
    }
}
