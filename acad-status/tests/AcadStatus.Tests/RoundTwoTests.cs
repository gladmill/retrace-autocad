using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AcadStatus.Core;

namespace AcadStatus.Tests;

/// <summary>Surrogate pairs (emoji) and lone surrogates: a cut through a pair, or a lone surrogate, made Utf8JsonWriter throw and the file stop.</summary>
public class SurrogateSafetyTests
{
    const string Emoji = "\U0001F600";   // a surrogate pair: two UTF-16 chars

    static bool HasLoneSurrogate(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { i++; continue; }
            if (char.IsSurrogate(s[i])) return true;
        }
        return false;
    }

    /// <summary>filler characters, then the emoji whose first half is the character at index <paramref name="at"/>.</summary>
    static string EmojiAt(int at, string tail = " tail") => new string('x', at) + Emoji + tail;

    [Fact]
    public void SafeTruncateNeverSplitsAPair()
    {
        var s = EmojiAt(99);                       // the pair is at indices 99 and 100
        Assert.Equal(101, TextUtil.SafeTruncate(s, 101).Length);   // 99 + the whole pair
        var cut = TextUtil.SafeTruncate(s, 100);                   // would end after the high surrogate
        Assert.Equal(99, cut.Length);
        Assert.False(HasLoneSurrogate(cut));
        Assert.Equal(s, TextUtil.SafeTruncate(s, 500));
        Assert.Equal("", TextUtil.SafeTruncate(s, 0));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(18)]
    [InlineData(60)]
    [InlineData(100)]
    [InlineData(200)]
    [InlineData(300)]
    public void CleanNeverLeavesALoneSurrogateAtAnyCut(int max)
    {
        for (int at = max - 3; at <= max + 1; at++)
        {
            if (at < 0) continue;
            var cut = TextUtil.Clean(EmojiAt(at), max);
            Assert.True(cut.Length <= max, $"max {max} at {at}: {cut.Length}");
            Assert.False(HasLoneSurrogate(cut), $"max {max} at {at}");
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(18)]
    [InlineData(120)]
    [InlineData(200)]
    public void CapNeverLeavesALoneSurrogateAtAnyCut(int max)
    {
        for (int at = max - 4; at <= max + 1; at++)
        {
            if (at < 0) continue;
            var cut = TextUtil.Cap(EmojiAt(at), max);
            Assert.True(cut.Length <= max, $"max {max} at {at}: {cut.Length}");
            Assert.False(HasLoneSurrogate(cut), $"max {max} at {at}");
            if (at + 2 > max) Assert.EndsWith("…", cut);
        }
    }

    [Fact]
    public void AnEmojiThatFitsStaysWhole()
    {
        Assert.Equal("ab" + Emoji, TextUtil.Clean("ab" + Emoji + "cd", 4));
        Assert.Equal("ab" + Emoji + "…", TextUtil.Cap("ab" + Emoji + "cdef", 5));
    }

    [Theory]
    [InlineData("a\uD83Db")]            // a lone high surrogate
    [InlineData("a\uDE00b")]            // a lone low surrogate
    [InlineData("\uD83D")]
    [InlineData("\uDE00\uD83D")]        // reversed pair
    [InlineData("ok " + "\U0001F600" + " \uD83D end")]
    public void SanitizeReplacesLoneSurrogatesOnly(string input)
    {
        var s = TextUtil.Sanitize(input);
        Assert.False(HasLoneSurrogate(s));
        Assert.Equal(input.Length, s.Length);
        if (!HasLoneSurrogate(input)) Assert.Same(input, s);
        Assert.Contains(TextUtil.Replacement, s);
    }

    [Fact]
    public void SanitizeLeavesGoodTextAlone()
    {
        var good = "plain " + Emoji + " é 日本語";
        Assert.Same(good, TextUtil.Sanitize(good));
        Assert.Equal("", TextUtil.Sanitize(null));
        Assert.Equal("", TextUtil.Sanitize(""));
    }

    [Fact]
    public void OneLineAndCleanReplaceLoneSurrogatesToo()
    {
        Assert.False(HasLoneSurrogate(TextUtil.OneLine("a \uD83D b")));
        Assert.False(HasLoneSurrogate(TextUtil.Clean("\uDE00 x", 10)));
        Assert.False(HasLoneSurrogate(PromptTracker.Clean("Specify \uD83D point")));
    }

    // ---- the file

    static AcadStatusBuilder NewBuilder() => new("AcadStatus 0.1.0", 4812, new DateTime(2026, 11, 5, 8, 0, 0, DateTimeKind.Utc));
    static DateTime Now => TestData.Now;

    [Fact]
    public void AnAlertWithAnEmojiAtTheCutStillRendersAndParses()
    {
        // the summary quotes the first dialog text cut at 100 characters, then the whole line is cut at 200: put the emoji on both cuts
        var text = EmojiAt(99, "tail of the message");
        var d = TestData.Dialog(texts: new[] { text }, buttons: new[] { "OK" });
        var doc = NewBuilder().Build(TestData.Snap(dialog: d, dialogSince: Now.AddSeconds(-20)), Now);
        var json = doc.Json;                                  // must not throw
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal("blocked", parsed.RootElement.GetProperty("state").GetString());
        Assert.False(HasLoneSurrogate(doc.Entry.Summary));
        Assert.True(doc.Entry.Summary.Length <= 200);
        var e = StatusParser.Parse("acad-4812.json", json).Entry!;
        Assert.Equal(text, e.Acad!.Dialog!.Texts.Single());   // the full text is in the detail, whole
    }

    [Fact]
    public void EveryCutOfTheSummaryIsSurrogateSafe()
    {
        int cutsThroughAPair = 0;
        for (int at = 150; at <= 205; at++)
        {
            // a long prompt: the summary is "Waiting for input in LINE: " + the prompt, cut at 200, so some `at` puts the emoji on the cut
            var prompt = new string('p', at) + Emoji + "tail";
            var doc = NewBuilder().Build(TestData.Snap(cmdActive: 1, cmdNames: "LINE", prompt: prompt), Now);
            Assert.False(HasLoneSurrogate(doc.Entry.Summary), "prompt at " + at);
            Assert.True(doc.Entry.Summary.Length <= 200);
            if (doc.Entry.Summary.Length == 199) cutsThroughAPair++;   // 199: the cut backed off one character to keep the pair whole
            using (JsonDocument.Parse(doc.Json)) { }

            // a long dialog text and title
            var title = new string('t', at) + Emoji;
            var dlg = NewBuilder().Build(TestData.Snap(dialog: TestData.Dialog(title: title, texts: new[] { EmojiAt(at) }), dialogSince: Now.AddSeconds(-20)), Now);
            Assert.False(HasLoneSurrogate(dlg.Entry.Summary), "dialog at " + at);
            Assert.True(dlg.Entry.Summary.Length <= 200);
            using (JsonDocument.Parse(dlg.Json)) { }
        }
        Assert.True(cutsThroughAPair > 0, "the loop never put the emoji on the cut at 200");
    }

    [Fact]
    public void LoneSurrogatesInAnyFieldNeverStopTheFile()
    {
        var lone = "bad\uD83D text \uDE00 here";
        var snaps = new[]
        {
            TestData.Snap(doc: lone),
            TestData.Snap(cmdActive: 1, cmdNames: lone, prompt: lone),
            TestData.Snap(dialog: TestData.Dialog(title: lone, texts: new[] { lone, lone }, buttons: new[] { lone }), dialogSince: Now),
            TestData.Snap(dialog: TestData.Dialog(title: "t", texts: new[] { "x" }, buttons: new[] { "o" }) with { Class = lone }, dialogSince: Now),
        };
        foreach (var s in snaps)
        {
            var doc = NewBuilder().Build(s, Now);
            var json = doc.Json;
            using var parsed = JsonDocument.Parse(json);   // valid JSON, and a reader gets it
            Assert.True(StatusParser.Parse("acad-4812.json", json).Ok);
            Assert.False(HasLoneSurrogate(json));
            Assert.False(HasLoneSurrogate(doc.Entry.Summary));
            Assert.False(HasLoneSurrogate(doc.Entry.Short ?? ""));
        }
    }

    [Fact]
    public void ThePaneTextAndTheTooltipAreSurrogateSafeToo()
    {
        var e = TestData.Entry(state: "running", @short: "Suite 3/7 smoke " + Emoji + Emoji + Emoji, summary: "Suite 3/7");
        var m = TestMerge.Merge(new[] { e }, TestData.Now, _ => true).Shown.Single();
        var text = CompactText.TextOf(m, TestData.Now);
        Assert.True(text.Length <= CompactText.MaxChars);
        Assert.False(HasLoneSurrogate(text));
        var blocked = TestData.Merged("blocked", "x" + new string('y', 5) + Emoji + Emoji + Emoji + Emoji, since: TestData.Now.AddSeconds(-61));
        Assert.False(HasLoneSurrogate(CompactText.TextOf(blocked, TestData.Now)));
    }
}

