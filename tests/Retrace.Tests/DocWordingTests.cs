using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;
using Retrace.Engine.Emit;
using Retrace.Engine.Recordings;

namespace Retrace.Tests;

/// <summary>The words of the test documents: the short "Start with" sentence, where a step happens (ribbon, command line, palette), the change list of a dialog, the split into
/// an action line and a smaller location line (steps.md, Word, Excel, the training document). Synthetic recordings only.</summary>
public class DocWordingTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("retrace-test-wording").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    const string Header = """{"type":"header","schema":3,"name":"wording","started":"2026-11-16T10:00:00","autocad":"25.1.0.0","product":"AutoCAD 2026"}""";

    string Md(params string[] lines) => MdWith(Header, lines);

    string MdWith(string header, params string[] lines)
    {
        var p = Path.Combine(_dir, Guid.NewGuid().ToString("N"), "recording.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllLines(p, new[] { header }.Concat(lines));
        return ScriptWriter.Build(RecordingReader.Load(p), null, _dir, new List<string>());
    }

    static string StartLine(string md) => md.Split('\n').First(l => l.StartsWith("**Start with:**"));

    // ---- Start with -----------------------------------------------------------------------------------------------------

    [Fact]
    public void An_empty_new_drawing_is_one_short_sentence_with_its_template()
    {
        var md = MdWith(Header.Replace("\"schema\":3", "\"schema\":3,\"template\":\"C:\\\\Templates\\\\acad.dwt\""),
            """{"t":1,"n":1,"type":"state","doc":"Drawing1.dwg","state":{"count":0,"byType":{},"byLayer":{},"layers":["0"],"hash":"e3"}}""");
        Assert.Equal("**Start with:** a new, empty drawing (template acad.dwt).", StartLine(md));
    }

    [Fact]
    public void A_named_drawing_says_its_name_and_counts_and_never_lists_layers_or_every_object_type()
    {
        var md = Md(
            """{"t":1,"n":1,"type":"state","doc":"Plan.dwg","state":{"count":42,"byType":{"LINE":20,"INSERT":15,"CIRCLE":4,"TEXT":2,"ARC":1},"byLayer":{"0":10,"WALLS":20,"DOORS":6,"DIM":4,"TEXT":2},"layers":["0","WALLS","DOORS","DIM","TEXT","DEFPOINTS"],"hash":"ab"}}""");
        Assert.Equal("**Start with:** the drawing Plan.dwg, 42 objects (mostly lines and block references). The drawing has 5 layers. Open or build it before the first step.", StartLine(md));
        Assert.DoesNotContain("WALLS", md);
        Assert.DoesNotContain("layers: ", md);
        Assert.DoesNotContain("LINE", StartLine(md));
    }

    [Fact]
    public void The_mix_names_at_most_two_kinds_in_plain_words()
    {
        RecordingState S(string json) => RecordingState.From(System.Text.Json.JsonDocument.Parse(json).RootElement)!;
        Assert.Equal("all lines", StartWording.Mix(S("""{"count":3,"byType":{"LINE":3}}""")));
        Assert.Equal("lines and circles", StartWording.Mix(S("""{"count":3,"byType":{"LINE":2,"CIRCLE":1}}""")));
        Assert.Equal("mostly lines", StartWording.Mix(S("""{"count":10,"byType":{"LINE":8,"CIRCLE":1,"ARC":1}}""")));
        Assert.Equal("mostly lines and circles", StartWording.Mix(S("""{"count":10,"byType":{"LINE":4,"CIRCLE":3,"ARC":2,"TEXT":1}}""")));
        Assert.Equal("", StartWording.Mix(S("""{"count":0,"byType":{}}""")));
    }

    [Fact]
    public void The_settings_line_stays_in_steps_md_only()
    {
        var md = MdWith(Header.Replace("\"schema\":3", "\"schema\":3,\"settings\":{\"screenshotMode\":\"classic\"}"));
        Assert.Contains("*Settings (not default): screenshotMode classic*", md);
    }

    // ---- where a step happens -------------------------------------------------------------------------------------------

    [Fact]
    public void A_ribbon_click_is_a_short_action_and_a_location_line()
    {
        var md = Md("""{"t":10,"n":1,"type":"ribbon","id":"ID_Line","text":"Line","macro":"^C^C_line","tab":"Home","panel":"Draw"}""");
        Assert.Contains("1. Click **Line**\n\n   *Ribbon › Home tab › Draw panel*\n", md);
        Assert.DoesNotContain("Home › Draw › Line", md);
        var noPanel = Md("""{"t":10,"n":1,"type":"ribbon","id":"ID_Line","text":"Line","macro":"^C^C_line","tab":"Home"}""");
        Assert.Contains("1. Click **Line**\n\n   *Ribbon › Home tab*\n", noPanel);
        var neither = Md("""{"t":10,"n":1,"type":"ribbon","id":"ID_Line","text":"Line","macro":"^C^C_line"}""");
        Assert.Contains("1. Click **Line**\n\n   *Ribbon*\n", neither);
    }

    [Fact]
    public void A_typed_command_says_it_is_typed_at_the_command_line()
    {
        var md = Md("""{"t":10,"n":1,"type":"cmd_start","command":"CIRCLE","origin":"typed/other"}""", """{"t":20,"n":2,"type":"cmd_end","command":"CIRCLE"}""");
        Assert.Contains("1. Type `CIRCLE` and press Enter\n\n   *Command line*\n", md);
    }

    // ---- the change list of a dialog -------------------------------------------------------------------------------------

    const string Dialog = """
        {"t":100,"n":1,"type":"cmd_start","command":"DRAWCABINET","origin":"typed/other"}
        {"t":110,"n":2,"type":"modal_enter","title":"AutoCAD"}
        {"t":200,"n":3,"type":"modal_title","title":"Cabinet"}
        {"t":1800,"n":4,"type":"modal_leave"}
        {"t":1810,"n":5,"type":"dialog_values","for":3,"title":"Cabinet","source":"win32","fields":[{"key":"id:1","label":"Width","kind":"edit","before":"24","after":"36"},{"key":"id:2","label":"Soft close","kind":"check","before":"off","after":"on"},{"key":"id:3","label":"Finish","kind":"combo","before":"Pine","after":"Oak"},{"key":"id:4","label":"#1012","kind":"tree","before":"","after":"Cabinets › Base › B12"},{"key":"id:5","label":"","kind":"edit","before":"","after":"7"}],"read":9,"closedWith":"ok"}
        {"t":1900,"n":6,"type":"cmd_end","command":"DRAWCABINET"}
        """;

    [Fact]
    public void A_dialog_lists_what_to_change_one_short_line_each_and_ends_with_the_closing_click()
    {
        var md = Md(Dialog.Split('\n'));
        Assert.Contains("   - Fill in the **Cabinet** dialog\n" +
                        "     - Set **Width** to `36`\n" +
                        "     - Tick **Soft close**\n" +
                        "     - Choose `Oak` in **Finish**\n" +
                        "     - Select `Cabinets › Base › B12` in the tree\n" +
                        "     - Set the field to `7`\n" +
                        "     - Click **OK**\n", md);
        Assert.DoesNotContain("(was", md);
        Assert.DoesNotContain("#1012", md);
        Assert.DoesNotContain("A dialog opens", md);
    }

    [Fact]
    public void The_guide_keeps_its_wording_with_the_old_values()
    {
        var dlg = new RecordingDialogValue("Width", "edit", "24", "36");
        Assert.Equal("Set **Width** to `36` (was `24`)", StepWording.ChangeText(dlg, Md_Cell, Md_Code, bold: true));
        Assert.Equal("Set **Width** to `36`", StepWording.ChangeText(dlg, Md_Cell, Md_Code, bold: true, showWas: false));
    }

    static string Md_Cell(string? s) => s ?? "";
    static string Md_Code(string? s) => $"`{s}`";

    [Fact]
    public void A_dialog_cancelled_without_a_click_is_one_line_and_one_with_clicks_ends_with_Cancel()
    {
        var cancel = Md("""{"t":100,"n":1,"type":"modal_enter","title":"Options"}""", """{"t":1800,"n":2,"type":"modal_leave"}""",
            """{"t":1810,"n":3,"type":"dialog_values","for":1,"fields":[],"read":2,"closedWith":"cancel"}""");
        Assert.Contains("1. Cancel the **Options** dialog\n", cancel);
        Assert.DoesNotContain("Click **OK**", cancel);
        Assert.DoesNotContain("Click **Cancel**", cancel);   // nothing was done in it, so the one line says it all
        var withClicks = Md("""{"t":100,"n":1,"type":"modal_enter","title":"Options"}""", """{"t":200,"n":2,"type":"dialog_click","for":1,"button":"Apply","fields":[],"read":2}""",
            """{"t":1800,"n":3,"type":"modal_leave"}""", """{"t":1810,"n":4,"type":"dialog_values","for":1,"fields":[],"read":2,"closedWith":"cancel"}""");
        Assert.Contains("1. Fill in the **Options** dialog\n", withClicks);
        Assert.Contains("   - Click **Apply**\n", withClicks);
        Assert.Contains("   - Click **Cancel**\n", withClicks);   // clicks were done, so the dialog is closed by a visible Cancel click
        Assert.DoesNotContain("Click **OK**", withClicks);
    }

    [Fact]
    public void The_replays_count_of_dialog_steps_follows_the_new_wording()
    {
        var md = Md(Dialog.Split('\n'));
        Assert.Equal(1, Retrace.Core.ScriptRef.DialogStepNumber(md, 1));
        Assert.Null(Retrace.Core.ScriptRef.DialogStepNumber(md, 2));
        Assert.Equal(3, Retrace.Core.ScriptRef.DialogStepNumber("1. Click **A**\n2. Click **B**\n3. Fill in the **Box** dialog\n", 1));
        Assert.Null(Retrace.Core.ScriptRef.DialogStepNumber("1. Fill in the **Box** palette\n2. Fill in the nested **Box** dialog\n", 1));   // a palette and a nested dialog are no dialog steps
    }

    // ---- Word ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_word_document_shows_the_location_line_small_and_grey_and_kept_with_the_step_and_has_no_settings_line()
    {
        var md = "# t\n\n*Settings (not default): screenshotMode classic*\n\n**Start with:** a new, empty drawing.\n\n## Steps\n\n1. Click **Line**\n\n   *Ribbon › Home tab › Draw panel*\n\n   - Pick the first point\n";
        var doc = new StepsDoc(md, _dir);
        var path = Path.Combine(_dir, "t.docx");
        ExportDocx.Write(doc, path);
        using var w = WordprocessingDocument.Open(path, false);
        var body = w.MainDocumentPart!.Document!.Body!;
        Assert.DoesNotContain("Settings", body.InnerText);
        var where = body.Elements<W.Paragraph>().Single(p => p.InnerText == "Ribbon › Home tab › Draw panel");
        Assert.NotNull(where.ParagraphProperties!.KeepNext);
        Assert.All(where.Elements<W.Run>(), r =>
        {
            Assert.Equal("6B6B6B", r.RunProperties!.Color!.Val!.Value);
            Assert.Equal("18", r.RunProperties.FontSize!.Val!.Value);
            Assert.NotNull(r.RunProperties.Italic);
            Assert.Null(r.RunProperties.Bold);
        });
        Assert.Contains("Click Line", body.InnerText);   // the main line stays normal size
        var main = body.Elements<W.Paragraph>().First(p => p.InnerText.Contains("Click Line"));
        Assert.Null(main.Elements<W.Run>().First().RunProperties?.FontSize);
    }

    // ---- training document ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_training_document_has_a_short_heading_and_the_location_line_under_it()
    {
        var step = new Retrace.Core.Step { Kind = "ribbon", Title = "Click Home > Draw > Line", Tab = "Home", Panel = "Draw", ItemId = "ID_Line" };
        Assert.Equal("Click Line", Retrace.Core.TdocPlan.StepTitle(step));
        Assert.Equal(new[] { "Click Line." }, Retrace.Core.TdocPlan.Instructions(step));
        Assert.Equal("Ribbon › Home tab › Draw panel", Retrace.Core.TdocPlan.Where(step));
        Assert.Equal("Command line", Retrace.Core.TdocPlan.Where(new Retrace.Core.Step { Kind = "command", Command = "LINE" }));

        var manifest = new TrainingManifest { Title = "T", Tests = [new TrainingTest { Name = "a", Steps = [new TrainingStep { Number = 1, Kind = "ribbon", Title = "Click Line", Where = "Ribbon › Home tab › Draw panel", Instructions = ["Click Line."] }] }] };
        var docx = Path.Combine(_dir, "training.docx");
        TrainingDocx.Write(manifest, _dir, docx);
        using var w = WordprocessingDocument.Open(docx, false);
        var paras = w.MainDocumentPart!.Document!.Body!.Elements<W.Paragraph>().ToList();
        int h = paras.FindIndex(p => p.InnerText == "Step 1: Click Line");
        Assert.True(h >= 0);
        Assert.Equal("Ribbon › Home tab › Draw panel", paras[h + 1].InnerText);
        Assert.Equal("18", paras[h + 1].Elements<W.Run>().Single().RunProperties!.FontSize!.Val!.Value);
        Assert.Equal("Click Line.", paras[h + 2].InnerText);
    }
}
