using System.Security.Cryptography;
using DocumentFormat.OpenXml.Packaging;
using Retrace.Core;
using Retrace.Engine.Recordings;
using Xdr = DocumentFormat.OpenXml.Drawing.Spreadsheet;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace Retrace.Tests;

/// <summary>The evidence workbook (steps-evidence_*.xlsx): pictures in the Evidence column, results, notes, the original sheet untouched, and the evidence file names. Synthetic data only.</summary>
public class EvidenceSheetTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("retrace-test-evidence").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    const string Md = "# t\n\n## Steps\n\n1. Click **Go**\n\n   *Ribbon › Home tab*\n\n   ![a](screenshots/a.png)\n\n2. Type **LINE** and press Enter\n\n3. Click **Stop**\n";

    static byte[] PngOf(int w, int h)
    {
        var b = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' };
        foreach (var v in new[] { w, h }) b.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
        b.AddRange([8, 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, (byte)'I', (byte)'E', (byte)'N', (byte)'D', 0, 0, 0, 0]);
        return b.ToArray();
    }

    string Pic(string name, int w = 1920, int h = 1080)
    {
        var f = Path.Combine(_dir, "evidence", name);
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllBytes(f, PngOf(w, h));
        return f;
    }

    StepsDoc Doc()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "screenshots"));
        File.WriteAllBytes(Path.Combine(_dir, "screenshots", "a.png"), PngOf(1920, 1080));
        return new StepsDoc(Md, _dir);
    }

    static WorksheetPart Steps(SpreadsheetDocument xl) => (WorksheetPart)xl.WorkbookPart!.GetPartById(xl.WorkbookPart.Workbook!.Sheets!.Elements<S.Sheet>().First().Id!);
    static string CellText(WorksheetPart sheet, string reference) => sheet.Worksheet!.Descendants<S.Cell>().Single(c => c.CellReference!.Value == reference).InnerText;
    static double RowHeight(WorksheetPart sheet, int row) => sheet.Worksheet!.Descendants<S.Row>().Single(r => r.RowIndex!.Value == row).Height!.Value;

    [Fact]
    public void Evidence_pictures_sit_in_the_Evidence_cell_of_their_row_stacked_with_the_sheets_size_rule()
    {
        var doc = Doc();
        var ev = new EvidenceData { Tester = "A. Tester", Date = "2026-11-17" };
        ev.Rows["1"] = new EvidenceRow([Pic("step-1_20261117-100000-000.png"), Pic("step-1_20261117-100100-000.png", 1000, 500)], "Pass", "looks right");
        ev.Rows["3"] = new EvidenceRow([Pic("step-3_20261117-100200-000.png")], "Fail", "wrong layer");
        var path = Path.Combine(_dir, "e.xlsx");
        ExportXlsx.Write(doc, _dir, path, null, ev);
        using var xl = SpreadsheetDocument.Open(path, false);
        var sheet = Steps(xl);
        var anchors = sheet.DrawingsPart!.WorksheetDrawing!.Descendants<Xdr.OneCellAnchor>().ToList();
        Assert.Equal(4, anchors.Count);   // Retrace's picture of row 1, two evidence pictures of row 1, one of row 3
        var evidence = anchors.Where(a => a.FromMarker!.ColumnId!.Text == "8").ToList();
        Assert.Equal(3, evidence.Count);
        Assert.Equal(new[] { "6", "6", "8" }, evidence.Select(a => a.FromMarker!.RowId!.Text));
        // first picture: 1920 x 1080 -> 420 x 236.25 px, 4 px inset; the second sits under it after another 4 px
        Assert.Equal(420L * 9525, evidence[0].Extent!.Cx!.Value);
        Assert.Equal("38100", evidence[0].FromMarker!.RowOffset!.Text);
        Assert.Equal(38100 + evidence[0].Extent!.Cy!.Value + 38100, long.Parse(evidence[1].FromMarker!.RowOffset!.Text));
        Assert.Equal(420L * 9525, evidence[1].Extent!.Cx!.Value);
        Assert.Equal(210L * 9525, evidence[1].Extent!.Cy!.Value);
        Assert.Equal("38100", evidence[1].FromMarker!.ColumnOffset!.Text);
        // the row is as high as both pictures with their insets; Retrace's own picture stays in column F
        long stack = 38100 + evidence[0].Extent!.Cy!.Value + 38100 + evidence[1].Extent!.Cy!.Value + 38100;
        Assert.True(RowHeight(sheet, 7) >= stack / 12700.0 - 0.01);
        Assert.True(RowHeight(sheet, 7) <= 409);
        Assert.Single(anchors, a => a.FromMarker!.ColumnId!.Text == "5");
        Assert.Equal(4, sheet.DrawingsPart.ImageParts.Count());
        // results, notes, tester, date, counts (cached) and the formulas stay
        Assert.Equal("Pass", CellText(sheet, "G7"));
        Assert.Equal("Not run", CellText(sheet, "G8"));
        Assert.Equal("Fail", CellText(sheet, "G9"));
        Assert.Equal("looks right", CellText(sheet, "H7"));
        Assert.Equal("A. Tester", CellText(sheet, "F1"));
        Assert.Equal("2026-11-17", CellText(sheet, "F2"));
        Assert.Equal("Fail", sheet.Worksheet!.Descendants<S.Cell>().Single(c => c.CellReference!.Value == "F3").CellValue!.Text);
        Assert.Contains("Pass: 1   Fail: 1   Blocked: 0   Not run: 1", CellText(sheet, "C4"));
        Assert.NotNull(sheet.Worksheet!.Descendants<S.Cell>().Single(c => c.CellReference!.Value == "F3").CellFormula);
        Assert.Equal(ExportXlsx.EvidenceFilledHint, CellText(sheet, "I3"));
        Assert.Empty(sheet.HyperlinkRelationships);
    }

    [Fact]
    public void A_stack_too_tall_for_one_row_shrinks_and_then_continues_in_the_next_column()
    {
        // three 16:10 pictures: 3 x 262 px do not fit 409 pt (545 px), so all shrink together; ten do not fit at half size, so the rest go to Evidence 2
        var three = EvidenceLayout.Stack(Enumerable.Repeat((1920, 1200), 3).ToList());
        Assert.Single(three.GroupHeights);
        Assert.All(three.Pictures, p => Assert.True(p.Cx < 420L * 9525));
        Assert.True(three.GroupHeights[0] <= 409.0 + 0.01);
        var ten = EvidenceLayout.Stack(Enumerable.Repeat((1920, 1200), 10).ToList());
        Assert.True(ten.GroupHeights.Count >= 2);
        Assert.All(ten.GroupHeights, h => Assert.True(h <= 409.0 + 0.01));
        Assert.All(ten.Pictures, p => Assert.True(p.Cx >= 0.5 * 420L * 9525 - 1));   // never below half size
        Assert.Equal(new[] { 0, 1 }, ten.Pictures.Select(p => p.Group).Distinct().Take(2));

        var doc = Doc();
        var ev = new EvidenceData();
        ev.Rows["2"] = new EvidenceRow(Enumerable.Range(0, 10).Select(i => Pic($"step-2_20261117-1000{i:00}-000.png", 1920, 1200)).ToList());
        var path = Path.Combine(_dir, "many.xlsx");
        ExportXlsx.Write(doc, _dir, path, null, ev);
        using var xl = SpreadsheetDocument.Open(path, false);
        var sheet = Steps(xl);
        Assert.Equal("Evidence 2", CellText(sheet, "J6"));
        Assert.Equal(10, sheet.DrawingsPart!.WorksheetDrawing!.Descendants<Xdr.OneCellAnchor>().Count(a => a.FromMarker!.RowId!.Text == "7" && int.Parse(a.FromMarker.ColumnId!.Text) >= 8));
        Assert.True(sheet.Worksheet!.Descendants<S.Column>().Count() >= 10);
        Assert.True(RowHeight(sheet, 8) <= 409);
    }

    [Fact]
    public void The_original_steps_xlsx_is_not_touched_when_the_evidence_workbook_is_written()
    {
        var doc = Doc();
        var original = Path.Combine(_dir, "steps.xlsx");
        ExportXlsx.Write(doc, _dir, original);
        var before = SHA256.HashData(File.ReadAllBytes(original));
        var stamp = File.GetLastWriteTimeUtc(original);
        var ev = new EvidenceData();
        ev.Rows["1"] = new EvidenceRow([Pic("step-1_20261117-100000-000.png")]);
        var target = EvidenceFiles.WorkbookPath(_dir, new DateTime(2026, 11, 17, 10, 15, 0));
        ExportXlsx.Write(doc, _dir, target, null, ev);
        Assert.Equal(Path.Combine(_dir, "steps-evidence_20261117-101500.xlsx"), target);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(original)));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(original));
        // a second run never overwrites the first
        var second = EvidenceFiles.WorkbookPath(_dir, new DateTime(2026, 11, 17, 10, 15, 0));
        Assert.NotEqual(target, second);
        // the plain sheet has no pictures in the Evidence column and no links
        using var xl = SpreadsheetDocument.Open(original, false);
        Assert.Equal(1, Steps(xl).DrawingsPart!.WorksheetDrawing!.Descendants<Xdr.OneCellAnchor>().Count());   // only Retrace's own picture
        Assert.Empty(Steps(xl).HyperlinkRelationships);
    }

    [Fact]
    public void A_missing_picture_file_is_skipped_and_info_lines_are_added()
    {
        var doc = Doc();
        var ev = new EvidenceData { Tester = "Automated run" };
        ev.InfoLines.Add(("Run", "Automated run on AutoCAD 25.1"));
        ev.Rows["1"] = new EvidenceRow([Path.Combine(_dir, "evidence", "gone.png")], "Pass");
        var path = Path.Combine(_dir, "m.xlsx");
        ExportXlsx.Write(doc, _dir, path, null, ev);
        using var xl = SpreadsheetDocument.Open(path, false);
        Assert.Single(Steps(xl).DrawingsPart!.WorksheetDrawing!.Descendants<Xdr.OneCellAnchor>());   // only Retrace's picture
        var info = (WorksheetPart)xl.WorkbookPart!.GetPartById(xl.WorkbookPart.Workbook!.Sheets!.Elements<S.Sheet>().Last().Id!);
        Assert.Contains("Automated run on AutoCAD 25.1", info.Worksheet!.InnerText);
        Assert.Equal("Pass", CellText(Steps(xl), "G7"));
    }

    [Fact]
    public void Evidence_files_are_named_by_row_and_time_listed_oldest_first_and_removed_not_deleted()
    {
        Assert.Equal("step-3.2_20261117-101500-042.png", EvidenceFiles.FileName("3.2", new DateTime(2026, 11, 17, 10, 15, 0, 42)));
        var a = Pic("step-3_20261117-101500-000.png"); var b = Pic("step-3_20261117-101600-000.png"); Pic("step-12_20261117-101600-000.png"); Pic("notes.png");
        Pic("auto_20261117-090000/step-3.png".Replace("/", Path.DirectorySeparatorChar.ToString()));
        var list = EvidenceFiles.List(_dir);
        Assert.Equal(new[] { "12", "3" }, list.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(new[] { a, b }, list["3"]);
        Assert.Equal("3", EvidenceFiles.RowOf(a));
        Assert.Null(EvidenceFiles.RowOf("notes.png"));
        var moved = EvidenceFiles.Remove(a);
        Assert.False(File.Exists(a)); Assert.True(File.Exists(moved));
        Assert.Equal(new[] { b }, EvidenceFiles.List(_dir)["3"]);
    }

    [Fact]
    public void A_replay_step_belongs_to_the_sheet_step_that_began_before_it_and_failures_carry_their_message()
    {
        var numbered = new long[] { 100, 900, 2000 };
        Assert.Equal(1, EvidenceBook.StepOf(50, numbered));    // before the first: the first
        Assert.Equal(2, EvidenceBook.StepOf(900, numbered));
        Assert.Equal(2, EvidenceBook.StepOf(1500, numbered));  // a dialog the second step opened
        Assert.Equal(0, EvidenceBook.StepOf(5, []));
        var a = Pic("auto/step-01.png"); var b = Pic("auto/step-02.png"); Pic("auto/step-03.png");
        var steps = new[]
        {
            new ReplayStepInfo(0, 100, "ok", null, a),
            new ReplayStepInfo(1, 900, "ok", null, b),
            new ReplayStepInfo(2, 1500, "failed", "the dialog did not open", null),   // a dialog of step 2: no picture of its own, step 2 keeps the one before
            new ReplayStepInfo(3, 2000, "", null, null),                               // never run
        };
        var o = EvidenceBook.Outcomes(numbered, steps);
        Assert.Equal("Pass", o[1].Result); Assert.Equal(a, o[1].Picture);
        Assert.Equal("Fail", o[2].Result); Assert.Equal(b, o[2].Picture);
        Assert.Contains("the dialog did not open", o[2].Notes);
        Assert.Equal("Not run", o[3].Result);
        // into the workbook: every row of a step gets its result, the picture and note sit on the step's own row
        var doc = new StepsDoc("# t\n\n## Steps\n\n1. Click **A**\n\n2. Click **B**\n\n   - Pick a point\n\n3. Click **C**\n", _dir);
        var ev = EvidenceBook.FromOutcomes(doc, o, EvidenceBook.AutomatedInfo(new DateTime(2026, 11, 17, 9, 30, 0), "25.1", "FAIL", 3, 4), "Automated run", "2026-11-17");
        Assert.Equal("Fail", ev.Rows["2.1"].Result);
        Assert.Empty(ev.Rows["2.1"].Pictures);
        Assert.Null(ev.Rows["2.1"].Notes);
        Assert.Equal(new[] { b }, ev.Rows["2"].Pictures);
        Assert.Contains("did not open", ev.Rows["2"].Notes);
        var line = string.Join(" ", ev.InfoLines.Select(l => l.Value));
        Assert.Contains("Automated run on AutoCAD 25.1, 2026-11-17 09:30", line);
        Assert.DoesNotContain(Environment.MachineName, line, StringComparison.OrdinalIgnoreCase);
        var path = EvidenceBook.Write(doc, _dir, ev, new DateTime(2026, 11, 17, 9, 31, 0));
        using var xl = SpreadsheetDocument.Open(path, false);
        var sheet = Steps(xl);
        Assert.Equal("Fail", sheet.Worksheet!.Descendants<S.Cell>().Single(x => x.CellReference!.Value == "F3").CellValue!.Text);
        Assert.Contains("Pass: 1   Fail: 2   Blocked: 0   Not run: 1", CellText(sheet, "C4"));
        Assert.Contains("did not open", CellText(sheet, "H8"));
    }

    [Fact]
    public void A_testers_walk_fills_pictures_and_only_the_results_they_set()
    {
        var doc = new StepsDoc("# t\n\n## Steps\n\n1. Click **A**\n\n2. Click **B**\n", _dir);
        Pic("step-1_20261117-100000-000.png"); Pic("step-1_20261117-100100-000.png");
        var ev = EvidenceBook.FromTester(doc, _dir, new Dictionary<string, ManualRow> { ["2"] = new("Not run", ""), ["1"] = new("Pass", "ok") }, "T", "2026-11-17");
        Assert.Equal(2, ev.Rows["1"].Pictures.Count);
        Assert.Equal("Pass", ev.Rows["1"].Result);
        Assert.False(ev.Rows.ContainsKey("2"));   // no picture, no result: the tester's to fill in Excel
        Assert.Equal("T", ev.Tester);
    }

    [Fact]
    public void A_shrunk_stack_has_equal_factors_insets_between_and_fits_the_row()
    {
        // two 1920 x 1080 pictures: 236.25 px each at full size, 4 + 236.25 + 4 + 236.25 + 4 = 484.5 px < 545 px: they fit unshrunk
        var two = EvidenceLayout.Stack([(1920, 1080), (1920, 1080)]);
        Assert.All(two.Pictures, p => Assert.Equal(420L * 9525, p.Cx));
        // two 1000 x 1000 pictures: 380 px high each (the sheet's cap), 4 + 380 + 4 + 380 + 4 = 772 px: they shrink by one factor to fit 409 pt (545.33 px)
        var tall = EvidenceLayout.Stack([(1000, 1000), (1000, 1000)]);
        Assert.Equal(tall.Pictures[0].Cx, tall.Pictures[1].Cx);
        Assert.Equal(tall.Pictures[0].Cy, tall.Pictures[1].Cy);
        Assert.True(tall.Pictures[0].Cx < 380L * 9525 && tall.Pictures[0].Cx > 0.5 * 380L * 9525);
        Assert.Equal(EvidenceLayout.Inset, tall.Pictures[0].Top);
        Assert.Equal(EvidenceLayout.Inset + tall.Pictures[0].Cy + EvidenceLayout.Inset, tall.Pictures[1].Top);   // 4 px between
        long bottom = tall.Pictures[1].Top + tall.Pictures[1].Cy + EvidenceLayout.Inset;
        Assert.True(bottom <= (long)(409 * 12700));
        Assert.True(bottom > (long)(409 * 12700) - 2 * 9525);   // and fills it (rounding apart)
        Assert.Equal(bottom / 12700.0, tall.GroupHeights[0], 3);
    }

    [Fact]
    public void An_evidence_workbook_is_valid_openxml()
    {
        var doc = Doc();
        var ev = new EvidenceData { Tester = "T", Date = "2026-11-17" };
        ev.Rows["1"] = new EvidenceRow(Enumerable.Range(0, 10).Select(i => Pic($"step-1_20261117-1000{i:00}-000.png", 1920, 1200)).ToList(), "Pass", "n");
        ev.Rows["3"] = new EvidenceRow([Pic("step-3_20261117-100200-000.png")], "Fail", "wrong");
        var path = Path.Combine(_dir, "valid.xlsx");
        ExportXlsx.Write(doc, _dir, path, null, ev);
        using var xl = SpreadsheetDocument.Open(path, false);
        var errors = new DocumentFormat.OpenXml.Validation.OpenXmlValidator(DocumentFormat.OpenXml.FileFormatVersions.Office2019).Validate(xl).Select(e => e.Description).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    // ---- the mapping of a finished replay to the sheet (EvidenceMapping) ------------------------------------------------

    static ReplayResult Result(string verdict, params (int Index, string Status, string? Message)[] steps)
    {
        var r = new ReplayResult { Verdict = verdict, Message = "msg" };
        foreach (var (index, status, message) in steps) r.Steps.Add(new StepResult { Index = index, Status = status, Message = message });
        return r;
    }

    static List<Step> Plan3() => [new Step { Kind = "ribbon", T = 100 }, new Step { Kind = "command", T = 900 }, new Step { Kind = "mark", T = 2000 }];
    static readonly long[] Numbered3 = [100, 900, 2000];

    static Dictionary<int, StepOutcome> Map(ReplayResult r, int endStep = -1, EvidenceEnd end = EvidenceEnd.Completed, List<Step>? steps = null, long[]? numbered = null) =>
        EvidenceMapping.Outcomes(steps ?? Plan3(), r, new string?[steps?.Count ?? 3], numbered ?? Numbered3, endStep, end);

    [Fact]
    public void A_pass_verdict_gives_pass_everywhere()
    {
        var o = Map(Result("PASS", (1, "ok", null), (2, "ok", null), (3, "ok", null)));
        Assert.All(o.Values, x => Assert.Equal("Pass", x.Result));
    }

    [Fact]
    public void A_failed_basic_check_makes_the_last_step_fail_with_the_differences()
    {
        var r = Result("FAIL", (1, "ok", null), (2, "ok", null), (3, "ok", null));
        r.BasicCheck = new BasicCheckResult { Pass = false, Items = [new BasicCheckItem { Item = "modelSpace", Expected = 12, Actual = 11, Pass = false }] };
        var o = Map(r);
        Assert.Equal("Pass", o[1].Result); Assert.Equal("Pass", o[2].Result);
        Assert.Equal("Fail", o[3].Result);
        Assert.Contains("Basic check", o[3].Notes);
    }

    [Fact]
    public void An_incomplete_run_never_reads_pass_and_an_unverified_checkpoint_is_not_run()
    {
        var r = Result("INCOMPLETE", (1, "ok", null), (2, "ok", null), (3, "ok", null));   // completed, nothing verified
        var o = Map(r);
        Assert.Equal("Pass", o[1].Result);
        Assert.Equal("Not run", o[3].Result);
        Assert.Contains("nothing verified", o[3].Notes);
        // an unverified checkpoint (no verdict) in a run that is not PASS
        var r2 = Result("INCOMPLETE", (1, "ok", null), (2, "ok", null), (3, "ok", null));
        r2.Checkpoints.Add(new CheckpointResult { Index = 1, Pass = null });
        var o2 = Map(r2);
        Assert.Equal("Not run", o2[3].Result);
        Assert.Contains("not verified", o2[3].Notes);
        // a checkpoint without a verdict in a PASS run (the basic check verified it) stays Pass
        var r3 = Result("PASS", (1, "ok", null), (2, "ok", null), (3, "ok", null));
        r3.Checkpoints.Add(new CheckpointResult { Index = 1, Pass = null });
        Assert.All(Map(r3).Values, x => Assert.Equal("Pass", x.Result));
    }

    [Fact]
    public void Blocked_and_timed_out_steps_and_the_end_of_an_early_run_are_named()
    {
        var o = Map(Result("INCOMPLETE", (1, "ok", null), (2, "blocked", "a security prompt")), endStep: 1, end: EvidenceEnd.Person);
        Assert.Equal("Blocked", o[2].Result); Assert.Contains("security prompt", o[2].Notes);
        Assert.Equal("Not run", o[3].Result);
        var t = Map(Result("ERROR", (1, "ok", null), (2, "timed-out", "step 2 timed out")), endStep: 1, end: EvidenceEnd.Error);
        Assert.Equal("Fail", t[2].Result); Assert.Contains("timed out", t[2].Notes);
        // an error with no result line of its own: the step the replay was running takes it, not an earlier failed dialog
        var e = Map(Result("ERROR", (1, "failed", "dialog did not fill"), (2, "ok", null)), endStep: 2, end: EvidenceEnd.Error);
        Assert.Equal("Fail", e[1].Result);
        Assert.Equal("Fail", e[3].Result);
        Assert.Contains("msg", e[3].Notes);
        // a Stop pressed by a person: the step it stopped on is not run, not Pass
        var st = Map(Result("INCOMPLETE", (1, "ok", null), (2, "ok", null)), endStep: 1, end: EvidenceEnd.Stopped);
        Assert.Equal("Not run", st[2].Result); Assert.Contains("Stopped", st[2].Notes);
    }

    [Fact]
    public void A_step_that_waited_for_a_person_says_so_and_the_info_line_does_not_claim_nobody_clicked()
    {
        var r = Result("PASS", (1, "ok", null), (2, "ok", null), (3, "ok", null));
        r.Steps[1].WaitedForPerson = true;
        var o = Map(r);
        Assert.Equal("Pass", o[2].Result);
        Assert.Contains("A person finished a dialog", o[2].Notes);
        var info = EvidenceBook.AutomatedInfo(new DateTime(2026, 11, 17), "25.1", "PASS", 3, 3, anyWaited: true);
        Assert.DoesNotContain("Nobody clicked", info[0].Value);
        Assert.Contains("Nobody clicked", EvidenceBook.AutomatedInfo(new DateTime(2026, 11, 17), "25.1", "PASS", 3, 3)[0].Value);
    }

    [Fact]
    public void A_paths_user_name_is_not_written_into_the_workbook()
    {
        Assert.Equal("could not open %USERPROFILE%\\AppData\\x.dwg", EvidenceMapping.Scrub(@"could not open C:\Users\someone\AppData\x.dwg"));
        Assert.Equal(403, EvidenceMapping.Scrub(new string('x', 900)).Length);
    }

    [Fact]
    public void A_recording_with_a_drawing_switch_a_script_chain_and_a_checkpoint_maps_every_numbered_step_and_a_pass_replay_is_pass()
    {
        var header = """{"type":"header","schema":3,"name":"map","started":"2026-11-16T10:00:00","autocad":"25.1.0.0","product":"AutoCAD 2026"}""";
        var lines = new[]
        {
            header,
            """{"t":100,"n":1,"type":"ribbon","id":"ID_Line","text":"Line","macro":"^C^C_line","tab":"Home","panel":"Draw"}""",
            """{"t":110,"n":2,"type":"cmd_start","command":"LINE","origin":"ribbon"}""",
            """{"t":300,"n":3,"type":"prompt","kind":"point","message":"Specify first point:","status":"OK","value":[0,0,0],"input":true,"by":"mouse"}""",
            """{"t":400,"n":4,"type":"cmd_end","command":"LINE"}""",
            """{"t":1000,"n":5,"type":"doc_created","doc":"B.DWG","path":"C:\\a\\B.DWG"}""",
            """{"t":2000,"n":6,"type":"cmd_start","command":"DRAWGRID","origin":"typed/other"}""",
            """{"t":2010,"n":7,"type":"modal_enter","title":""}""",
            """{"t":2300,"n":8,"type":"modal_title","title":"Draw grid"}""",
            """{"t":3000,"n":9,"type":"modal_leave"}""",
            """{"t":3010,"n":10,"type":"cmd_end","command":"DRAWGRID"}""",
            """{"t":4000,"n":11,"type":"mark","note":"drawn","state":{"count":1,"byType":{"LINE":1},"layers":["0"],"hash":"ab"}}""",
            """{"t":5000,"n":12,"type":"session_end"}""",
        };
        var path = Path.Combine(_dir, "map", "recording.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
        var session = RecordingReader.Load(path);
        RecordingAnalysis.Run([session], null);
        var numberedT = session.Steps.Where(ScriptWriter.IsNumbered).Select(s => s.T).ToList();
        var steps = Plan.Build(Plan.ReadEvents(path)).Where(s => s.Kind != "tab").ToList();
        Assert.True(numberedT.Count >= 3, string.Join(",", numberedT));
        var r = new ReplayResult { Verdict = "PASS" };
        for (int i = 0; i < steps.Count; i++) r.Steps.Add(new StepResult { Index = i + 1, Kind = steps[i].Kind, Status = "ok" });
        r.Checkpoints.Add(new CheckpointResult { Index = 1, Pass = true });
        var o = EvidenceMapping.Outcomes(steps, r, new string?[steps.Count], numberedT, -1, EvidenceEnd.Completed);
        Assert.Equal(numberedT.Count, o.Count);   // every numbered step has an outcome, the drawing switch too
        Assert.All(o.Values, x => Assert.Equal("Pass", x.Result));
        // the workbook of it reads Overall Pass
        var doc = StepsDoc.Load(ScriptWriter.WriteFor(path, null, [], out _));
        var ev = EvidenceBook.FromOutcomes(doc, o, [], "t", "d");
        var xlsx = EvidenceBook.Write(doc, Path.GetDirectoryName(path)!, ev, new DateTime(2026, 11, 17, 9, 0, 0));
        using var xl = SpreadsheetDocument.Open(xlsx, false);
        Assert.Equal("Pass", Steps(xl).Worksheet!.Descendants<S.Cell>().Single(x => x.CellReference!.Value == "F3").CellValue!.Text);
    }

    // ---- smaller review items -----------------------------------------------------------------------------------------

    [Fact]
    public void Column_letters_go_past_Z_and_a_cell_text_is_made_safe()
    {
        Assert.Equal("A", ExportXlsx.ColName(0)); Assert.Equal("Z", ExportXlsx.ColName(25)); Assert.Equal("AA", ExportXlsx.ColName(26)); Assert.Equal("AB", ExportXlsx.ColName(27)); Assert.Equal("BA", ExportXlsx.ColName(52));
        Assert.Equal("a b\nc", ExportXlsx.CleanText("a\u0001b\nc"));
        Assert.Equal(ExportXlsx.MaxCellChars + 3, ExportXlsx.CleanText(new string('x', 40000)).Length);
        // a workbook with notes holding control characters, a very long note and 70 pictures in one row is valid and opens
        var doc = Doc();
        var ev = new EvidenceData();
        ev.Rows["1"] = new EvidenceRow(Enumerable.Range(0, 70).Select(i => Pic($"step-1_20261117-1000{i:00}-000.png", 400, 300)).ToList(), "Pass", "bad\u0001\u0002 chars " + new string('n', 40000));
        var path = Path.Combine(_dir, "wide.xlsx");
        ExportXlsx.Write(doc, _dir, path, null, ev);
        using var xl = SpreadsheetDocument.Open(path, false);
        var errors = new DocumentFormat.OpenXml.Validation.OpenXmlValidator(DocumentFormat.OpenXml.FileFormatVersions.Office2019).Validate(xl).Select(e => e.Description).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));
        var sheet = Steps(xl);
        Assert.True(sheet.Worksheet!.Descendants<S.Cell>().Any(c => c.CellReference!.Value.StartsWith("AA")));   // columns past Z are named AA... not punctuation
        Assert.True(RowHeight(sheet, 7) <= 409);
    }

    [Fact]
    public void A_name_that_exists_is_moved_on_by_a_millisecond_and_stays_listed()
    {
        var now = new DateTime(2026, 11, 17, 10, 15, 0, 42);
        var first = EvidenceFiles.NewPath(_dir, "3", now);
        Directory.CreateDirectory(Path.GetDirectoryName(first)!); File.WriteAllBytes(first, PngOf(10, 10));
        var second = EvidenceFiles.NewPath(_dir, "3", now);
        Assert.NotEqual(first, second);
        File.WriteAllBytes(second, PngOf(10, 10));
        Assert.Equal(new[] { first, second }, EvidenceFiles.List(_dir)["3"]);
    }

    [Fact]
    public void A_changed_steps_file_is_noticed_against_the_pictures_taken_with_the_first_one()
    {
        Assert.True(EvidenceFiles.StepsUnchanged(_dir, "one"));          // no pictures yet: nothing to compare
        EvidenceFiles.StampSteps(_dir, "one");
        Pic("step-1_20261117-100000-000.png");
        Assert.True(EvidenceFiles.StepsUnchanged(_dir, "one"));
        Assert.False(EvidenceFiles.StepsUnchanged(_dir, "two"));
        Assert.Empty(EvidenceFiles.List(_dir).Keys.Where(k => k.StartsWith(".")));   // the stamp is not a picture
    }

    [Fact]
    public void Notes_make_the_row_taller_and_more_than_sixty_pictures_are_left_out_with_a_note()
    {
        var row = new SheetRow("1", "a", "", "", "", [], false);
        Assert.True(ExportXlsx.TextHeight(row, string.Join("\n", Enumerable.Repeat("note", 10))) >= 10 * 15 + 6);
        var doc = new StepsDoc("# t\n\n## Steps\n\n1. Click **A**\n", _dir);
        for (int i = 0; i < 65; i++) Pic($"step-1_20261117-1000{i:00}-000.png", 100, 100);
        var ev = EvidenceBook.FromTester(doc, _dir, new Dictionary<string, ManualRow>(), "t", "d");
        Assert.Equal(EvidenceFiles.MaxPicturesPerRow, ev.Rows["1"].Pictures.Count);
        Assert.Contains("5 more picture(s)", ev.Rows["1"].Notes);
    }

    // ---- a plain export is what it was (apart from the links, the row height, the I3 text and the Picture cell) ------------


    [Fact]
    public void A_plain_export_keeps_its_structure()
    {
        // Compared with 0.6.16's output for this document (diffed by hand when the evidence work was made): only the Picture cell (the file name and its link are gone), the row heights (16 pt less: 22 pt for the file-name
        // line became 6 pt of inset), the I3 text, the hyperlinks and the style numbering (no link style) differ. The hashes are of the parts as they are now, style numbers and relationship ids left out: a change shows here.
        byte[] Png(int w, int h) { var b = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }; foreach (var v in new[] { w, h }) b.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]); b.AddRange([8, 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, (byte)'I', (byte)'E', (byte)'N', (byte)'D', 0, 0, 0, 0]); return b.ToArray(); }
        Directory.CreateDirectory(Path.Combine(_dir, "screenshots"));
        File.WriteAllBytes(Path.Combine(_dir, "screenshots", "a.png"), Png(840, 300));
        File.WriteAllBytes(Path.Combine(_dir, "screenshots", "b.png"), Png(600, 300));
        var md = "# Golden test\n\nCreated 2026-10-10, with AutoCAD 2026 25.1.0.0.\n\n**Start with:** a new, empty drawing.\n\n## Steps\n\n1. Click **Go**\n\n   *Ribbon › Home tab*\n\n   ![a](screenshots/a.png)\n\n2. Fill in the **Opts** dialog\n\n   - Set **Rows** to `5`\n   - Click **OK**\n\n   ![b](screenshots/b.png)\n\n3. Type `LINE` and press Enter\n\n   - Pick the point\n";
        var path = Path.Combine(_dir, "golden.xlsx");
        ExportXlsx.Write(new StepsDoc(md, _dir), _dir, path);
        using var xl = SpreadsheetDocument.Open(path, false);
        string Hash(string text) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).Substring(0, 16);
        var sheet = Steps(xl);
        // what the sheet holds, read with the SDK (the same on every target: the XML text differs between OpenXml versions): rows, heights, cells with style, text and formula, merges, columns, data validation
        var proj = new System.Text.StringBuilder();
        foreach (var row in sheet.Worksheet!.Descendants<S.Row>())
        {
            proj.Append($"row {row.RowIndex!.Value} ht={(row.Height?.Value ?? 0).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}\n");
            foreach (var c in row.Elements<S.Cell>()) proj.Append($"  {c.CellReference!.Value} s={c.StyleIndex?.Value} {c.InnerText.Replace(_dir, "<folder>")}\n");
        }
        foreach (var m in sheet.Worksheet.Descendants<S.MergeCell>()) proj.Append($"merge {m.Reference!.Value}\n");
        foreach (var c in sheet.Worksheet.Descendants<S.Column>()) proj.Append($"col {c.Min!.Value} {c.Width!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n");
        foreach (var v in sheet.Worksheet.Descendants<S.DataValidation>()) proj.Append($"valid {v.SequenceOfReferences!.InnerText} {v.Formula1!.Text}\n");
        foreach (var a in sheet.DrawingsPart!.WorksheetDrawing!.Descendants<DocumentFormat.OpenXml.Drawing.Spreadsheet.OneCellAnchor>())
            proj.Append($"pic {a.FromMarker!.ColumnId!.Text},{a.FromMarker.ColumnOffset!.Text} {a.FromMarker.RowId!.Text},{a.FromMarker.RowOffset!.Text} {a.Extent!.Cx!.Value}x{a.Extent.Cy!.Value}\n");
        var info = (WorksheetPart)xl.WorkbookPart!.GetPartById(xl.WorkbookPart.Workbook!.Sheets!.Elements<S.Sheet>().Last().Id!);
        proj.Append("info " + info.Worksheet!.InnerText.Replace(Path.GetFileName(_dir), "<folder>"));
        Assert.Equal("62996FB060A6C403", Hash(proj.ToString()));
        Assert.Empty(sheet.HyperlinkRelationships);
        Assert.Empty(sheet.Worksheet.Descendants<S.Hyperlink>());
    }

    [Fact]
    public void A_user_profile_path_is_written_as_USERPROFILE_in_notes_checkpoint_differences_and_the_evidence_info_folder()
    {
        Assert.Equal("a %USERPROFILE%\\AppData\\x.dwg b", EvidenceBook.ScrubPaths(@"a C:\Users\someone\AppData\x.dwg b"));
        Assert.Equal("%USERPROFILE%/AppData/x.dwg", EvidenceBook.ScrubPaths("d:/" + "Users/someone/AppData/x.dwg"));
        Assert.Equal(@"D:\Tests\Users\x", EvidenceBook.ScrubPaths(@"D:\Tests\Users\x"));       // not under a profile: as it is
        Assert.Equal(@"\\server\share\tests", EvidenceBook.ScrubPaths(@"\\server\share\tests"));   // a share: as it is
        // a step's message, a checkpoint's differences and the basic check line
        var r = Result("FAIL", (1, "failed", @"could not open C:\Users\someone\a.dwg"), (2, "ok", null), (3, "ok", null));
        r.Checkpoints.Add(new CheckpointResult { Index = 1, Pass = false, Differences = ["layer in C:/" + "Users/someone/x differs"] });
        var o = Map(r, steps: [new Step { Kind = "ribbon", T = 100 }, new Step { Kind = "command", T = 900 }, new Step { Kind = "mark", T = 2000 }]);
        Assert.DoesNotContain("someone", o[1].Notes);
        Assert.Contains("%USERPROFILE%", o[1].Notes);
        Assert.DoesNotContain("someone", o[3].Notes);
        // the Info sheet's Test folder: scrubbed in an evidence workbook, as it was in the plain one
        var doc = Doc();
        var folder = @"C:\Users\someone\tests\t1";
        var ev = new EvidenceData();
        var evPath = Path.Combine(_dir, "scrub.xlsx");
        ExportXlsx.Write(doc, folder, evPath, null, ev);
        var plainPath = Path.Combine(_dir, "plain.xlsx");
        ExportXlsx.Write(doc, folder, plainPath);
        string InfoText(string path) { using var xl = SpreadsheetDocument.Open(path, false); var info = (WorksheetPart)xl.WorkbookPart!.GetPartById(xl.WorkbookPart.Workbook!.Sheets!.Elements<S.Sheet>().Last().Id!); return info.Worksheet!.InnerText; }
        // only the folder's name is printed, in both: no path, so no user name and no root folder
        Assert.Contains("t1", InfoText(evPath));
        Assert.DoesNotContain("someone", InfoText(evPath));
        Assert.DoesNotContain("someone", InfoText(plainPath));
        Assert.DoesNotContain("Users", InfoText(plainPath));
    }

    [Fact]
    public void A_cut_at_the_cap_never_splits_a_surrogate_pair_and_a_lone_half_is_replaced()
    {
        var emoji = "\U0001F600";   // two UTF-16 units
        var text = new string('x', ExportXlsx.MaxCellChars - 1) + emoji + "tail";
        var cut = ExportXlsx.CleanText(text);
        Assert.EndsWith("...", cut);
        Assert.DoesNotContain(cut, c => char.IsSurrogate(c));
        Assert.Equal(ExportXlsx.MaxCellChars - 1 + 3, cut.Length);
        Assert.Equal("a b", ExportXlsx.CleanText("a\uD83Db"));   // a lone high half
        Assert.Equal("a" + emoji + "b", ExportXlsx.CleanText("a" + emoji + "b"));
    }

    [Fact]
    public void The_steps_stamp_starts_again_when_the_pictures_are_all_gone_and_a_read_never_writes_it()
    {
        Pic("step-1_20261117-100000-000.png");
        EvidenceFiles.StampSteps(_dir, "one");
        Assert.True(EvidenceFiles.StepsUnchanged(_dir, "one"));
        Assert.False(EvidenceFiles.StepsUnchanged(_dir, "two"));
        foreach (var f in EvidenceFiles.List(_dir).Values.SelectMany(x => x).ToList()) EvidenceFiles.Remove(f);   // all pictures removed
        Assert.True(EvidenceFiles.StepsUnchanged(_dir, "two"));   // nothing left to be on the wrong step
        var stamp = Path.Combine(EvidenceFiles.FolderOf(_dir), ".steps-sha");
        var before = File.ReadAllText(stamp);
        EvidenceFiles.StepsUnchanged(_dir, "three");
        Assert.Equal(before, File.ReadAllText(stamp));             // a read does not write
        Pic("step-1_20261117-100100-000.png");                      // the first picture of an empty folder: the stamp is renewed
        EvidenceFiles.StampSteps(_dir, "two");
        Assert.True(EvidenceFiles.StepsUnchanged(_dir, "two"));
        Assert.False(EvidenceFiles.StepsUnchanged(_dir, "one"));
    }
}
