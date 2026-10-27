using Retrace.Engine.Recordings;

namespace Retrace.Tests;

/// <summary>Automatic checkpoint names, reading checkpoints back and renaming them in recording.jsonl. Synthetic data only.</summary>
public class CheckpointTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("retrace-test-checkpoints").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    static Checkpoints.CheckpointContext Dialog(string title, string? closed) => new("dialog", title, closed);

    [Fact]
    public void Automatic_names_say_which_checkpoint_and_where_it_was_taken()
    {
        Assert.Equal("checkpoint1", Checkpoints.AutoName(1, null));
        Assert.Equal("checkpoint1_draw_grid_button_ok", Checkpoints.AutoName(1, Dialog("Draw grid", "ok")));
        Assert.Equal("checkpoint2_layer_properties_button_cancel", Checkpoints.AutoName(2, Dialog("Layer Properties...", "cancel")));
        Assert.Equal("checkpoint3_drawing_units", Checkpoints.AutoName(3, Dialog("Drawing Units", null)));   // closed some other way
        Assert.Equal("checkpoint4_draw_button_grid", Checkpoints.AutoName(4, new("ribbon", "Draw", "Grid...")));
        Assert.Equal("checkpoint5_cmd_drawgrid", Checkpoints.AutoName(5, new("command", null, "DRAWGRID")));
        Assert.Equal("checkpoint6", Checkpoints.AutoName(6, Dialog("", null)));                               // untitled dialog
        Assert.Equal("checkpoint7_cmd_lisp", Checkpoints.AutoName(7, new("command", null, "lisp")));
    }

    [Fact]
    public void Name_parts_are_lowercase_words_joined_by_underscores_and_capped()
    {
        Assert.Equal("save_as", Checkpoints.Token("&Save As..."));
        Assert.Equal("größe_2", Checkpoints.Token("Größe (2)"));
        Assert.Equal("", Checkpoints.Token("  ---  "));
        Assert.Equal(30, Checkpoints.Token(new string('a', 50)).Length);
        Assert.True(Checkpoints.AutoName(12, Dialog(new string('x', 200), "ok")).Length <= Checkpoints.MaxName);
    }

    [Fact]
    public void Names_are_stored_on_one_line_trimmed_and_capped()
    {
        Assert.Equal("Grid has 12 points", Checkpoints.Clean("  Grid\thas\r\n12   points "));
        Assert.Equal("", Checkpoints.Clean(null));
        Assert.Equal(Checkpoints.MaxName, Checkpoints.Clean(new string('a', 500)).Length);
    }

    [Fact]
    public void A_dash_or_underscore_form_of_our_command_is_ours_too()
    {
        Assert.True(OwnCommands.IsOwnCommand("-RETRACEMARK"));
        Assert.True(OwnCommands.IsOwnCommand("_RETRACECHECKPOINTS"));
        Assert.True(OwnCommands.IsOwnCommand("-retracesettings"));
        Assert.False(OwnCommands.IsOwnCommand("-LAYER"));
        Assert.False(OwnCommands.IsOwnCommand(null));
    }

    const string Header = """{"type":"header","schema":1,"name":"grid test","started":"2026-10-26T19:40:00+00:00","autocad":"25.1.0.0","product":"AutoCAD 2026"}""";

    /// <summary>DRAWGRID with its dialog (OK), a checkpoint named by the new recorder, LINE typed, then a checkpoint of an older recorder (no autoName, empty note).</summary>
    static readonly string[] Lines =
    {
        Header,
        """{"t":5,"n":1,"type":"state","state":{"count":0,"byType":{},"byLayer":{},"layers":["0"],"hash":"e3","v":2}}""",
        """{"t":100,"n":2,"type":"cmd_start","command":"DRAWGRID","origin":"typed/other"}""",
        """{"t":110,"n":3,"type":"modal_enter","title":"AutoCAD"}""",
        """{"t":200,"n":4,"type":"modal_title","title":"Draw grid"}""",
        """{"t":1900,"n":5,"type":"dialog_values","for":4,"title":"Draw grid","source":"win32","fields":[{"key":"id:1","label":"Layer name","kind":"edit","before":"GRID","after":"MYGRID"}],"read":4,"closedWith":"ok"}""",
        """{"t":1910,"n":6,"type":"modal_leave"}""",
        """{"t":2500,"n":7,"type":"prompt","kind":"point","input":true,"by":"keys","message":"Lower-left point of the grid:","status":"OK","value":[2,2,0]}""",
        """{"t":2510,"n":8,"type":"cmd_end","command":"DRAWGRID"}""",
        """{"t":3000,"n":9,"type":"mark","note":"checkpoint1_draw_grid_button_ok","autoName":"checkpoint1_draw_grid_button_ok","state":{"count":12,"byType":{"POINT":12},"byLayer":{"MYGRID":12},"layers":["0","MYGRID"],"hash":"aa","v":2}}""",
        """{"t":3100,"n":10,"type":"shot","for":9,"file":"screenshots/009-mark.png","what":"window"}""",
        """{"t":4000,"n":11,"type":"cmd_start","command":"LINE","origin":"typed/other"}""",
        """{"t":4100,"n":12,"type":"prompt","kind":"point","input":false,"message":"Specify first point:","status":"OK","value":[0,0,0]}""",
        """{"t":4200,"n":13,"type":"cmd_end","command":"LINE"}""",
        """{"t":5000,"n":14,"type":"mark","note":"","state":{"count":13,"byType":{"LINE":1,"POINT":12},"byLayer":{"0":1,"MYGRID":12},"layers":["0","MYGRID"],"hash":"bb","v":2}}""",
        """{"t":6000,"n":15,"type":"session_end"}""",
    };

    string Recording()
    {
        var dir = Path.Combine(_root, "2026-10-26_1940_grid-test");
        Directory.CreateDirectory(Path.Combine(dir, "screenshots"));
        File.WriteAllBytes(Path.Combine(dir, "screenshots", "009-mark.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        var rec = Path.Combine(dir, "recording.jsonl");
        File.WriteAllText(rec, string.Join("\n", Lines) + "\n");
        return rec;
    }

    [Fact]
    public void Read_lists_the_checkpoints_with_step_context_state_and_picture()
    {
        var rec = Recording();
        var list = Checkpoints.Read(rec, out var test);
        Assert.Equal("grid test", test);
        Assert.Equal(2, list.Count);

        var a = list[0];
        Assert.Equal((9L, 1, 2), (a.N, a.Index, a.Step));   // step 1 = DRAWGRID, step 2 = the checkpoint
        Assert.Equal("checkpoint1_draw_grid_button_ok", a.Name);
        Assert.False(a.Renamed);
        Assert.Equal("OK in Draw grid", a.After);
        Assert.Contains("12 POINT", a.State);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(rec)!, "screenshots", "009-mark.png"), a.Picture);

        var b = list[1];   // an older recording's checkpoint: named here, nothing written
        Assert.Equal((14L, 2, 4), (b.N, b.Index, b.Step));
        Assert.Equal("checkpoint2_cmd_line", b.AutoName);
        Assert.Equal("checkpoint2_cmd_line", b.Name);
        Assert.Equal("LINE typed", b.After);
        Assert.Null(b.Picture);
    }

    [Fact]
    public void Rename_changes_only_the_note_of_those_checkpoints()
    {
        var rec = Recording();
        var before = File.ReadAllLines(rec);
        Assert.Equal(2, Checkpoints.Rename(rec, new Dictionary<long, string> { [9] = "  Grid of 12 points\non MYGRID ", [14] = "Line added" }));
        var after = File.ReadAllLines(rec);
        Assert.Equal(before.Length, after.Length);
        for (int i = 0; i < before.Length; i++)
            if (i != 9 && i != 14) Assert.Equal(before[i], after[i]);   // every other line byte for byte
        Assert.Contains("\"note\":\"Grid of 12 points on MYGRID\"", after[9]);
        Assert.Contains("\"autoName\":\"checkpoint1_draw_grid_button_ok\"", after[9]);
        Assert.Contains("\"hash\":\"aa\"", after[9]);
        Assert.Contains("\"note\":\"Line added\"", after[14]);
        Assert.False(File.Exists(rec + ".tmp"));

        var list = Checkpoints.Read(rec);
        Assert.Equal("Grid of 12 points on MYGRID", list[0].Name);
        Assert.True(list[0].Renamed);
        Assert.Equal("checkpoint1_draw_grid_button_ok", list[0].AutoName);
    }

    [Fact]
    public void An_empty_name_goes_back_to_the_automatic_one_and_unchanged_names_write_nothing()
    {
        var rec = Recording();
        Checkpoints.Rename(rec, new Dictionary<long, string> { [9] = "Renamed" });
        Assert.Equal(1, Checkpoints.Rename(rec, new Dictionary<long, string> { [9] = "   " }));
        Assert.Equal("checkpoint1_draw_grid_button_ok", Checkpoints.Read(rec)[0].Name);

        var stamp = File.GetLastWriteTimeUtc(rec);
        var bytes = File.ReadAllBytes(rec);
        Assert.Equal(0, Checkpoints.Rename(rec, new Dictionary<long, string> { [9] = "checkpoint1_draw_grid_button_ok" }));
        Assert.Equal(bytes, File.ReadAllBytes(rec));
    }

    [Fact]
    public void Rename_refuses_an_event_that_is_not_a_checkpoint_and_changes_nothing()
    {
        var rec = Recording();
        var bytes = File.ReadAllBytes(rec);
        var ex = Assert.Throws<InvalidDataException>(() => Checkpoints.Rename(rec, new Dictionary<long, string> { [9] = "ok", [8] = "not a mark" }));
        Assert.Contains("8", ex.Message);
        Assert.Equal(bytes, File.ReadAllBytes(rec));
    }

    [Fact]
    public void RenameCheckpoints_writes_the_steps_again_with_the_new_name()
    {
        var rec = Recording();
        var r = EngineRunner.RenameCheckpoints(rec, new Dictionary<long, string> { [9] = "Grid of 12 points" });
        Assert.True(r.Success, r.Error);
        var md = File.ReadAllText(Path.Combine(Path.GetDirectoryName(rec)!, "steps.md"));
        Assert.Contains("**Check:** Grid of 12 points", md);
        Assert.Contains("Check 1 (step 2): Grid of 12 points", md);
        Assert.Contains("checkpoint2_cmd_line", Checkpoints.Read(rec)[1].Name);   // the older one still reads with its automatic name
    }

    [Fact]
    public void Keys_use_the_name_without_the_checkpoint_number()
    {
        Assert.Equal("cmd_line", Checkpoints.WithoutNumber("checkpoint2_cmd_line"));
        Assert.Equal("", Checkpoints.WithoutNumber("checkpoint12"));
        Assert.Equal("checkpoints are fine", Checkpoints.WithoutNumber("checkpoints are fine"));
        Assert.Equal("checkpoint_x", Checkpoints.WithoutNumber("checkpoint_x"));
        Assert.Equal("Grid of 12 points", Checkpoints.WithoutNumber("Grid of 12 points"));
        Assert.Equal("", Checkpoints.WithoutNumber(null));
    }

    [Fact]
    public void A_Word_or_Excel_file_edited_after_Retrace_wrote_it_is_kept_on_rename()
    {
        var rec = Recording();
        var dir = Path.GetDirectoryName(rec)!;
        Assert.True(EngineRunner.WriteSteps(rec, docx: true, xlsx: true).Success);
        var xlsx = Path.Combine(dir, "steps.xlsx");
        File.SetLastWriteTimeUtc(xlsx, DateTime.UtcNow.AddHours(1));   // a tester saved results in it later
        var before = File.ReadAllBytes(xlsx);
        var r = EngineRunner.RenameCheckpoints(rec, new Dictionary<long, string> { [9] = "Grid" }, docx: false, xlsx: false);
        Assert.True(r.Success, r.Error);
        Assert.Equal(before, File.ReadAllBytes(xlsx));
        Assert.Contains(r.Warnings, w => w.Contains("steps.xlsx was changed after Retrace wrote it"));
        Assert.NotNull(r.Docx);   // the untouched Word file exists, so it is written again with the new name
        Assert.Contains("**Check:** Grid", File.ReadAllText(Path.Combine(dir, "steps.md")));
    }
    [Fact]
    public void An_edited_Excel_file_stays_kept_on_every_later_rename()
    {
        var rec = Recording();
        var dir = Path.GetDirectoryName(rec)!;
        Assert.True(EngineRunner.WriteSteps(rec, docx: false, xlsx: true).Success);
        var xlsx = Path.Combine(dir, "steps.xlsx");
        File.AppendAllText(xlsx, "results");                       // a tester saved it: size and time change
        File.SetLastWriteTimeUtc(xlsx, DateTime.UtcNow.AddSeconds(-30));   // ...but not long after steps.md, and before the next save
        var before = File.ReadAllBytes(xlsx);
        foreach (var name in new[] { "Grid", "Grid of points", "Grid again" })
        {
            var r = EngineRunner.RenameCheckpoints(rec, new Dictionary<long, string> { [9] = name }, docx: false, xlsx: true);
            Assert.True(r.Success, r.Error);
            Assert.Contains(r.Warnings, w => w.Contains("steps.xlsx was changed after Retrace wrote it"));
        }
        Assert.Equal(before, File.ReadAllBytes(xlsx));
    }

    [Fact]
    public void An_untouched_Excel_file_is_written_again_on_each_rename()
    {
        var rec = Recording();
        var dir = Path.GetDirectoryName(rec)!;
        Assert.True(EngineRunner.WriteSteps(rec, docx: false, xlsx: true).Success);
        foreach (var name in new[] { "Grid", "Grid of points" })
        {
            var r = EngineRunner.RenameCheckpoints(rec, new Dictionary<long, string> { [9] = name }, docx: false, xlsx: true);
            Assert.True(r.Success, r.Error);
            Assert.DoesNotContain(r.Warnings, w => w.Contains("was changed after Retrace wrote it"));
            Assert.NotNull(r.Xlsx);
        }
        Assert.Equal(true, Exporter.WrittenByRetrace(Path.Combine(dir, "steps.xlsx")));
    }
}
