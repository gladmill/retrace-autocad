using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AcadStatus.Core;

namespace AcadStatus.Tests;

public class TextUtilTests
{
    [Theory]
    [InlineData("  a\n b\t\tc  ", "a b c")]
    [InlineData("one", "one")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("  \r\n ", "")]
    [InlineData("a\u0001b", "a b")]
    public void OneLineCollapsesWhiteSpaceAndControls(string? input, string expected) => Assert.Equal(expected, TextUtil.OneLine(input));

    [Theory]
    [InlineData("abcdef", 3, "abc")]
    [InlineData("abc def", 4, "abc")]        // no trailing space left behind
    [InlineData("abc", 3, "abc")]
    [InlineData("abc", 0, "")]
    [InlineData("abc", -1, "")]
    [InlineData(null, 5, "")]
    public void CleanCutsWithoutAnEllipsis(string? input, int max, string expected) => Assert.Equal(expected, TextUtil.Clean(input, max));

    [Theory]
    [InlineData("abcdef", 4, "abc…")]
    [InlineData("abc def", 5, "abc…")]
    [InlineData("abcdef", 1, "…")]
    [InlineData("abc", 3, "abc")]
    [InlineData("abc", 0, "")]
    public void CapEndsALongTextInAnEllipsis(string input, int max, string expected) => Assert.Equal(expected, TextUtil.Cap(input, max));

    [Fact]
    public void CleanAndCapAgreeOnEverythingThatFits()
    {
        foreach (var s in new[] { "  a  b ", "x\ny", "plain", "", "tab\there" })
            Assert.Equal(TextUtil.Clean(s, 50), TextUtil.Cap(s, 50));
    }

    [Fact]
    public void EveryToneHasItsOwnColour()
    {
        var colours = Enum.GetValues<Tone>().Select(TonePalette.Of).ToList();
        Assert.Equal(colours.Count, colours.Distinct().Count());
    }

    [Fact]
    public void TheDotsAndTheTextShareOnePaletteAndNothingElseDefinesOne()
    {
        // the status bar dot and the details window read TonePalette; no other file maps a Tone to a colour
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Here())!, "..", "..", "src", "AcadStatus"));
        foreach (var f in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
            var text = File.ReadAllText(f);
            if (Path.GetFileName(f) == "TextUtil.cs") continue;
            Assert.DoesNotContain("Tone.Green =>", text);
            Assert.DoesNotContain("new Rgb(", text);
        }
        foreach (var name in new[] { Path.Combine("AutoCad", "StatusPane.cs"), Path.Combine("Ui", "DetailsForm.cs") })
            Assert.Contains("TonePalette.Of(", File.ReadAllText(Path.Combine(root, name)));
    }

    static string Here([CallerFilePath] string p = "") => p;
}

public class AcadStatusDocTests
{
    static DateTime Now => TestData.Now;
    static readonly DateTime Started = new(2026, 11, 5, 8, 2, 11, DateTimeKind.Utc);

    static AcadStatusBuilder NewBuilder(DateTime? start = null) => new("AcadStatus 0.1.0", 4812, start ?? Started);

    [Fact]
    public void ThePidStartIsWrittenAndParsed()
    {
        var doc = NewBuilder().Build(TestData.Snap(), Now);
        using var json = JsonDocument.Parse(doc.Json);
        Assert.Equal("2026-11-05T08:02:11Z", json.RootElement.GetProperty("pidStart").GetString());
        var e = StatusParser.Parse("acad-4812.json", doc.Json).Entry!;
        Assert.Equal(Started, e.PidStart);
        Assert.Equal(Started, doc.Entry.PidStart);
    }

    [Fact]
    public void WithoutAKnownStartNoPidStartIsWritten()
    {
        var doc = new AcadStatusBuilder("w", 1).Build(TestData.Snap(), Now);
        using var json = JsonDocument.Parse(doc.Json);
        Assert.False(json.RootElement.TryGetProperty("pidStart", out _));
        Assert.Null(StatusParser.Parse("acad-1.json", doc.Json).Entry!.PidStart);
    }

    [Fact]
    public void TheTtlIsTenSeconds() => Assert.Equal(10, AcadStatusBuilder.TtlSec);