public class DialogClassesTests
{
    [Theory]
    [InlineData("Static", true)]
    [InlineData("WindowsForms10.STATIC.app.0.141b42a_r6_ad1", true)]
    [InlineData("WindowsForms10.Static.app.0.141b42a_r6_ad1", true)]
    [InlineData("WindowsForms10.BUTTON.app.0.141b42a_r6_ad1", false)]
    [InlineData("Button", false)]
    [InlineData("Edit", false)]
    [InlineData("static", false)]   // Win32 class names compare exactly for the plain class
    [InlineData("", false)]
    public void IsText(string cls, bool expected) => Assert.Equal(expected, DialogClasses.IsText(cls));

    [Theory]
    [InlineData("Button", true)]
    [InlineData("WindowsForms10.BUTTON.app.0.141b42a_r6_ad1", true)]
    [InlineData("WindowsForms10.Button.app.0.141b42a_r6_ad1", true)]
    [InlineData("WindowsForms10.STATIC.app.0.141b42a_r6_ad1", false)]
    [InlineData("WindowsForms10.EDIT.app.0.141b42a_r6_ad1", false)]
    [InlineData("Static", false)]
    [InlineData("", false)]
    public void IsButton(string cls, bool expected) => Assert.Equal(expected, DialogClasses.IsButton(cls));

