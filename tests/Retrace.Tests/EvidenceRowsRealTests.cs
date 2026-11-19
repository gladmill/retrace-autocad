using Retrace.Core;
using Retrace.Engine.Emit;
using Retrace.Engine.Recordings;

namespace Retrace.Tests;

/// <summary>The row placement against real files: a recording, the steps.md written from it, and the failure index the replayer really uses; a steps.md that does not belong to the recording; inline status bar rows; the notes in a workbook.</summary>
public class EvidenceRowsRealTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("retrace-test-evrows2").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    const string Header = """{"type":"header","schema":3,"name":"rows","started":"2026-11-19T10:00:00","autocad":"25.1.0.0","product":"AutoCAD 2026"}""";

    static readonly string[] DialogShape =
    [
        Header,
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
    ];

    const string StatusClick = """{"t":1200,"n":4,"type":"statusbar_click","text":"Sample TB: edit","tooltip":"Edit","index":61,"kind":"pane","own":false,"button":"left"}""";
    static string Pick(int t, int n, double x) => $$"""{"t":{{t}},"n":{{n}},"type":"prompt","kind":"point","status":"OK","value":[{{x}},0,0],"message":"Specify point","input":true}""";

    static readonly string[] InlineStatus =
    [
        Header,
        """{"t":1000,"n":2,"type":"cmd_start","command":"LINE","origin":"ribbon"}""",
        Pick(1100, 3, 1), StatusClick, Pick(1300, 5, 2), Pick(1400, 6, 3),
        """{"t":1600,"n":8,"type":"cmd_end"}""",
        """{"t":3000,"n":10,"type":"session_end"}""",
    ];

    string Png(string name)
    {
        var f = Path.Combine(_dir, "evidence", name);
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        var b = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 7, 128, 0, 0, 4, 56 };
        b.AddRange([8, 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, (byte)'I', (byte)'E', (byte)'N', (byte)'D', 0, 0, 0, 0]);
        File.WriteAllBytes(f, b.ToArray());
        return f;
    }

    /// <summary>Everything EvidenceCapture.WriteWorkbook reads: the steps.md of the recording, the numbered steps with their sub-items, the replay's steps.</summary>
    sealed record World(string Path, StepsDoc Doc, List<long> NumberedT, List<IReadOnlyList<WordingBullet>> Bullets, List<Step> Steps);

    World Make(string[] lines, Func<string, string>? editSteps = null)
    {
        var path = System.IO.Path.Combine(_dir, "recording.jsonl");
        File.WriteAllLines(path, lines);
        var one = RecordingReader.Load(path);
        RecordingAnalysis.Run([one], null);
        var numberedT = new List<long>(); var bullets = new List<IReadOnlyList<WordingBullet>>();
        foreach (var st in one.Steps)
        {
            var l = new List<WordingBullet>();
            if (StepWording.Render(st, numberedT.Count + 1, new WordingContext { Script = true, Bullets = l }) == null) continue;
            numberedT.Add(st.T); bullets.Add(l);
        }
        var md = ScriptWriter.WriteFor(path, null, [], out _);
        if (editSteps != null) File.WriteAllText(md, editSteps(File.ReadAllText(md)));
        var steps = Plan.Build(Plan.ReadEvents(path)).Where(s => s.Kind != "tab").ToList();
        return new World(path, StepsDoc.Load(md), numberedT, bullets, steps);
    }

    static ReplayResult Result(World w, string verdict, params (int Index, string Status, string? Message)[] steps)
    {
        var r = new ReplayResult { Verdict = verdict, Message = "msg" };
        foreach (var (i, st, m) in steps) r.Steps.Add(new StepResult { Index = i, Kind = w.Steps[i - 1].Kind, Status = st, Message = m });
        return r;
    }

    static Dictionary<int, StepOutcome> Map(World w, ReplayResult r, List<StepShots?> shots, int endStep, EvidenceEnd end, bool perRow = true)
    {
        var shape = perRow ? EvidenceRows.Shape(w.Doc, w.NumberedT.Count) : null;
        return EvidenceMapping.Outcomes(w.Steps, r, new string?[w.Steps.Count], w.NumberedT, endStep, end, shots, shape == null ? null : w.Bullets, shape?.RowCounts, shape?.SubRowsFilled);
    }

    // ---- 1. the failure index the replayer uses ------------------------------------------------------------------------------

    [Fact]
    public void A_test_that_errors_at_the_click_that_should_open_a_dialog_keeps_the_failure_picture_on_the_clicks_row()
    {
        var w = Make(DialogShape);
        Assert.Equal(2, w.Steps.Count);   // the click and its dialog
        // Replayer.Finish files the failure under _acting: the click's index (0). The dialog step has no result line and no picture.
        var shots = new List<StepShots?> { new(Failed: Png("step-01-failed.png")), null };
        var r = Result(w, "ERROR", (1, "failed", "the ribbon item was not found"));
        var o = Map(w, r, shots, endStep: 0, EvidenceEnd.Error);
        var ev = EvidenceBook.FromOutcomes(w.Doc, o, [], "t", "d");
        Assert.Equal("Fail", ev.Rows["1"].Result);
        Assert.Equal(new[] { Png("step-01-failed.png") }, ev.Rows["1"].Pictures);   // the click's row, not "No picture was taken"
        Assert.Empty(ev.Rows["1.1"].Pictures);
        Assert.Empty(ev.Rows["1.3"].Pictures);
        Assert.DoesNotContain("No picture was taken for this row", ev.Rows["1"].Notes ?? "");
    }

    [Fact]
    public void A_test_that_ends_while_the_dialog_is_pending_has_its_failure_picture_on_the_click_too()
    {
        // the replayer's _acting is still the click (a dialog step never sets it): the dialog step is "not run", the failure picture belongs to step 0
        var w = Make(DialogShape);
        var shots = new List<StepShots?> { new(Failed: Png("pending-failed.png")), null };
        var r = Result(w, "ERROR", (1, "ok", null));
        var o = Map(w, r, shots, endStep: 0, EvidenceEnd.Error);
        Assert.Equal(Png("pending-failed.png"), o[1].Rows![0].Picture);
    }

    // ---- 3. a steps.md that is not the recording's script -------------------------------------------------------------------------

    [Fact]
    public void A_steps_md_whose_dialog_row_is_not_a_dialog_gets_the_last_row_only()
    {
        // a hand-edited steps.md: the dialog line no longer reads as a dialog filled in (same row count, same number of steps)
        var w = Make(DialogShape, md => md.Replace("A dialog opens: **Insert part**", "Look at **Insert part**").Replace("Fill in the **Insert part** dialog", "Look at **Insert part**"));
        var shape = EvidenceRows.Shape(w.Doc, w.NumberedT.Count)!;
        Assert.Equal(w.Bullets[0].Count, shape.Value.SubRowsFilled[0].Length);
        Assert.DoesNotContain(true, shape.Value.SubRowsFilled[0]);   // no sub-row is a dialog any more
        var shots = new List<StepShots?> { new(), new(Opened: Png("o.png"), Filled: Png("f.png"), Ended: Png("e.png")) };
        var r = Result(w, "PASS", (1, "ok", null), (2, "ok", null));
        var o = Map(w, r, shots, -1, EvidenceEnd.Completed);
        var ev = EvidenceBook.FromOutcomes(w.Doc, o, [], "t", "d");
        // nothing on the click or the dialog row: only the last row has the end picture
        Assert.Empty(ev.Rows["1"].Pictures);
        Assert.Empty(ev.Rows["1.1"].Pictures);
        Assert.Equal(new[] { Png("e.png") }, ev.Rows["1.3"].Pictures);
        Assert.Contains("do not line up", ev.Rows["1.3"].Notes);
    }

    [Fact]
    public void A_steps_md_with_another_number_of_steps_keeps_the_pictures_on_each_steps_own_row()
    {
        var w = Make(DialogShape, md => md.Replace("## Result", "2. Click **Extra**\n\n## Result"));
        Assert.NotEqual(w.NumberedT.Count, w.Doc.Steps.Count);
        Assert.Null(EvidenceRows.Shape(w.Doc, w.NumberedT.Count));
        var shots = new List<StepShots?> { new(), new(Opened: Png("o2.png"), Filled: Png("f2.png"), Ended: Png("e2.png")) };
        var r = Result(w, "PASS", (1, "ok", null), (2, "ok", null));
        var o = Map(w, r, shots, -1, EvidenceEnd.Completed);   // Shape null: the old placement
        Assert.Null(o[1].Rows);
        var ev = EvidenceBook.FromOutcomes(w.Doc, o, [], "t", "d");
        Assert.Equal(new[] { Png("e2.png") }, ev.Rows["1"].Pictures);   // the old rule: the step's last picture on its own row
        Assert.Empty(ev.Rows["1.1"].Pictures);
    }

    [Fact]
    public void The_real_script_of_the_reported_shape_passes_the_dialog_row_check()
    {
        var w = Make(DialogShape);
        var shape = EvidenceRows.Shape(w.Doc, w.NumberedT.Count)!;
        Assert.Equal(new[] { true, false, false }, shape.Value.SubRowsFilled[0]);   // 1.1 is the dialog row
        var shots = new List<StepShots?> { new(), new(Opened: Png("ro.png"), Filled: Png("rf.png"), Ended: Png("re.png")) };
        var o = Map(w, Result(w, "PASS", (1, "ok", null), (2, "ok", null)), shots, -1, EvidenceEnd.Completed);
        Assert.Equal(Png("ro.png"), o[1].Rows![0].Picture);
        Assert.Equal(Png("rf.png"), o[1].Rows![1].Picture);
        Assert.Equal(Png("re.png"), o[1].Rows![3].Picture);
    }

    // ---- 8. inline status bar rows from real output -------------------------------------------------------------------------------------

    [Fact]
    public void An_inline_status_bar_row_of_a_real_sheet_says_the_replay_does_not_click_it_and_takes_no_picture()
    {
        var w = Make(InlineStatus);
        var rowsOfStep = w.Doc.Rows.Where(r => EvidenceRows.MainOf(r.Label) == 1).ToList();
        Assert.Equal(1 + w.Bullets[0].Count, rowsOfStep.Count);
        int status = w.Bullets[0].ToList().FindIndex(b => b.Kind == WordingBulletKind.Status) + 1;
        Assert.True(status > 0);
        Assert.Contains("a replay does not click it", rowsOfStep[status].Action);
        var shots = new List<StepShots?> { new(Ended: Png("line-end.png")) };
        var o = Map(w, Result(w, "PASS", (1, "ok", null)), shots, -1, EvidenceEnd.Completed);
        var ev = EvidenceBook.FromOutcomes(w.Doc, o, [], "t", "d");
        var label = rowsOfStep[status].Label;
        Assert.Empty(ev.Rows[label].Pictures);
        Assert.Contains("does not click this", ev.Rows[label].Notes);
        Assert.Equal(new[] { Png("line-end.png") }, ev.Rows[rowsOfStep[^1].Label].Pictures);   // the end state: the last row
    }

    // ---- 2. no product name in the workbook text ------------------------------------------------------------------------------------

    [Fact]
    public void No_note_the_placement_writes_into_a_workbook_names_the_product()
    {
        var notes = new List<string?>();
        ReplayStepInfo I(int i, string kind, string? o = null, string? f = null, string? e = null, string? fail = null, bool nw = false) => new(i, i * 100L, "ok", null, e ?? fail, false, kind, o, f, e, fail, nw);
        foreach (var rows in new[]
        {
            EvidenceRows.Place("1", [I(0, "ribbon"), I(1, "dialog", o: "o", e: "e")], [new(WordingBulletKind.Dialog, 0), new(WordingBulletKind.Answer, 0), new(WordingBulletKind.Answer, 0)], 4),
            EvidenceRows.Place("1", [I(0, "ribbon", e: "e")], [new(WordingBulletKind.Dialog, 0)], 2),
            EvidenceRows.Place("1", [I(0, "ribbon"), I(1, "dialog", nw: true)], [new(WordingBulletKind.Dialog, 0), new(WordingBulletKind.Status, 0)], 3),
            EvidenceRows.Place("2", [I(3, "dialog", e: "after")], [], 1),
            EvidenceRows.Place("1", [I(0, "command")], [new(WordingBulletKind.Answer, 0), new(WordingBulletKind.Answer, 0)], 3),
        })
            notes.AddRange(rows.Values.Select(v => v.Note));
        Assert.Contains(notes, n => n != null);
        Assert.All(notes.Where(n => n != null), n => Assert.DoesNotContain("retrace", n!, StringComparison.OrdinalIgnoreCase));
    }

    // ---- 7. the fallback picture --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_fallback_uses_the_last_end_picture_there_is_and_words_the_note_by_whether_there_is_one()
    {
        ReplayStepInfo I(int i, string kind, string? e = null, string? fail = null) => new(i, i * 100L, "ok", null, e ?? fail, false, kind, null, null, e, fail);
        // the last step has none, an earlier one has: that one
        var withEarlier = EvidenceRows.Place("1", [I(0, "ribbon", e: "first-end"), I(1, "dialog")], [new(WordingBulletKind.Answer, 0)], 2);   // dialog step but no dialog row: fallback
        Assert.Equal("first-end", withEarlier[1].Picture);
        Assert.Contains("this picture shows", withEarlier[1].Note);
        // nothing at all: no sentence about a picture
        var none = EvidenceRows.Place("1", [I(0, "ribbon"), I(1, "dialog")], [new(WordingBulletKind.Answer, 0)], 2);
        Assert.Null(none[1].Picture);
        Assert.DoesNotContain("this picture shows", none[1].Note);
        Assert.Contains("no picture was taken", none[1].Note);
    }
}