    [Fact]
    public void TheEntryIsWhatAReaderWouldParseFromTheFile()
    {
        var snaps = new[]
        {
            TestData.Snap(),
            TestData.Snap(doc: null, docs: 0),
            TestData.Snap(cmdActive: 1, cmdNames: "OFFSET", prompt: "Specify point on side to offset or [Exit/Multiple/Undo] <Exit>:"),
            TestData.Snap(prompt: ""),
            TestData.Snap(cmdActive: 32, dialog: TestData.Dialog(texts: new[] { "a", "b" }, buttons: new[] { "OK", "Cancel" }), dialogSince: Now.AddSeconds(-3)),
            TestData.Snap(dialog: TestData.Dialog(texts: Array.Empty<string>(), buttons: Array.Empty<string>(), modal: false), dialogSince: Now.AddSeconds(-30)),
        };
        foreach (var s in snaps)
        {
            var doc = NewBuilder().Build(s, Now);
            var parsed = StatusParser.Parse("acad-4812.json", doc.Json).Entry!;
            var e = doc.Entry;
            Assert.Equal(parsed.File, e.File);
            Assert.Equal(parsed.Source, e.Source);
            Assert.Equal(parsed.Writer, e.Writer);
            Assert.Equal(parsed.Pid, e.Pid);
            Assert.Equal(parsed.PidStart, e.PidStart);
            Assert.Equal(parsed.TtlSec, e.TtlSec);
            Assert.Equal(parsed.State, e.State);
            Assert.Equal(parsed.Summary, e.Summary);
            Assert.Equal(parsed.Short, e.Short);
            Assert.Equal(parsed.Since, e.Since);
            Assert.Equal(parsed.Owner, e.Owner);
            Assert.Equal(parsed.Acad!.Doc, e.Acad!.Doc);
            Assert.Equal(parsed.Acad.Docs, e.Acad.Docs);
            Assert.Equal(parsed.Acad.CmdActive, e.Acad.CmdActive);
            Assert.Equal(parsed.Acad.CmdNames, e.Acad.CmdNames);
            Assert.Equal(parsed.Acad.Prompt, e.Acad.Prompt);
            Assert.Equal(parsed.Acad.Dialog?.Title, e.Acad.Dialog?.Title);
            Assert.Equal(parsed.Acad.Dialog?.Class, e.Acad.Dialog?.Class);
            Assert.Equal(parsed.Acad.Dialog?.Modal, e.Acad.Dialog?.Modal);
            Assert.Equal(parsed.Acad.Dialog?.Texts ?? Array.Empty<string>(), e.Acad.Dialog?.Texts ?? Array.Empty<string>());
            Assert.Equal(parsed.Acad.Dialog?.Buttons ?? Array.Empty<string>(), e.Acad.Dialog?.Buttons ?? Array.Empty<string>());
            Assert.Equal(parsed.Acad.Dialog?.Since, e.Acad.Dialog?.Since);
            Assert.Equal("", e.Raw);
        }
    }

    [Fact]
    public void TheStoppedEntryIsWhatAReaderWouldParse()
    {
        var doc = NewBuilder().BuildStopped(Now);
        var parsed = StatusParser.Parse("acad-4812.json", doc.Json).Entry!;
        Assert.Equal(parsed.State, doc.Entry.State);
        Assert.Equal(parsed.Summary, doc.Entry.Summary);
        Assert.Null(doc.Entry.Acad);
        Assert.Null(parsed.Acad);
        Assert.Equal(parsed.PidStart, doc.Entry.PidStart);
    }

    [Fact]
    public void TheEntryMergesLikeAnyOther()
    {
        var doc = NewBuilder().Build(TestData.Snap(cmdActive: 1, cmdNames: "LINE", prompt: "Specify first point:"), Now);
        var m = TestMerge.Merge(new[] { doc.Entry }, Now, _ => true).Shown.Single();
        Assert.Equal("waiting-input", m.State);
        Assert.Equal("Input: LINE", CompactText.TextOf(m, Now));
        Assert.Equal(TimeSpan.Zero, m.Age);
    }

    [Fact]
    public void TheJsonIsRenderedOnceAndOnlyWhenAsked()
    {
        var doc = NewBuilder().Build(TestData.Snap(), Now);
        var a = doc.Json;
        Assert.Same(a, doc.Json);
    }

    [Fact]
    public void TheContentKeyNeedsNoJsonAndSeesEveryField()
    {
        var b = NewBuilder();
        var keys = new[]
        {
            b.Build(TestData.Snap(), Now).ContentKey,
            b.Build(TestData.Snap(doc: "x.dwg"), Now).ContentKey,
            b.Build(TestData.Snap(docs: 3), Now).ContentKey,
            b.Build(TestData.Snap(cmdActive: 1, cmdNames: "A"), Now).ContentKey,
            b.Build(TestData.Snap(cmdActive: 1, cmdNames: "B"), Now).ContentKey,
            b.Build(TestData.Snap(cmdActive: 1, cmdNames: "B", prompt: "p1"), Now).ContentKey,
            b.Build(TestData.Snap(cmdActive: 1, cmdNames: "B", prompt: "p2"), Now).ContentKey,
            b.Build(TestData.Snap(dialog: TestData.Dialog(texts: new[] { "t1" }), dialogSince: Now), Now).ContentKey,
            b.Build(TestData.Snap(dialog: TestData.Dialog(texts: new[] { "t2" }), dialogSince: Now), Now).ContentKey,
            b.Build(TestData.Snap(dialog: TestData.Dialog(texts: new[] { "t2" }, buttons: new[] { "Yes" }), dialogSince: Now), Now).ContentKey,
            b.Build(TestData.Snap(dialog: TestData.Dialog(texts: new[] { "t2" }, buttons: new[] { "Yes" }, modal: false), dialogSince: Now), Now).ContentKey,
        };
        Assert.Equal(keys.Length, keys.Distinct().Count());
        // a different second does not change it
        Assert.Equal(b.Build(TestData.Snap(), Now).ContentKey, b.Build(TestData.Snap(), Now.AddSeconds(1)).ContentKey);
    }