    [Fact]
    public void AWinFormsMessageBoxLikeDialogCountsAsAMessageBox()
    {
        Assert.True(DialogPick.IsMessageBox(new[] { "WindowsForms10.STATIC.app.0.141b42a_r6_ad1", "WindowsForms10.BUTTON.app.0.141b42a_r6_ad1" }));
        Assert.False(DialogPick.IsMessageBox(new[] { "WindowsForms10.STATIC.app.0.141b42a_r6_ad1", "WindowsForms10.BUTTON.app.0.141b42a_r6_ad1", "WindowsForms10.EDIT.app.0.141b42a_r6_ad1" }));
    }

    [Fact]
    public void TheScanReadsWinFormsTextsAndButtonsThroughTheHelpersAndNeverUsesUiAutomation()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Here())!, "..", "..", "src", "AcadStatus"));
        var scan = File.ReadAllText(Path.Combine(root, "AutoCad", "DialogScan.cs"));
        Assert.Contains("DialogClasses.IsText(", scan);
        Assert.Contains("DialogClasses.IsButton(", scan);
        Assert.DoesNotContain("== \"Static\"", scan);
        Assert.DoesNotContain("== \"Button\"", scan);
        foreach (var f in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)))
        {
            var text = File.ReadAllText(f);
            Assert.DoesNotContain("System.Windows.Automation", text);   // UI Automation from the UI thread that owns the window can deadlock
            Assert.DoesNotContain("UIAutomation", text);
        }
    }

    [Fact]
    public void DialogScanCutsTextsSurrogateSafe()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Here())!, "..", "..", "src", "AcadStatus"));
        var scan = File.ReadAllText(Path.Combine(root, "AutoCad", "DialogScan.cs"));
        Assert.Contains("TextUtil.SafeTruncate(", scan);
        Assert.DoesNotContain("s[..MaxText]", scan);
    }

    static string Here([CallerFilePath] string p = "") => p;
}

