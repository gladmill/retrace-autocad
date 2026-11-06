using System;
using System.Linq;
using System.Text.Json;
using AcadStatus.Core;

namespace AcadStatus.Tests;

public class AcadStatusFileTests
{
    static DateTime Now => TestData.Now;

    static AcadStatusBuilder NewBuilder() => new("AcadStatus 0.1.0", 4812);

    static StatusEntry Parse(AcadStatusDoc doc)
    {
        var r = StatusParser.Parse("acad-4812.json", doc.Json);
        Assert.True(r.Ok, r.Error);
        return r.Entry!;
    }

    [Fact]
    public void TheFileIsNamedAfterThePid() => Assert.Equal("acad-4812.json", NewBuilder().FileName);

    [Fact]
    public void AnIdleFileHasAllRequiredFieldsAndTtl10()
    {
        var doc = NewBuilder().Build(TestData.Snap(doc: "fx-all.dwg", docs: 2), Now);
        var e = Parse(doc);
        Assert.Equal("acad", e.Source);
        Assert.Equal("AcadStatus 0.1.0", e.Writer);
        Assert.Equal(4812, e.Pid);
        Assert.Equal(10, e.TtlSec);
        Assert.Equal("idle", e.State);
        Assert.Equal(Now, e.Updated);
        Assert.Equal(Now, e.Since);
        Assert.Null(e.Short);
        Assert.Equal("Idle, fx-all.dwg", e.Summary);
        Assert.Equal("fx-all.dwg", e.Acad!.Doc);
        Assert.Equal(2, e.Acad.Docs);
        Assert.Equal(0, e.Acad.CmdActive);
        Assert.Null(e.Acad.Prompt);
        Assert.Null(e.Acad.Dialog);
        using var json = JsonDocument.Parse(doc.Json);
        Assert.Equal(1, json.RootElement.GetProperty("v").GetInt32());
    }

    [Fact]
    public void TheFileIsUtcIsoWithZ()
    {
        var doc = NewBuilder().Build(TestData.Snap(), new DateTime(2026, 11, 5, 10, 14, 3, 987, DateTimeKind.Utc));
        using var json = JsonDocument.Parse(doc.Json);
        Assert.Equal("2026-11-05T10:14:03Z", json.RootElement.GetProperty("updated").GetString());
        Assert.Equal("2026-11-05T10:14:03Z", json.RootElement.GetProperty("since").GetString());
    }

    [Fact]
    public void ThereIsNoBomAndOneTrailingNewline()
    {
        var doc = NewBuilder().Build(TestData.Snap(), Now);
        Assert.False(doc.Json.StartsWith('\uFEFF'));
        Assert.EndsWith("}\n", doc.Json);
    }

    [Fact]
    public void ApromptGivesWaitingInputWithTheCommandAndPrompt()
    {
        var doc = NewBuilder().Build(TestData.Snap(cmdActive: 1, cmdNames: "OFFSET", prompt: "Specify point on side to offset or [Exit/Multiple/Undo] <Exit>:"), Now);
        var e = Parse(doc);
        Assert.Equal("waiting-input", e.State);
        Assert.Equal("Input: OFFSET", e.Short);
        Assert.Equal("Waiting for input in OFFSET: Specify point on side to offset or [Exit/Multiple/Undo] <Exit>:", e.Summary);
        Assert.Equal("OFFSET", e.Acad!.CmdNames);
        Assert.Equal(1, e.Acad.CmdActive);
        Assert.Equal("Specify point on side to offset or [Exit/Multiple/Undo] <Exit>:", e.Acad.Prompt);
    }

    [Fact]
    public void APromptWithoutTextAndWithoutCommandStillWaits()
    {
        var doc = NewBuilder().Build(TestData.Snap(prompt: ""), Now);
        var e = Parse(doc);
        Assert.Equal("waiting-input", e.State);
        Assert.Equal("Input", e.Short);
        // CONTRACT.md: `prompt` is absent only when no prompt is pending; a pending prompt of unknown text is ""
        Assert.Equal("", e.Acad!.Prompt);
        Assert.Equal("", doc.Entry.Acad!.Prompt);
        using var json = JsonDocument.Parse(doc.Json);
        Assert.Equal("", json.RootElement.GetProperty("detail").GetProperty("prompt").GetString());
    }

    [Fact]
    public void NoPendingPromptMeansNoPromptField()
    {
        var doc = NewBuilder().Build(TestData.Snap(cmdActive: 1, cmdNames: "REGEN"), Now);
        using var json = JsonDocument.Parse(doc.Json);
        Assert.False(json.RootElement.GetProperty("detail").TryGetProperty("prompt", out _));
        Assert.Null(Parse(doc).Acad!.Prompt);
        Assert.Null(doc.Entry.Acad!.Prompt);
    }

