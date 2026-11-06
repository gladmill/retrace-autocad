using System;
using AcadStatus.Core;

namespace AcadStatus.Tests;

public class StateRulesTests
{
    static DateTime Now => TestData.Now;

    [Fact]
    public void NothingIsIdle() => Assert.Equal("idle", StateRules.Decide(TestData.Snap(), Now));

    [Fact]
    public void ACommandWithoutAPromptIsRunning() =>
        Assert.Equal("running", StateRules.Decide(TestData.Snap(cmdActive: 1, cmdNames: "REGEN"), Now));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(32)]
    [InlineData(33)]
    public void AnyNonZeroCmdActiveIsRunning(int bits) =>
        Assert.Equal("running", StateRules.Decide(TestData.Snap(cmdActive: bits), Now));

    [Fact]
    public void APromptIsWaitingInput()
    {
        Assert.Equal("waiting-input", StateRules.Decide(TestData.Snap(cmdActive: 1, cmdNames: "OFFSET", prompt: "Specify point:"), Now));
    }

    [Fact]
    public void APendingPromptWithoutTextIsStillWaitingInput() =>
        Assert.Equal("waiting-input", StateRules.Decide(TestData.Snap(prompt: ""), Now));

    [Fact]
    public void APromptWithNoCommandActiveIsStillWaitingInput() =>
        Assert.Equal("waiting-input", StateRules.Decide(TestData.Snap(cmdActive: 0, prompt: "Select a point:"), Now));

    [Fact]
    public void ANewDialogIsWaitingInput()
    {
        var s = TestData.Snap(dialog: TestData.Dialog(), dialogSince: Now.AddSeconds(-3));
        Assert.Equal("waiting-input", StateRules.Decide(s, Now));
    }

    [Theory]
    [InlineData(0, "waiting-input")]
    [InlineData(14, "waiting-input")]
    [InlineData(14.999, "waiting-input")]
    [InlineData(15, "blocked")]
    [InlineData(15.001, "blocked")]
    [InlineData(600, "blocked")]
    public void ADialogIsBlockedFromFifteenSeconds(double openSec, string expected)
    {
        var s = TestData.Snap(dialog: TestData.Dialog(), dialogSince: Now.AddSeconds(-openSec));
        Assert.Equal(expected, StateRules.Decide(s, Now));
    }

    [Fact]
    public void ADialogOutranksAPromptAndARunningCommand()
    {
        var old = TestData.Snap(cmdActive: 1, cmdNames: "LINE", prompt: "Specify first point:", dialog: TestData.Dialog(), dialogSince: Now.AddSeconds(-20));
        Assert.Equal("blocked", StateRules.Decide(old, Now));
        var fresh = TestData.Snap(cmdActive: 1, cmdNames: "LINE", prompt: "Specify first point:", dialog: TestData.Dialog(), dialogSince: Now.AddSeconds(-1));
        Assert.Equal("waiting-input", StateRules.Decide(fresh, Now));
    }

    [Fact]
    public void ADialogWithoutAKnownStartIsNew() =>
        Assert.Equal("waiting-input", StateRules.Decide(TestData.Snap(dialog: TestData.Dialog(), dialogSince: null), Now));

    [Theory]
    [InlineData("OFFSET", "OFFSET")]
    [InlineData("ZOOM'PAN", "ZOOM")]
    [InlineData("LINE LINE", "LINE")]
    [InlineData("  ", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void FirstCommandOfCmdNames(string? names, string expected) => Assert.Equal(expected, StateRules.FirstCommand(names));

    // ---- DialogClock

    [Fact]
    public void ADialogClockRemembersWhenADialogWasFirstSeen()
    {
        var c = new DialogClock();
        var d = TestData.Dialog(handle: 1);
        Assert.Null(c.Observe(null, Now));
        Assert.Equal(Now, c.Observe(d, Now));
        Assert.Equal(Now, c.Observe(d, Now.AddSeconds(5)));
        Assert.Equal(Now, c.Observe(d, Now.AddSeconds(20)));
    }

    [Fact]
    public void ADialogClockStartsOverForAnotherWindowOrAfterNone()
    {
        var c = new DialogClock();
        c.Observe(TestData.Dialog(handle: 1), Now);
        Assert.Equal(Now.AddSeconds(5), c.Observe(TestData.Dialog(handle: 2), Now.AddSeconds(5)));
        Assert.Equal(Now.AddSeconds(5), c.Observe(TestData.Dialog(handle: 2), Now.AddSeconds(6)));
        Assert.Null(c.Observe(null, Now.AddSeconds(8)));
        Assert.Equal(Now.AddSeconds(9), c.Observe(TestData.Dialog(handle: 2), Now.AddSeconds(9)));
    }

    [Fact]
    public void ADialogWhoseCaptionChangesIsStillTheSameDialogAndBecomesBlocked()
    {
        // a progress dialog whose title counts: "Plotting 12%", "Plotting 13%", ... for a minute
        var c = new DialogClock();
        var b = new AcadStatusBuilder("w", 1);
        string state = "";
        for (int s = 0; s <= 20; s++)
        {
            var now = Now.AddSeconds(s);
            var d = TestData.Dialog(handle: 77, title: "Plotting " + s + "%");
            state = b.Build(TestData.Snap(dialog: d, dialogSince: c.Observe(d, now)), now).Entry.State;
        }
        Assert.Equal("blocked", state);
        Assert.Equal(Now, c.Observe(TestData.Dialog(handle: 77, title: "Plotting 99%"), Now.AddSeconds(30)));
    }

    // ---- PromptTracker

    [Fact]
    public void ATrackerKnowsWhenAPromptIsOpen()
    {
        var t = new PromptTracker();
        Assert.False(t.IsOpen);
        t.Push("\nSpecify first point: ");
        Assert.True(t.IsOpen);
        Assert.Equal("Specify first point:", t.Current);
        t.Pop();
        Assert.False(t.IsOpen);
        Assert.Null(t.Current);
    }

    [Fact]
    public void ANestedPromptShowsTheInnermostTextAndNeedsBothPopped()
    {
        var t = new PromptTracker();
        t.Push("Specify opposite corner:");
        t.Push("Specify point:");
        Assert.Equal(2, t.Depth);
        Assert.Equal("Specify point:", t.Current);
        t.Pop();
        Assert.Equal("Specify opposite corner:", t.Current);
        Assert.True(t.IsOpen);
        t.Pop();
        Assert.False(t.IsOpen);
    }

    [Fact]
    public void APromptWithoutTextIsOpenButHasNoText()
    {
        var t = new PromptTracker();
        t.Push(null);
        Assert.True(t.IsOpen);
        Assert.Null(t.Current);
        t.Push("Real text");
        t.Push("   ");
        Assert.Equal("Real text", t.Current);
    }

    [Fact]
    public void AnExtraPopDoesNothingAndClearEmpties()
    {
        var t = new PromptTracker();
        t.Pop();
        t.Pop();
        Assert.False(t.IsOpen);
        t.Push("a");
        t.Push("b");
        t.Clear();
        Assert.False(t.IsOpen);
        Assert.Equal(0, t.Depth);
    }

    [Fact]
    public void ADepthLimitStopsRunawayPushes()
    {
        var t = new PromptTracker();
        for (int i = 0; i < 1000; i++) t.Push("p" + i);
        Assert.Equal(PromptTracker.MaxDepth, t.Depth);
        Assert.Equal("p999", t.Current);
    }

    [Fact]
    public void APromptIsOneLineOfAtMostTwoHundredCharacters()
    {
        var t = new PromptTracker();
        t.Push("line one\nline two " + new string('z', 400));
        Assert.StartsWith("line one line two", t.Current);
        Assert.Equal(PromptTracker.MaxText, t.Current!.Length);
    }
}
