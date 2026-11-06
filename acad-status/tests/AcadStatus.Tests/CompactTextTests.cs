using System;
using System.Collections.Generic;
using System.Linq;
using AcadStatus.Core;

namespace AcadStatus.Tests;

public class CompactTextTests
{
    static DateTime Now => TestData.Now;

    [Theory]
    [InlineData("Suite 3/7", "Suite 3/7")]
    [InlineData("Input: OFFSET", "Input: OFFSET")]
    [InlineData("  spaced   out \n text ", "spaced out text")]
    [InlineData("exactly eighteen!!", "exactly eighteen!!")]
    [InlineData("this one is nineteen", "this one is ninet…")]
    [InlineData("a very long summary text indeed", "a very long summa…")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    public void CapKeepsEighteenCharactersOrFewer(string? input, string expected)
    {
        var got = TextUtil.Cap(input, CompactText.MaxChars);
        Assert.Equal(expected, got);
        Assert.True(got.Length <= CompactText.MaxChars);
    }

    [Fact]
    public void CapOfALongTextNeverExceedsTheLimit()
    {
        foreach (var n in new[] { 1, 5, 17, 18, 19, 40, 500 })
            foreach (var max in new[] { 1, 2, 10, 18 })
                Assert.True(TextUtil.Cap(new string('x', n), max).Length <= max, $"n={n} max={max}");
        Assert.Equal("", TextUtil.Cap("abc", 0));
    }

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(59, "59s")]
    [InlineData(60, "1m00s")]
    [InlineData(125, "2m05s")]
    [InlineData(3599, "59m59s")]
    [InlineData(3600, "1h00m")]
    [InlineData(3720, "1h02m")]
    [InlineData(86400, "1d00h")]
    [InlineData(90000, "1d01h")]
    [InlineData(-5, "0s")]
    public void AgeIsShort(int seconds, string expected) => Assert.Equal(expected, CompactText.Age(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(9, "0:09")]
    [InlineData(151, "2:31")]
    [InlineData(3599, "59:59")]
    [InlineData(3723, "1:02:03")]
    [InlineData(-4, "0:00")]
    public void ClockIsMinutesAndSeconds(int seconds, string expected) => Assert.Equal(expected, CompactText.Clock(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void IdleShowsTheDotOnly()
    {
        var m = TestData.Merged("idle");
        Assert.Equal("", CompactText.TextOf(m, Now));
        Assert.Equal(Tone.Green, CompactText.ToneOf(m));
    }

    [Fact]
    public void TheWritersShortIsShown()
    {
        Assert.Equal("Suite 3/7", CompactText.TextOf(TestData.Merged("running", "Suite 3/7"), Now));
        Assert.Equal("Input: OFFSET", CompactText.TextOf(TestData.Merged("waiting-input", "Input: OFFSET", source: "acad"), Now));
    }

    [Fact]
    public void ALongShortIsCut()
    {
        var text = CompactText.TextOf(TestData.Merged("running", "Suite 3/7 smoke040 step 4"), Now);
        Assert.Equal(18, text.Length);
        Assert.EndsWith("…", text);
    }

    [Theory]
    [InlineData("needs-person", "Needs person")]
    [InlineData("waiting-input", "Input")]
    [InlineData("busy", "Busy")]
    [InlineData("error", "Error")]
    [InlineData("running", "Running")]
    [InlineData("stopped", "Stopped")]
    [InlineData("blocked", "Blocked")]
    public void WithoutAShortTheStateNamesItself(string state, string expected)
    {
        Assert.Equal(expected, CompactText.TextOf(TestData.Merged(state), Now));
    }

    [Fact]
    public void BlockedShowsHowLong()
    {
        var m = TestData.Merged("blocked", "Blocked", since: Now.AddSeconds(-151));
        Assert.Equal("Blocked 2:31", CompactText.TextOf(m, Now));
        Assert.Equal("Blocked 2:32", CompactText.TextOf(m, Now.AddSeconds(1)));
    }

    [Fact]
    public void BlockedWithoutASinceHasNoClock()
    {
        Assert.Equal("Blocked", CompactText.TextOf(TestData.Merged("blocked", "Blocked"), Now));
    }

    [Fact]
    public void BlockedWithALongLabelKeepsItsClock()
    {
        var m = TestData.Merged("blocked", "Plot dialog is open and waiting", since: Now.AddSeconds(-3725));
        var text = CompactText.TextOf(m, Now);
        Assert.True(text.Length <= CompactText.MaxChars, text);
        Assert.EndsWith(" 1:02:05", text);
    }

    [Fact]
    public void AnUnknownStateShowsItsText()
    {
        var m = TestData.Merged("compiling");
        Assert.Equal("compiling", CompactText.TextOf(m, Now));
        Assert.Equal(Tone.Grey, CompactText.ToneOf(m));
        Assert.Equal("a-very-long-unkno…", CompactText.TextOf(TestData.Merged("a-very-long-unknown-state"), Now));
    }

    [Fact]
    public void AnUnknownStateIsAlwaysCappedEvenWithAShort()
    {
        var text = CompactText.TextOf(TestData.Merged("a-very-long-unknown-state", "ignored short"), Now);
        Assert.True(text.Length <= CompactText.MaxChars);
    }

    [Fact]
    public void AStaleEntryShowsNoReply()
    {
        var e = TestData.Entry(state: "running", updated: Now.AddSeconds(-25), ttl: 5);
        var m = TestMerge.Merge(new[] { e }, Now, _ => true).Shown.Single();
        Assert.Equal("No reply 0:25", CompactText.TextOf(m, Now));
        Assert.Equal(Tone.Red, CompactText.ToneOf(m));
    }

    [Theory]
    [InlineData("idle", Tone.Green)]
    [InlineData("running", Tone.Blue)]
    [InlineData("waiting-input", Tone.Amber)]
    [InlineData("needs-person", Tone.Amber)]
    [InlineData("blocked", Tone.Red)]
    [InlineData("busy", Tone.Red)]
    [InlineData("error", Tone.Red)]
    [InlineData("stopped", Tone.Grey)]
    [InlineData("mystery", Tone.Grey)]
    public void ColoursFollowTheState(string state, Tone tone) => Assert.Equal(tone, CompactText.ToneOf(TestData.Merged(state)));

    [Fact]
    public void NoEntriesShowAGreyDot()
    {
        var v = CompactText.Build(TestMerge.Merge(Array.Empty<StatusEntry>(), Now, _ => true), Now);
        Assert.Equal(Tone.Grey, v.Tone);
        Assert.Equal("", v.Text);
        Assert.Contains("no status entries", v.Tooltip);
    }

    [Fact]
    public void TheTextNeverExceedsEighteenCharactersForAnyState()
    {
        var states = new[] { "blocked", "needs-person", "waiting-input", "busy", "error", "running", "idle", "stopped", "x", new string('q', 80) };
        var shorts = new string?[] { null, "a", "Suite 3/7", new string('s', 100), "Blocked by a very long dialog title" };
        foreach (var state in states)
            foreach (var s in shorts)
                foreach (var since in new DateTime?[] { null, Now.AddSeconds(-3), Now.AddHours(-30) })
                {
                    var text = CompactText.TextOf(TestData.Merged(state, s, since), Now);
                    Assert.True(text.Length <= CompactText.MaxChars, $"{state}/{s}/{since}: '{text}'");
                }
    }

    [Fact]
    public void TheTooltipHasOneLinePerEntry()
    {
        var entries = new[]
        {
            TestData.Entry("acad", 4812, state: "waiting-input", summary: "Waiting for input in OFFSET", file: "acad-4812.json"),
            TestData.Entry("retrace", 4812, state: "running", summary: "Suite 3/7: smoke040", file: "retrace-4812.json"),
        };
        var v = CompactText.Build(TestMerge.Merge(entries, Now, _ => true), Now);
        var lines = v.Tooltip.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Equal("acad-4812: waiting-input - Waiting for input in OFFSET", lines[0]);
        Assert.Equal("retrace-4812: running - Suite 3/7: smoke040", lines[1]);
        Assert.Equal(Tone.Amber, v.Tone);
    }

    [Fact]
    public void TheTooltipSaysHowLongBlockedAndWhenNoReply()
    {
        var blocked = TestData.Entry("acad", 1, state: "blocked", summary: "Blocked by dialog", since: Now.AddSeconds(-151), file: "acad-1.json");
        var stale = TestData.Entry("retrace", 2, state: "running", summary: "Suite", updated: Now.AddSeconds(-40), ttl: 5, file: "retrace-2.json");
        var v = CompactText.Build(TestMerge.Merge(new[] { blocked, stale }, Now, _ => true), Now);
        var lines = v.Tooltip.Split('\n');
        Assert.Equal("acad-1: blocked 2:31 - Blocked by dialog", lines[0]);
        Assert.Equal("retrace-2: no reply for 40s (last: running) - Suite", lines[1]);
    }

    [Fact]
    public void TheTooltipHasAtMostTenEntryLines()
    {
        var entries = Enumerable.Range(1, 14).Select(i => TestData.Entry("t" + i, i, state: "running", file: "t" + i + "-" + i + ".json")).ToArray();
        var lines = CompactText.Build(TestMerge.Merge(entries, Now, _ => true), Now).Tooltip.Split('\n');
        Assert.Equal(11, lines.Length);
        Assert.Equal("... and 4 more", lines[10]);
    }

    [Fact]
    public void ThePaneShowsTheTopEntry()
    {
        var entries = new[]
        {
            TestData.Entry("acad", 1, state: "idle", file: "acad-1.json"),
            TestData.Entry("retrace", 1, state: "running", @short: "Suite 3/7", file: "retrace-1.json"),
        };
        var v = CompactText.Build(TestMerge.Merge(entries, Now, _ => true), Now);
        Assert.Equal("Suite 3/7", v.Text);
        Assert.Equal(Tone.Blue, v.Tone);
    }

    [Fact]
    public void AStaleEntryOfADeadProcessDoesNotAppear()
    {
        var entries = new[]
        {
            TestData.Entry("acad", 1, state: "idle", file: "acad-1.json"),
            TestData.Entry("retrace", 2, state: "blocked", updated: Now.AddMinutes(-30), ttl: 15, file: "retrace-2.json"),
        };
        var v = CompactText.Build(TestMerge.Merge(entries, Now, p => p == 1), Now);
        Assert.Equal(Tone.Green, v.Tone);
        Assert.Equal("", v.Text);
        Assert.DoesNotContain("retrace", v.Tooltip);
    }
}