    [Fact]
    public void APendingPromptWithNoTextIsNotTheSameContentAsNoPrompt()
    {
        var b = NewBuilder();
        var none = b.Build(TestData.Snap(cmdActive: 1, cmdNames: "LINE", prompt: null), Now);
        var pending = b.Build(TestData.Snap(cmdActive: 1, cmdNames: "LINE", prompt: ""), Now);
        Assert.NotEqual(none.ContentKey, pending.ContentKey);   // the file changes, so it is written
        Assert.NotEqual(pending.ContentKey, b.Build(TestData.Snap(cmdActive: 1, cmdNames: "LINE", prompt: "Specify first point:"), Now).ContentKey);
    }

    [Fact]
    public void TheDocHoldsItsValuesInOnePlace()
    {
        // State, Short, Summary and Since are the entry's: no second copy on the doc
        var members = typeof(AcadStatusDoc).GetProperties().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "ContentKey", "Entry", "Json" }, members);
    }

    [Fact]
    public void ARunningCommandHasItsNameInTheShort()
    {
        var e = Parse(NewBuilder().Build(TestData.Snap(cmdActive: 1, cmdNames: "ZOOM'PAN"), Now));
        Assert.Equal("running", e.State);
        Assert.Equal("Running: ZOOM", e.Short);
        Assert.Equal("Running ZOOM'PAN", e.Summary);
        var anon = Parse(NewBuilder().Build(TestData.Snap(cmdActive: 4), Now));
        Assert.Equal("Running", anon.Short);
    }

    [Fact]
    public void ALongCommandNameStaysInEighteenCharacters()
    {
        var e = Parse(NewBuilder().Build(TestData.Snap(cmdActive: 1, cmdNames: "AVERYLONGCOMMANDNAMEFROMALISP"), Now));
        Assert.True(e.Short!.Length <= 18, e.Short);
    }

    [Fact]
    public void ADialogIsDescribedWithItsTextsAndButtons()
    {
        var d = TestData.Dialog(texts: new[] { "status board test" }, buttons: new[] { "OK" });
        var since = Now.AddSeconds(-3);
        var e = Parse(NewBuilder().Build(TestData.Snap(dialog: d, dialogSince: since), Now));
        Assert.Equal("waiting-input", e.State);
        Assert.Equal("Dialog", e.Short);
        Assert.Equal("Dialog \"AutoCAD Message\" waits for an answer: status board test [OK]", e.Summary);
        var dlg = e.Acad!.Dialog!;
        Assert.Equal("AutoCAD Message", dlg.Title);
        Assert.Equal("#32770", dlg.Class);
        Assert.True(dlg.Modal);
        Assert.Equal(new[] { "status board test" }, dlg.Texts);
        Assert.Equal(new[] { "OK" }, dlg.Buttons);
        Assert.Equal(since, dlg.Since);
        Assert.Equal(since, e.Since);   // the state began when the dialog opened
    }

    [Fact]
    public void ABlockedFileSaysSoAndKeepsTheDialogStart()
    {
        var d = TestData.Dialog(texts: new[] { "status board test" });
        var since = Now.AddSeconds(-16);
        var e = Parse(NewBuilder().Build(TestData.Snap(dialog: d, dialogSince: since), Now));
        Assert.Equal("blocked", e.State);
        Assert.Equal("Blocked", e.Short);
        Assert.StartsWith("Blocked by dialog \"AutoCAD Message\": status board test", e.Summary);
        Assert.Equal(since, e.Since);
    }

    [Fact]
    public void ADialogWithoutTextsOrButtonsIsStillDescribed()
    {
        var d = TestData.Dialog(title: "Security - Unsigned Executable File", texts: Array.Empty<string>(), buttons: Array.Empty<string>());
        var e = Parse(NewBuilder().Build(TestData.Snap(dialog: d, dialogSince: Now), Now));
        Assert.Equal("Dialog \"Security - Unsigned Executable File\" waits for an answer", e.Summary);
        Assert.Empty(e.Acad!.Dialog!.Texts);
        Assert.Empty(e.Acad.Dialog.Buttons);
    }

    [Fact]
    public void QuotesBackslashesNewlinesAndUnicodeInATextSurviveTheRoundTrip()
    {
        var nasty = "He said \"stop\" \\ at C:\\temp\\x.dwg\nsecond line\ttab é ü € 日本語 <tag> & more";
        var d = TestData.Dialog(title: "T \"q\"", texts: new[] { nasty }, buttons: new[] { "&Yes", "No" });
        var doc = NewBuilder().Build(TestData.Snap(dialog: d, dialogSince: Now, prompt: nasty), Now);
        var e = Parse(doc);
        Assert.Equal(nasty, e.Acad!.Dialog!.Texts.Single());
        Assert.Equal("T \"q\"", e.Acad.Dialog.Title);
        Assert.Equal(new[] { "&Yes", "No" }, e.Acad.Dialog.Buttons);
        Assert.True(e.Summary.Length <= 200);
        Assert.DoesNotContain('\n', e.Summary);
    }

    [Fact]
    public void TheSummaryIsAtMostTwoHundredCharacters()
    {
        var huge = new string('w', 3000);
        foreach (var s in new[]
        {
            TestData.Snap(prompt: huge, cmdNames: huge),
            TestData.Snap(dialog: TestData.Dialog(title: huge, texts: new[] { huge }, buttons: new[] { huge, huge }), dialogSince: Now),
            TestData.Snap(cmdActive: 1, cmdNames: huge),
            TestData.Snap(doc: huge),
        })
        {
            var doc = NewBuilder().Build(s, Now);
            Assert.True(doc.Entry.Summary.Length <= 200, doc.Entry.Summary.Length.ToString());
            Assert.True((doc.Entry.Short ?? "").Length <= 18);
            Assert.True(Parse(doc).Summary.Length <= 200);
        }
    }

    [Fact]
    public void TheContentKeyIgnoresUpdatedButNotContent()
    {
        var b = NewBuilder();
        var a1 = b.Build(TestData.Snap(), Now);
        var a2 = b.Build(TestData.Snap(), Now.AddSeconds(2));
        Assert.NotEqual(a1.Json, a2.Json);
        Assert.Equal(a1.ContentKey, a2.ContentKey);
        var c = b.Build(TestData.Snap(cmdActive: 1, cmdNames: "LINE"), Now.AddSeconds(3));
        Assert.NotEqual(a2.ContentKey, c.ContentKey);
        var d = b.Build(TestData.Snap(doc: "other.dwg"), Now.AddSeconds(4));
        Assert.NotEqual(a1.ContentKey, d.ContentKey);
    }

    [Fact]
    public void ABlockedDialogDoesNotChangeTheContentEverySecond()
    {
        var b = NewBuilder();
        var d = TestData.Dialog();
        var since = Now;
        var keys = Enumerable.Range(20, 10).Select(i => b.Build(TestData.Snap(dialog: d, dialogSince: since), Now.AddSeconds(i)).ContentKey).Distinct().Count();
        Assert.Equal(1, keys);
    }

    [Fact]
    public void SinceMovesOnlyWhenTheStateDoes()
    {
        var b = NewBuilder();
        Assert.Equal(Now, b.Build(TestData.Snap(), Now).Entry.Since);
        Assert.Equal(Now, b.Build(TestData.Snap(), Now.AddSeconds(10)).Entry.Since);
        Assert.Equal(Now.AddSeconds(11), b.Build(TestData.Snap(cmdActive: 1, cmdNames: "LINE"), Now.AddSeconds(11)).Entry.Since);
        Assert.Equal(Now.AddSeconds(11), b.Build(TestData.Snap(cmdActive: 1, cmdNames: "LINE"), Now.AddSeconds(30)).Entry.Since);
        Assert.Equal(Now.AddSeconds(31), b.Build(TestData.Snap(), Now.AddSeconds(31)).Entry.Since);
    }

    [Fact]
    public void ASecondDialogStartsANewSince()
    {
        var b = NewBuilder();
        var first = b.Build(TestData.Snap(dialog: TestData.Dialog(handle: 1), dialogSince: Now), Now.AddSeconds(1));
        Assert.Equal(Now, first.Entry.Since);
        var second = b.Build(TestData.Snap(dialog: TestData.Dialog(handle: 2), dialogSince: Now.AddSeconds(5)), Now.AddSeconds(5));
        Assert.Equal(Now.AddSeconds(5), second.Entry.Since);
    }

    [Fact]
    public void TheStoppedFileSaysStopped()
    {
        var b = NewBuilder();
        b.Build(TestData.Snap(cmdActive: 1), Now);
        var doc = b.BuildStopped(Now.AddSeconds(9));
        var e = Parse(doc);
        Assert.Equal("stopped", e.State);
        Assert.Equal(Now.AddSeconds(9), e.Updated);
        Assert.Equal(10, e.TtlSec);
        Assert.Null(e.Acad);
        Assert.Equal("AutoCAD closed", e.Summary);
    }

    [Fact]
    public void ANoDocumentSnapshotLeavesDocOut()
    {
        var e = Parse(NewBuilder().Build(TestData.Snap(doc: null, docs: 0), Now));
        Assert.Null(e.Acad!.Doc);
        Assert.Equal(0, e.Acad.Docs);
        Assert.Equal("Idle", e.Summary);
    }
}