    [Fact]
    public void TextsThatRunTogetherDoNotMakeTheSameKey()
    {
        var b = NewBuilder();
        var one = b.Build(TestData.Snap(dialog: TestData.Dialog(texts: new[] { "ab", "c" }), dialogSince: Now), Now).ContentKey;
        var two = b.Build(TestData.Snap(dialog: TestData.Dialog(texts: new[] { "a", "bc" }), dialogSince: Now), Now).ContentKey;
        Assert.NotEqual(one, two);
    }
}

public class WriteCadenceTests
{
    static readonly DateTime T0 = new(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void WithAJitteringTimerTheGapBetweenWritesStaysNearTwoSecondsAndAReaderNeverSeesAStaleFile()
    {
        var rng = new Random(12345);
        var b = new AcadStatusBuilder("w", 1);
        string? lastKey = null;
        DateTime? lastWrite = null;
        var now = T0;
        var writes = new List<DateTime>();
        double worstAge = 0;
        for (int i = 0; i < 3600; i++)
        {
            // the tick comes 0.85 to 1.2 s after the last one
            now = now.AddMilliseconds(850 + rng.Next(0, 350));
            var doc = b.Build(TestData.Snap(), now);
            if (WritePolicy.ShouldWrite(lastKey, lastWrite, doc.ContentKey, now))
            {
                lastKey = doc.ContentKey; lastWrite = now; writes.Add(now);
            }
            // what a reader sees: updated is written to the whole second
            var written = new DateTime(lastWrite!.Value.Ticks - lastWrite.Value.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
            worstAge = Math.Max(worstAge, (now - written).TotalSeconds);
        }
        var gaps = writes.Zip(writes.Skip(1), (a, c) => (c - a).TotalSeconds).ToList();
        Assert.True(gaps.Max() <= 2.41, "longest gap " + gaps.Max());
        Assert.True(gaps.Min() >= 1.5, "shortest gap " + gaps.Min());
        Assert.True(worstAge < AcadStatusBuilder.TtlSec / 2.0, "oldest a reader saw: " + worstAge);
    }

    [Fact]
    public void TheWholeSecondTruncationAndTheRefreshStayInsideHalfTheTtl()
    {
        // worst case by construction: a write just after a second ticks over, the next one a full tick late
        Assert.True(1.0 + WritePolicy.RefreshSec + 1.2 < AcadStatusBuilder.TtlSec / 2.0);
    }
}

public class SourceRuleReviewTests
{
    static string Root([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "AcadStatus"));

    static string Read(params string[] parts) => File.ReadAllText(Path.Combine(new[] { Root() }.Concat(parts).ToArray()));

    [Fact]
    public void TheServiceDoesNotParseItsOwnFileBack()
    {
        Assert.DoesNotContain("StatusParser", Read("AutoCad", "StatusService.cs"));
    }

    [Fact]
    public void TheDetailsWindowMakesItsFontsOnceAndDisposesThem()
    {
        var text = Read("Ui", "DetailsForm.cs");
        // the regular and the bold font are made once, with the window; text runs only pick one of them
        Assert.True(System.Text.RegularExpressions.Regex.Matches(text, "new Font\\(").Count <= 2);
        var append = text[text.IndexOf("void Append(", StringComparison.Ordinal)..];
        Assert.DoesNotContain("new Font", append);
        Assert.Contains("_bold.Dispose()", text);
        Assert.Contains("_regular.Dispose()", text);
    }

    [Fact]
    public void TheDetailsWindowKeepsEveryAgeOutOfWhatItRebuildsFrom()
    {
        var text = Read("Ui", "DetailsForm.cs");
        Assert.DoesNotContain("i.Reason", text);   // "no update for 47m47s" ticks every second; Why does not
        Assert.Contains("i.Why", text);
    }

    [Fact]
    public void TheDetailsWindowClampsToTheWorkingAreaNotToZero()
    {
        var text = Read("Ui", "DetailsForm.cs");
        Assert.DoesNotContain("Math.Max(0,", text);
        Assert.Contains("Placement.Above(", text);
    }

    [Fact]
    public void NoTimerGuardDecidesWhetherAClickIsTheSecondOne()
    {
        var all = Read("AutoCad", "StatusService.cs") + Read("Ui", "DetailsForm.cs");
        Assert.DoesNotContain("TickCount", all);
        Assert.DoesNotContain("ClosedAtMs", all);
        Assert.Contains("ClickGuard", all);
    }

    [Fact]
    public void ThePromptStateIsPerDocument()
    {
        var text = Read("AutoCad", "PromptWatch.cs");
        Assert.Contains("Dictionary<Document,", text);
        Assert.DoesNotContain("LispDepth", text);
        Assert.DoesNotContain("Tracker.Clear()", text);
    }

    [Fact]
    public void ProcessesAreLookedUpThroughTheCacheOnly()
    {
        var text = Read("AutoCad", "StatusService.cs");
        Assert.DoesNotContain("Process.GetProcessById", text);
        Assert.Contains("_liveness.IsAlive(", text);
    }
}