public class TickStructureTests
{
    static string Service([CallerFilePath] string here = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "AcadStatus", "AutoCad", "StatusService.cs"));

    static string TickBody()
    {
        var text = Service();
        var start = text.IndexOf("void Tick()", StringComparison.Ordinal);
        var end = text.IndexOf("bool NeedsMerge", StringComparison.Ordinal);
        return text[start..end];
    }

    [Fact]
    public void TheScanAndTheWriteComeBeforeTheGuardAndAreNotGuarded()
    {
        var body = TickBody();
        int sample = body.IndexOf("Sample(now)", StringComparison.Ordinal);
        int write = body.IndexOf("Write(snap, now)", StringComparison.Ordinal);
        int guard = body.IndexOf("if (_inUi) return;", StringComparison.Ordinal);
        Assert.True(sample >= 0 && write > sample && guard > write, "scan, then write, then the guard");
        Assert.DoesNotContain("_inTick", Service());
    }

    [Fact]
    public void OnlyThePaneAndWindowPartIsGuardedAndOnlyWhenSomethingShowsIt()
    {
        var body = TickBody();
        var guarded = body[body.IndexOf("if (_inUi) return;", StringComparison.Ordinal)..];
        Assert.Contains("UpdatePane(now)", guarded);
        Assert.Contains("UpdateDetails(now)", guarded);
        Assert.Contains("Merge(now, false)", guarded);
        Assert.Contains("if (NeedsMerge)", guarded);
        Assert.Contains("_inUi = false", guarded);
        var needs = Service();
        needs = needs[needs.IndexOf("bool NeedsMerge", StringComparison.Ordinal)..];
        Assert.Contains("_paneWanted", needs[..200]);
        Assert.Contains("Visible: true", needs[..200]);
    }

    [Fact]
    public void TheDumpMergesOnDemand()
    {
        var text = Service();
        var dump = text[text.IndexOf("public string DumpText()", StringComparison.Ordinal)..];
        Assert.Contains("Merge(now, true)", dump);
    }

    [Fact]
    public void TheLivenessLambdaCarriesTheAgeThrough()
    {
        var text = Service();
        Assert.Contains("(pid, start, age) => _liveness.IsAlive(pid, start, now, age)", text);
        Assert.Contains("ProcessProbe.Probe", text);
    }

    [Fact]
    public void StatusFilesHasNoPidOnlyOverloads()
    {
        var path = Path.Combine(Path.GetDirectoryName(Here())!, "..", "..", "src", "AcadStatus", "Core", "StatusFiles.cs");
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("Func<int, bool>", text);
        Assert.DoesNotContain("Func<int, DateTime?, bool>", text);
    }

    static string Here([CallerFilePath] string p = "") => p;
}

public class DetailsKeyTests
{
    [Fact]
    public void TheStructureKeyIncludesTheSkippedReason()
    {
        var path = Path.Combine(Path.GetDirectoryName(Here())!, "..", "..", "src", "AcadStatus", "Ui", "DetailsForm.cs");
        var text = File.ReadAllText(path);
        Assert.Contains("Append(s.File).Append(s.Reason)", text);
    }

    static string Here([CallerFilePath] string p = "") => p;
}
