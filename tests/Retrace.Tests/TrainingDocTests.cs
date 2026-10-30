using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using W = DocumentFormat.OpenXml.Wordprocessing;
using Retrace.Engine.Recordings;

namespace Retrace.Tests;

/// <summary>The Tdoc manifest (tdoc.json) and the training document built from it. Synthetic tests, steps and pictures only.</summary>
public class TrainingDocTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("retrace-test-tdoc").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    /// <summary>A small PNG written with Core/Png: solid <paramref name="grey"/>.</summary>
    static void WritePng(string path, int w, int h, byte grey)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var px = new byte[w * h * 4];
        Array.Fill(px, grey);
        Png.Write(path, w, h, px);
    }

    static TrainingPicture Pic(string file, string? caption = null, params (int N, string Text)[] callouts) =>
        new() { File = file, Caption = caption, Callouts = callouts.Select(c => new TrainingCallout { Number = c.N, Text = c.Text }).ToList() };

    /// <summary>Two tests: "Insert kit block" (3 steps: ribbon, dialog, one passing checkpoint) and "Plot sheet" (1 step + a failing checkpoint, one picture file missing).</summary>
    TrainingManifest Sample()
    {
        WritePng(Path.Combine(_root, "pictures", "t1-s1.png"), 64, 40, 200);
        WritePng(Path.Combine(_root, "pictures", "t1-s2.png"), 48, 32, 180);
        WritePng(Path.Combine(_root, "pictures", "t1-s3.png"), 64, 40, 160);
        WritePng(Path.Combine(_root, "pictures", "t2-s2.png"), 64, 40, 140);
        return new TrainingManifest
        {
            Title = "Kit training",
            CreatedUtc = "2026-10-30T09:15:00Z",
            RetraceVersion = "1.2.3",
            Tests =
            [
                new TrainingTest
                {
                    Name = "Insert kit block", Folder = "tests/insert-kit-block", Verdict = "PASS",
                    Steps =
                    [
                        new TrainingStep { Number = 1, Kind = "ribbon", Title = "Insert > Create Block", Instructions = ["Click Create Block on the Insert tab."],
                            Pictures = [Pic("pictures/t1-s1.png", "The Insert tab", (1, "Create Block"))] },
                        new TrainingStep { Number = 2, Kind = "dialog", Title = "Block Definition", Instructions = ["In Block Definition, set Name to KitBlock.", "Click OK."],
                            Pictures = [Pic("pictures/t1-s2.png", null, (1, "Name: KitBlock"), (2, "OK"))] },
                        new TrainingStep { Number = 3, Kind = "mark", Title = "Checkpoint", Instructions = ["Checkpoint: block exists."],
                            Pictures = [Pic("pictures/t1-s3.png")],
                            Checkpoint = new TrainingCheckpoint { Index = 1, Note = "block exists", Pass = true } },
                    ],
                },
                new TrainingTest
                {
                    Name = "Plot sheet", Folder = "tests/plot-sheet", Verdict = "FAIL", Message = "1 checkpoint differs",
                    Steps =
                    [
                        new TrainingStep { Number = 1, Kind = "command", Title = "PLOT", Instructions = ["Type PLOT at the command line."],
                            Pictures = [Pic("pictures/gone.png", "Command line")] },
                        new TrainingStep { Number = 2, Kind = "mark", Title = "Checkpoint", Instructions = [],
                            Pictures = [Pic("pictures/t2-s2.png")],
                            Checkpoint = new TrainingCheckpoint { Index = 1, Note = "sheet plotted", Pass = false, Differences = ["Layers: 3 expected, 4 found", "Count of LINE: 2 expected, 1 found"] } },
                        new TrainingStep { Number = 3, Kind = "mark", Title = "Checkpoint", Checkpoint = new TrainingCheckpoint { Index = 2, Note = "not compared" } },
                    ],
                },
            ],
        };
    }

    // ---- manifest ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Manifest_round_trips_through_camelCase_json()
    {
        var m = Sample();
        var path = Path.Combine(_root, "sub", "tdoc.json");
        m.Write(path);
        Assert.False(File.Exists(Path.Combine(_root, "sub", ".tdoc.json.tmp")));
        var json = File.ReadAllText(path);
        Assert.Contains("\"createdUtc\"", json);
        Assert.Contains("\"retraceVersion\"", json);
        Assert.Contains("\"instructions\"", json);
        Assert.Contains("\"callouts\"", json);
        Assert.DoesNotContain("\"Title\"", json);

        var back = TrainingManifest.Read(path)!;
        Assert.Equal("Kit training", back.Title);
        Assert.Equal("2026-10-30T09:15:00Z", back.CreatedUtc);
        Assert.Equal("1.2.3", back.RetraceVersion);
        Assert.Equal(2, back.Tests.Count);
        var t1 = back.Tests[0];
        Assert.Equal("Insert kit block", t1.Name);
        Assert.Equal("PASS", t1.Verdict);
        Assert.Null(t1.Message);
        Assert.Equal(3, t1.Steps.Count);
        Assert.Equal(["In Block Definition, set Name to KitBlock.", "Click OK."], t1.Steps[1].Instructions);
        var pic = t1.Steps[1].Pictures.Single();
        Assert.Equal("pictures/t1-s2.png", pic.File);
        Assert.Null(pic.Caption);
        Assert.Equal([(1, "Name: KitBlock"), (2, "OK")], pic.Callouts.Select(c => (c.Number, c.Text)).ToArray());
        Assert.Equal(true, t1.Steps[2].Checkpoint!.Pass);
        Assert.Null(t1.Steps[0].Checkpoint);
        var t2 = back.Tests[1];
        Assert.Equal("1 checkpoint differs", t2.Message);
        Assert.Equal(false, t2.Steps[1].Checkpoint!.Pass);
        Assert.Equal(2, t2.Steps[1].Checkpoint!.Differences.Count);
        Assert.Null(t2.Steps[2].Checkpoint!.Pass);
    }

    [Fact]
    public void Manifest_read_tolerates_unknown_missing_and_null_fields()
    {
        var path = Path.Combine(_root, "tolerant.json");
        File.WriteAllText(path, """
            {
              // a comment, a trailing comma and a field from a newer version
              "title": "Old file",
              "somethingNew": { "a": 1 },
              "tests": [
                null,
                { "NAME": "Only a name", "steps": null },
                { "name": "Half a step", "folder": null, "steps": [
                    null,
                    { "number": "2", "title": null, "instructions": [null, "Do it."], "pictures": [ { "file": "p.png", "callouts": null, "extra": true }, null ],
                      "checkpoint": { "index": 3, "pass": null, "differences": null }, },
                ] },
              ],
            }
            """);
        var m = TrainingManifest.Read(path)!;
        Assert.Equal("Old file", m.Title);
        Assert.Equal("", m.CreatedUtc);
        Assert.Equal("", m.RetraceVersion);
        Assert.Equal(2, m.Tests.Count);
        Assert.Equal("Only a name", m.Tests[0].Name);
        Assert.Empty(m.Tests[0].Steps);
        var s = Assert.Single(m.Tests[1].Steps);
        Assert.Equal(2, s.Number);
        Assert.Equal("", s.Title);
        Assert.Equal("other", s.Kind);
        Assert.Equal(["Do it."], s.Instructions);
        var p = Assert.Single(s.Pictures);
        Assert.Equal("p.png", p.File);
        Assert.Empty(p.Callouts);
        Assert.Equal(3, s.Checkpoint!.Index);
        Assert.Null(s.Checkpoint.Pass);
        Assert.Empty(s.Checkpoint.Differences);
    }

    [Fact]
    public void Manifest_read_gives_null_for_a_missing_or_broken_file()
    {
        Assert.Null(TrainingManifest.Read(Path.Combine(_root, "nope.json")));
        var bad = Path.Combine(_root, "bad.json");
        File.WriteAllText(bad, "{ not json");
        Assert.Null(TrainingManifest.Read(bad));
        File.WriteAllText(bad, "[1,2,3]");
        Assert.Null(TrainingManifest.Read(bad));
        File.WriteAllText(bad, "null");
        Assert.Null(TrainingManifest.Read(bad));
    }

    // ---- the document -------------------------------------------------------------------------------------------------

    static List<string> Errors(OpenXmlPackage pkg) =>
        new OpenXmlValidator(DocumentFormat.OpenXml.FileFormatVersions.Office2019).Validate(pkg).Select(e => $"{e.Description} @ {e.Path?.XPath}").ToList();

    static string StyleOf(W.Paragraph p) => p.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "";

    /// <summary>Paragraphs that are directly in the body (the legend cells and nothing else are inside tables).</summary>
    static List<W.Paragraph> BodyParagraphs(WordprocessingDocument d) => d.MainDocumentPart!.Document.Body!.Elements<W.Paragraph>().ToList();

    [Fact]
    public void Document_has_the_headings_in_order_and_the_expected_pictures()
    {
        var m = Sample();
        var docx = Path.Combine(_root, "out", "kit.docx");
        var count = TrainingDocx.Write(m, _root, docx);
        Assert.Equal(4, count);   // 5 pictures in the manifest, one file is missing
        Assert.False(File.Exists(Path.Combine(_root, "out", ".kit.docx.tmp")));

        using var d = WordprocessingDocument.Open(docx, false);
        var errors = Errors(d);
        Assert.True(errors.Count == 0, string.Join("\n", errors));

        var paras = BodyParagraphs(d);
        var headings = paras.Where(p => StyleOf(p) is "Title" or "Heading1" or "Heading2" or "Heading3").Select(p => (StyleOf(p), p.InnerText)).ToList();
        Assert.Equal(
            [
                ("Title", "Kit training"),
                ("Heading1", "Insert kit block"),
                ("Heading2", "Step 1: Insert > Create Block"),
                ("Heading2", "Step 2: Block Definition"),
                ("Heading2", "Step 3: Checkpoint"),
                ("Heading3", "Checkpoint 1: block exists"),
                ("Heading1", "Plot sheet"),
                ("Heading2", "Step 1: PLOT"),
                ("Heading2", "Step 2: Checkpoint"),
                ("Heading3", "Checkpoint 1: sheet plotted"),
                ("Heading2", "Step 3: Checkpoint"),
                ("Heading3", "Checkpoint 2: not compared"),
            ], headings);

        // page break before every test but the first
        var h1 = paras.Where(p => StyleOf(p) == "Heading1").ToList();
        Assert.Null(h1[0].ParagraphProperties!.PageBreakBefore);
        Assert.NotNull(h1[1].ParagraphProperties!.PageBreakBefore);

        // pictures: 4 embedded images, each its own paragraph with a drawing
        Assert.Equal(4, d.MainDocumentPart!.Document.Body!.Descendants<W.Drawing>().Count());
        Assert.Equal(4, d.MainDocumentPart.ImageParts.Count());

        var text = paras.Select(p => p.InnerText).ToList();
        Assert.Contains("Click Create Block on the Insert tab.", text);
        Assert.Contains("In Block Definition, set Name to KitBlock.", text);
        Assert.Contains("3 steps, 1 checkpoint, last result: PASS", text);
        Assert.Contains("3 steps, 2 checkpoints, last result: FAIL", text);
        Assert.Contains("1 checkpoint differs", text);
        Assert.Contains("The Insert tab", text);   // captions
    }

    [Fact]
    public void Document_has_the_legend_tables_and_the_checkpoint_result_lines()
    {
        var m = Sample();
        var docx = Path.Combine(_root, "kit.docx");
        TrainingDocx.Write(m, _root, docx);
        using var d = WordprocessingDocument.Open(docx, false);
        var body = d.MainDocumentPart!.Document.Body!;

        // legends: one table per picture with callouts, two columns, the number in a green cell
        var tables = body.Elements<W.Table>().ToList();
        Assert.Equal(2, tables.Count);
        Assert.Equal(["Create Block"], tables[0].Elements<W.TableRow>().Select(r => r.Elements<W.TableCell>().Last().InnerText).ToArray());
        Assert.Equal(["1", "2"], tables[1].Elements<W.TableRow>().Select(r => r.Elements<W.TableCell>().First().InnerText).ToArray());
        Assert.Equal(["Name: KitBlock", "OK"], tables[1].Elements<W.TableRow>().Select(r => r.Elements<W.TableCell>().Last().InnerText).ToArray());
        foreach (var row in tables.SelectMany(t => t.Elements<W.TableRow>()))
        {
            var cells = row.Elements<W.TableCell>().ToList();
            Assert.Equal(2, cells.Count);
            Assert.Equal("22B14C", cells[0].TableCellProperties!.Shading!.Fill!.Value);
            var run = cells[0].Descendants<W.Run>().Single();
            Assert.NotNull(run.RunProperties!.Bold);
            Assert.Equal("FFFFFF", run.RunProperties.Color!.Val!.Value);
        }

        // result lines: green PASS, red FAIL, grey not verified; differences as bullets
        var paras = BodyParagraphs(d);
        W.Paragraph Result(string text) => paras.Single(p => p.InnerText == text);
        Assert.Equal("C6EFCE", Result("Result: PASS").ParagraphProperties!.Shading!.Fill!.Value);
        Assert.Equal("FFC7CE", Result("Result: FAIL").ParagraphProperties!.Shading!.Fill!.Value);
        Assert.Equal("E7E6E6", Result("Result: not verified").ParagraphProperties!.Shading!.Fill!.Value);
        var failAt = paras.IndexOf(Result("Result: FAIL"));
        Assert.Contains("Layers: 3 expected, 4 found", paras[failAt + 1].InnerText);
        Assert.Contains("Count of LINE: 2 expected, 1 found", paras[failAt + 2].InnerText);
        Assert.StartsWith("•", paras[failAt + 1].InnerText);
    }

    [Fact]
    public void Document_has_a_toc_field_and_asks_word_to_update_it()
    {
        var docx = Path.Combine(_root, "kit.docx");
        TrainingDocx.Write(Sample(), _root, docx);
        using var d = WordprocessingDocument.Open(docx, false);
        var body = d.MainDocumentPart!.Document.Body!;
        var code = body.Descendants<W.FieldCode>().Single();
        Assert.Contains("TOC", code.Text);
        Assert.Contains("\\o \"1-2\"", code.Text);
        var chars = body.Descendants<W.FieldChar>().Select(c => c.FieldCharType!.Value).ToList();
        Assert.Equal([W.FieldCharValues.Begin, W.FieldCharValues.Separate, W.FieldCharValues.End], chars);
        Assert.Contains("Update field", BodyParagraphs(d).Single(p => p.Descendants<W.FieldCode>().Any()).InnerText);
        Assert.True(d.MainDocumentPart.DocumentSettingsPart!.Settings.Elements<W.UpdateFieldsOnOpen>().Single().Val!.Value);
    }

    [Fact]
    public void A_missing_picture_file_gets_a_note_instead()
    {
        var docx = Path.Combine(_root, "kit.docx");
        TrainingDocx.Write(Sample(), _root, docx);
        using var d = WordprocessingDocument.Open(docx, false);
        var paras = BodyParagraphs(d);
        var note = paras.Single(p => p.InnerText.Contains("picture not found"));
        Assert.Contains("pictures/gone.png", note.InnerText);
        Assert.NotNull(note.Descendants<W.Italic>().FirstOrDefault());
        Assert.Empty(note.Descendants<W.Drawing>());
        Assert.Contains("Command line", paras.Select(p => p.InnerText));   // its caption is still there
    }

    [Fact]
    public void A_file_that_is_not_a_png_and_a_bad_path_are_notes_too()
    {
        File.WriteAllText(Path.Combine(_root, "text.png"), "not a picture");
        var m = new TrainingManifest
        {
            Title = "T",
            Tests = [new TrainingTest { Name = "A", Steps = [new TrainingStep { Number = 1, Title = "s", Pictures = [Pic("text.png"), Pic(""), Pic("bad\0name.png")] }] }],
        };
        var docx = Path.Combine(_root, "n.docx");
        Assert.Equal(0, TrainingDocx.Write(m, _root, docx));
        using var d = WordprocessingDocument.Open(docx, false);
        Assert.Equal(3, BodyParagraphs(d).Count(p => p.InnerText.StartsWith("[picture not found")));
        Assert.Empty(Errors(d));
    }

    [Fact]
    public void An_empty_manifest_still_makes_a_valid_document()
    {
        var docx = Path.Combine(_root, "empty.docx");
        Assert.Equal(0, TrainingDocx.Write(new TrainingManifest(), _root, docx));
        using var d = WordprocessingDocument.Open(docx, false);
        Assert.Empty(Errors(d));
        Assert.Contains(BodyParagraphs(d), p => StyleOf(p) == "Title");
    }

    [Fact]
    public void Writing_again_replaces_the_document()
    {
        var docx = Path.Combine(_root, "kit.docx");
        Assert.Equal(4, TrainingDocx.Write(Sample(), _root, docx));
        Assert.Equal(0, TrainingDocx.Write(new TrainingManifest { Title = "Second" }, _root, docx));
        using var d = WordprocessingDocument.Open(docx, false);
        Assert.Contains(BodyParagraphs(d), p => p.InnerText == "Second");
    }

    // ---- EngineRunner.WriteTrainingDoc ------------------------------------------------------------------------------------

    [Fact]
    public void WriteTrainingDoc_reads_the_manifest_and_returns_the_docx()
    {
        Sample().Write(Path.Combine(_root, "tdoc.json"));
        var docx = Path.Combine(_root, "doc", "kit.docx");
        var r = EngineRunner.WriteTrainingDoc(Path.Combine(_root, "tdoc.json"), docx);
        Assert.True(r.Success, r.Error);
        Assert.Equal(Path.GetFullPath(docx), r.Docx);
        Assert.True(File.Exists(docx));
        Assert.Contains(r.Warnings, w => w.Contains("1 picture file(s) not found"));
        Assert.Contains(r.Lines(), l => l.Contains("4 picture(s)"));
    }

    [Fact]
    public void WriteTrainingDoc_never_throws()
    {
        var r = EngineRunner.WriteTrainingDoc(Path.Combine(_root, "none.json"), Path.Combine(_root, "x.docx"));
        Assert.False(r.Success);
        Assert.Contains("cannot read the training manifest", r.Error);
        Assert.False(File.Exists(Path.Combine(_root, "x.docx")));

        Sample().Write(Path.Combine(_root, "tdoc.json"));
        var blocked = Path.Combine(_root, "blocked");
        File.WriteAllText(blocked, "a file where a folder should be");
        r = EngineRunner.WriteTrainingDoc(Path.Combine(_root, "tdoc.json"), Path.Combine(blocked, "x.docx"));
        Assert.False(r.Success);
        Assert.NotNull(r.Error);
    }
}
