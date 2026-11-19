using DocumentFormat.OpenXml.Packaging;
using Retrace.Core;
using Retrace.Engine.Emit;
using Retrace.Engine.Recordings;
using Xdr = DocumentFormat.OpenXml.Drawing.Spreadsheet;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace Retrace.Tests;

/// <summary>Which evidence picture sits on which sheet row (EvidenceRows), from the unit level to the workbook. Synthetic data only.</summary>
public class EvidenceRowsTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("retrace-test-evrows").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    static ReplayStepInfo Info(int index, string kind, string status = "ok", string? opened = null, string? filled = null, string? ended = null, string? failed = null, string? message = null) =>
        new(index, index * 100L, status, message, ended ?? failed, false, kind, opened, filled, ended, failed);

    static WordingBullet B(WordingBulletKind k) => new(k, 0);

    // ---- the row rules ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Click_then_dialog_then_prompts_gives_each_row_its_own_picture_and_the_end_state_goes_on_the_last_row()
    {
        // rows: 1 the click, 1.1 the dialog, 1.2 the insertion point, 1.3 the rotation
        var steps = new[] { Info(0, "ribbon"), Info(1, "dialog", opened: "open.png", filled: "filled.png", ended: "end.png") };
        var rows = EvidenceRows.Place("1", steps, [B(WordingBulletKind.Dialog), B(WordingBulletKind.Answer), B(WordingBulletKind.Answer)], 4);
        Assert.Equal("open.png", rows[0].Picture);      // AutoCAD with the dialog open
        Assert.Equal("filled.png", rows[1].Picture);    // the dialog as filled in
        Assert.Null(rows[2].Picture);                   // typed in one go with the next row
        Assert.Contains("1.3", rows[2].Note);
        Assert.Equal("end.png", rows[3].Picture);       // the end state: last row only
        Assert.DoesNotContain(rows, r => r.Key != 3 && r.Value.Picture == "end.png");
    }

    [Fact]
    public void A_dialog_with_one_answer_after_it_has_three_rows_and_three_pictures()
    {
        var steps = new[] { Info(0, "ribbon"), Info(1, "dialog", opened: "o", filled: "f", ended: "e") };
        var rows = EvidenceRows.Place("1", steps, [B(WordingBulletKind.Dialog), B(WordingBulletKind.Answer)], 3);
        Assert.Equal(new[] { "o", "f", "e" }, new[] { rows[0].Picture, rows[1].Picture, rows[2].Picture });
        Assert.All(rows.Values, r => Assert.Null(r.Note));
    }

    [Fact]
    public void Two_dialogs_in_one_command_put_the_prompts_between_them_on_the_second_dialogs_opening()
    {
        // 1 click, 1.1 dialog A, 1.2 answer between, 1.3 dialog B, 1.4 answer after
        var steps = new[] { Info(0, "command"), Info(1, "dialog", opened: "oA", filled: "fA"), Info(2, "dialog", opened: "oB", filled: "fB", ended: "end") };
        var rows = EvidenceRows.Place("1", steps, [B(WordingBulletKind.Dialog), B(WordingBulletKind.Answer), B(WordingBulletKind.Dialog), B(WordingBulletKind.Answer)], 5);
        Assert.Equal("oA", rows[0].Picture); Assert.Equal("fA", rows[1].Picture);
        Assert.Equal("oB", rows[2].Picture);   // the prompts between: the screen when the second dialog is open
        Assert.Equal("fB", rows[3].Picture); Assert.Equal("end", rows[4].Picture);
    }

    [Fact]
    public void A_command_without_a_dialog_puts_its_end_state_on_the_last_row_only()
    {
        var steps = new[] { Info(0, "command", ended: "end") };
        var rows = EvidenceRows.Place("2", steps, [B(WordingBulletKind.Answer), B(WordingBulletKind.Answer)], 3);
        Assert.Null(rows[0].Picture); Assert.Null(rows[1].Picture);
        Assert.Contains("2.2", rows[1].Note);
        Assert.Equal("end", rows[2].Picture);
    }

    [Fact]
    public void A_palette_a_status_bar_click_and_a_checkpoint_row_each_get_their_own_end_picture()
    {
        foreach (var kind in new[] { "palette", "statusbar", "mark" })
        {
            var rows = EvidenceRows.Place("3", [Info(4, kind, ended: "e.png")], [], 1);
            Assert.Equal("e.png", rows[0].Picture);
        }
    }

    [Fact]
    public void A_click_whose_dialog_is_a_step_of_its_own_shows_that_dialog_open_on_the_clicks_row()
    {
        var rows = EvidenceRows.Place("7", [Info(6, "palette")], [], 1, nextDialogOpened: "next-open.png");
        Assert.Equal("next-open.png", rows[0].Picture);
        // its own end picture wins when there is one
        Assert.Equal("e.png", EvidenceRows.Place("7", [Info(6, "palette", ended: "e.png")], [], 1, "next-open.png")[0].Picture);
        // through the mapping: step 1 is a palette click with no end picture, step 2 the dialog it opened
        var steps = new List<Step> { new() { Kind = "palette", T = 100 }, new() { Kind = "dialog", T = 900 } };
        var open = Png("o2.png"); var filled = Png("f2.png");
        var r = new ReplayResult { Verdict = "PASS" };
        r.Steps.Add(new StepResult { Index = 1, Status = "ok" }); r.Steps.Add(new StepResult { Index = 2, Status = "ok" });
        var bullets = new List<IReadOnlyList<WordingBullet>> { new List<WordingBullet>(), new List<WordingBullet>() };
        var o = EvidenceMapping.Outcomes(steps, r, new string?[2], [100, 900], -1, EvidenceEnd.Completed, [null, new StepShots(Opened: open, Filled: filled)], bullets, [1, 1]);
        Assert.Equal(open, o[1].Rows![0].Picture);
        Assert.Equal(filled, o[2].Rows![0].Picture);
    }

    [Fact]
    public void A_status_bar_click_that_opens_a_dialog_pictures_the_dialog_open_then_filled_then_the_end()
    {
        var steps = new[] { Info(0, "statusbar"), Info(1, "dialog", opened: "o", filled: "f", ended: "e") };
        var rows = EvidenceRows.Place("1", steps, [B(WordingBulletKind.Dialog)], 2);
        Assert.Equal("o", rows[0].Picture); Assert.Equal("f", rows[1].Picture);
    }

    [Fact]
    public void A_top_level_dialog_row_is_the_dialog_filled_in_and_a_tab_step_is_ignored()
    {
        var rows = EvidenceRows.Place("2", [Info(3, "tab", ended: "tab.png"), Info(4, "dialog", opened: "o", filled: "f", ended: "e")], [], 1);
        Assert.Equal("f", rows[0].Picture);
    }

    [Fact]
    public void A_dialog_that_could_not_be_pictured_filled_in_shows_as_opened_and_says_so()
    {
        var rows = EvidenceRows.Place("1", [Info(0, "ribbon"), Info(1, "dialog", opened: "o", ended: "e")], [B(WordingBulletKind.Dialog)], 2);
        Assert.Equal("o", rows[1].Picture);
        Assert.Contains("as it opened", rows[1].Note);
        var none = EvidenceRows.Place("1", [Info(0, "ribbon"), Info(1, "dialog")], [B(WordingBulletKind.Dialog)], 2);
        Assert.Null(none[1].Picture);
        Assert.Contains("No picture", none[1].Note);
        var top = EvidenceRows.Place("2", [Info(3, "dialog", status: "failed", ended: "after.png")], [], 1);   // a top-level dialog that never showed: the screen after the step, said so
        Assert.Equal("after.png", top[0].Picture);
        Assert.Contains("not on the screen", top[0].Note);
    }

    [Fact]
    public void A_failed_dialog_step_keeps_the_failure_picture_on_its_own_row_and_no_later_picture_anywhere()
    {
        var steps = new[] { Info(0, "ribbon"), Info(1, "dialog", status: "failed", opened: "o", failed: "fail.png") };
        var rows = EvidenceRows.Place("1", steps, [B(WordingBulletKind.Dialog), B(WordingBulletKind.Answer)], 3);
        Assert.Equal("fail.png", rows[1].Picture);
        Assert.Null(rows[2].Picture);   // the end never came: nothing is borrowed
        Assert.Equal("o", rows[0].Picture);
    }

    [Fact]
    public void When_the_sheet_and_the_steps_do_not_line_up_only_the_last_row_gets_the_end_picture()
    {
        var rows = EvidenceRows.Place("1", [Info(0, "ribbon", ended: "e")], [B(WordingBulletKind.Dialog)], 2);   // a dialog row but no dialog step
        Assert.Null(rows[0].Picture);
        Assert.Equal("e", rows[1].Picture);
        Assert.Contains("do not line up", rows[1].Note);
    }

    // ---- through the mapping and into the workbook -------------------------------------------------------------------------

    string Png(string name)
    {
        var f = Path.Combine(_dir, "evidence", name);
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        var b = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 7, 128, 0, 0, 4, 56 };
        b.AddRange([8, 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, (byte)'I', (byte)'E', (byte)'N', (byte)'D', 0, 0, 0, 0]);
        File.WriteAllBytes(f, b.ToArray());
        return f;
    }

    /// <summary>A recording of the reported shape: a ribbon click that starts a command, which opens a dialog (set a value, OK) and then asks for a point and a rotation.</summary>
    string Recording()
    {
        var lines = new[]
        {
            """{"type":"header","schema":3,"name":"rows","started":"2026-11-19T10:00:00","autocad":"25.1.0.0","product":"AutoCAD 2026"}""",
            """{"t":100,"n":1,"type":"ribbon","id":"ID_Ins","text":"Insert part","macro":"^C^C_insertpart","tab":"Home","panel":"Draw"}""",
            """{"t":110,"n":2,"type":"cmd_start","command":"INSERTPART","origin":"ribbon"}""",
            """{"t":200,"n":3,"type":"modal_enter","title":""}""",
            """{"t":210,"n":4,"type":"modal_title","title":"Insert part"}""",
            """{"t":900,"n":5,"type":"modal_leave"}""",
            """{"t":910,"n":6,"type":"dialog_values","for":3,"fields":[{"key":"name:Size","label":"Size","kind":"edit","before":"1","after":"2"}],"read":2,"closedWith":"ok"}""",
            """{"t":1200,"n":7,"type":"prompt","kind":"point","message":"Specify insertion point:","status":"OK","value":[5,5,0],"input":true,"by":"mouse"}""",
            """{"t":1500,"n":8,"type":"prompt","kind":"angle","message":"Specify rotation angle:","status":"OK","value":0,"input":true,"by":"keys"}""",
            """{"t":1700,"n":9,"type":"cmd_end","command":"INSERTPART"}""",
            """{"t":3000,"n":10,"type":"session_end"}""",
        };
        var path = Path.Combine(_dir, "recording.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void The_reported_test_writes_three_pictures_on_three_rows_with_the_end_state_on_the_last()
    {
        var path = Recording();
        var one = RecordingReader.Load(path);
        RecordingAnalysis.Run([one], null);
        var numberedT = new List<long>(); var bullets = new List<IReadOnlyList<WordingBullet>>();
        foreach (var st in one.Steps)
        {
            var l = new List<WordingBullet>();
            if (StepWording.Render(st, numberedT.Count + 1, new WordingContext { Script = true, Bullets = l }) == null) continue;
            numberedT.Add(st.T); bullets.Add(l);
        }
        var doc = StepsDoc.Load(ScriptWriter.WriteFor(path, null, [], out _));
        var labels = doc.Rows.Select(r => r.Label).ToList();
        var steps = Plan.Build(Plan.ReadEvents(path)).Where(s => s.Kind != "tab").ToList();
        Assert.Equal(2, steps.Count);   // the replay runs 2 steps for these rows
        Assert.Equal(1 + bullets[0].Count, labels.Count);

        var shots = new List<StepShots?> { new(Opened: null, Ended: null), new(Opened: Png("open.png"), Filled: Png("filled.png"), Ended: Png("end.png")) };
        var r = new ReplayResult { Verdict = "PASS" };
        for (int i = 0; i < steps.Count; i++) r.Steps.Add(new StepResult { Index = i + 1, Kind = steps[i].Kind, Status = "ok" });
        var o = EvidenceMapping.Outcomes(steps, r, new string?[steps.Count], numberedT, -1, EvidenceEnd.Completed, shots, bullets, EvidenceRows.RowCounts(doc, numberedT.Count));
        var ev = EvidenceBook.FromOutcomes(doc, o, [], "t", "d");
        var xlsx = EvidenceBook.Write(doc, _dir, ev, new DateTime(2026, 11, 19, 9, 0, 0));

        using var xl = SpreadsheetDocument.Open(xlsx, false);
        var sheet = (WorksheetPart)xl.WorkbookPart!.GetPartById(xl.WorkbookPart.Workbook!.Sheets!.Elements<S.Sheet>().First().Id!);
        var anchors = sheet.DrawingsPart!.WorksheetDrawing!.Descendants<Xdr.OneCellAnchor>().Where(a => a.FromMarker!.ColumnId!.Text == "8").ToList();
        // sheet rows start at 7 (row index 6, 0-based): one picture each on the click, the dialog and the last answer; none on the middle answer
        var byRow = anchors.Select(a => int.Parse(a.FromMarker!.RowId!.Text)).OrderBy(x => x).ToList();
        Assert.Equal(3, anchors.Count);
        Assert.Equal(new[] { labels[0], labels[1], labels[3] }, byRow.Select(x => labels[x - 6]).ToArray());
    }

    [Fact]
    public void A_picture_filed_by_the_camera_on_row_1_1_lands_on_that_row_of_the_workbook()
    {
        var doc = new StepsDoc("# t\n\n## Steps\n\n1. Click **Go**\n\n   - Pick a point\n   - Type a rotation\n\n2. Click **Stop**\n", _dir);
        var a = Png(EvidenceFiles.FileName("1.1", new DateTime(2026, 11, 19, 10, 0, 0, 1)));
        Assert.Equal("1.1", EvidenceFiles.RowOf(a));
        var ev = EvidenceBook.FromTester(doc, _dir, new Dictionary<string, ManualRow>(), "T", "2026-11-19");
        Assert.Equal(new[] { a }, ev.Rows["1.1"].Pictures);
        Assert.False(ev.Rows.ContainsKey("1"));
        var xlsx = EvidenceBook.Write(doc, _dir, ev, new DateTime(2026, 11, 19, 10, 5, 0));
        using var xl = SpreadsheetDocument.Open(xlsx, false);
        var sheet = (WorksheetPart)xl.WorkbookPart!.GetPartById(xl.WorkbookPart.Workbook!.Sheets!.Elements<S.Sheet>().First().Id!);
        var anchor = Assert.Single(sheet.DrawingsPart!.WorksheetDrawing!.Descendants<Xdr.OneCellAnchor>(), x => x.FromMarker!.ColumnId!.Text == "8");
        Assert.Equal(1, doc.Rows.FindIndex(r => r.Label == "1.1"));
        Assert.Equal((6 + 1).ToString(), anchor.FromMarker!.RowId!.Text);
    }

    [Fact]
    public void Results_follow_the_rules_and_a_failed_dialog_leaves_later_steps_not_run_with_the_failure_picture_on_the_dialog_row()
    {
        var doc = new StepsDoc("# t\n\n## Steps\n\n1. Click **Go**\n\n   - A dialog opens: **D**. Make these changes, then confirm:\n     - Set **A** to `1`\n   - Pick a point\n\n2. Click **Next**\n", _dir);
        var steps = new List<Step> { new() { Kind = "ribbon", T = 100 }, new() { Kind = "dialog", T = 200 }, new() { Kind = "command", T = 900 } };
        var bullets = new List<IReadOnlyList<WordingBullet>> { new[] { B(WordingBulletKind.Dialog), B(WordingBulletKind.Answer) }, new List<WordingBullet>() };
        var fail = Png("fail.png"); var open = Png("o.png");
        var r = new ReplayResult { Verdict = "ERROR", Message = "dialog did not fill" };
        r.Steps.Add(new StepResult { Index = 1, Status = "ok" }); r.Steps.Add(new StepResult { Index = 2, Status = "failed", Message = "could not set A" });
        var shots = new List<StepShots?> { null, new(Opened: open, Failed: fail), null };
        var o = EvidenceMapping.Outcomes(steps, r, new string?[3], [100, 900], 1, EvidenceEnd.Error, shots, bullets, EvidenceRows.RowCounts(doc, 2));
        Assert.Equal("Fail", o[1].Result); Assert.Equal("Not run", o[2].Result);
        var ev = EvidenceBook.FromOutcomes(doc, o, [], "t", "d");
        Assert.Equal(new[] { open }, ev.Rows["1"].Pictures);
        Assert.Equal(new[] { fail }, ev.Rows["1.1"].Pictures);
        Assert.Empty(ev.Rows["1.2"].Pictures);
        Assert.Empty(ev.Rows["2"].Pictures);
        Assert.Equal("Not run", ev.Rows["2"].Result);
    }
}
