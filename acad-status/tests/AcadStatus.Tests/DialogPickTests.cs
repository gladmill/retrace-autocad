using System;
using System.Collections.Generic;
using System.Linq;
using AcadStatus.Core;

namespace AcadStatus.Tests;

public class DialogPickTests
{
    const long Frame = 100;

    static WindowInfo W(long handle, string cls = "#32770", bool visible = true, bool enabled = true, bool captioned = true, bool tool = false,
        long owner = Frame, params string[] children) =>
        new(handle, cls, visible, enabled, captioned, tool, owner, children);

    static long? Pick(IEnumerable<WindowInfo> windows, bool modal, params long[] preferred) =>
        DialogPick.Pick(windows, modal, Frame, preferred)?.Handle;

    // ---- the message box rule

    [Theory]
    [InlineData(new[] { "Static", "Button" }, true)]
    [InlineData(new[] { "Static", "Static", "Button", "Button" }, true)]
    [InlineData(new[] { "Button" }, true)]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "Static" }, false)]
    [InlineData(new[] { "Static", "Button", "Edit" }, false)]
    [InlineData(new[] { "Static", "Button", "SysListView32" }, false)]
    [InlineData(new[] { "DirectUIHWND", "Button" }, false)]
    public void AMessageBoxHasOnlyTextsAndButtonsAndAButton(string[] children, bool expected) =>
        Assert.Equal(expected, DialogPick.IsMessageBox(children));

    // ---- who can be the dialog

    [Fact]
    public void WhileModalACaptionedDialogOfTheUsualKindsCounts()
    {
        Assert.True(DialogPick.IsCandidate(W(1), true));
        Assert.True(DialogPick.IsCandidate(W(1, "WindowsForms10.Window.8.app.0.141b42a_r6_ad1"), true));
        Assert.True(DialogPick.IsCandidate(W(1, "HwndWrapper[acad.exe;;1234]"), true));
    }

    [Theory]
    [InlineData("tooltips_class32")]
    [InlineData("#32768")]
    [InlineData("IME")]
    [InlineData("MSCTFIME UI")]
    public void NoiseWindowsNeverCount(string cls)
    {
        Assert.False(DialogPick.IsCandidate(W(1, cls), true));
        Assert.False(DialogPick.IsCandidate(W(1, cls, children: new[] { "Button" }), false));
    }

    [Fact]
    public void WhileModalToolWindowsUncaptionedAndInvisibleOnesDoNotCount()
    {
        Assert.False(DialogPick.IsCandidate(W(1, tool: true), true));        // a floating palette
        Assert.False(DialogPick.IsCandidate(W(1, captioned: false), true));
        Assert.False(DialogPick.IsCandidate(W(1, visible: false), true));
        Assert.False(DialogPick.IsCandidate(W(1, enabled: false), true));    // under another dialog
        Assert.False(DialogPick.IsCandidate(W(1, "Afx:00007FF6:b:10003:6:1234"), true));   // some other window class
    }

    [Fact]
    public void WhileNotModalOnlyAMessageBoxCounts()
    {
        Assert.True(DialogPick.IsCandidate(W(1, children: new[] { "Static", "Button" }), false));
        Assert.False(DialogPick.IsCandidate(W(1, children: new[] { "Static", "Button", "Edit" }), false));   // a modeless dialog with a field
        Assert.False(DialogPick.IsCandidate(W(1, "WindowsForms10.Window.8.app.0.141b42a_r6_ad1", children: new[] { "Button" }), false));
        Assert.False(DialogPick.IsCandidate(W(1), false));   // a #32770 whose children were not asked for
    }

    // ---- which one is picked

    [Fact]
    public void NothingIsNull()
    {
        Assert.Null(Pick(Array.Empty<WindowInfo>(), true));
        Assert.Null(Pick(new[] { W(1, tool: true) }, true));
    }

    [Fact]
    public void TheMainFrameIsNeverThePick()
    {
        Assert.Null(Pick(new[] { W(Frame, owner: 0) }, true));
    }

    [Fact]
    public void TheRealModalBeatsAModelessWindowInFrontOfIt()
    {
        // front to back: a modeless captioned window first, the modal dialog behind it. The last active popup is the modal.
        var windows = new[] { W(7, "#32770", owner: 0), W(42, "#32770", owner: Frame) };
        Assert.Equal(42, Pick(windows, true, 42));
    }

    [Fact]
    public void ThePreferredHandlesAreTriedInOrder()
    {
        var windows = new[] { W(7), W(8), W(9) };
        Assert.Equal(9, Pick(windows, true, 9, 8));
        Assert.Equal(8, Pick(windows, true, 5, 8, 9));   // 5 is not a candidate at all
    }

    [Fact]
    public void WithoutAPreferredCandidateTheFrontMostOwnedByTheMainFrameWins()
    {
        var windows = new[] { W(7, owner: 0), W(8, owner: Frame), W(9, owner: Frame) };
        Assert.Equal(8, Pick(windows, true));
        Assert.Equal(8, Pick(windows, true, 1234));   // the preferred window is not there
    }

    [Fact]
    public void FailingThatTheFrontMostCandidateWins()
    {
        var windows = new[] { W(7, owner: 0), W(8, owner: 0) };
        Assert.Equal(7, Pick(windows, true));
    }

    [Fact]
    public void ALastActivePopupThatIsNotACandidateFallsBackToTheScan()
    {
        // the last active popup is a floating palette (a tool window): the scan finds the dialog
        var windows = new[] { W(5, tool: true), W(8, owner: Frame) };
        Assert.Equal(8, Pick(windows, true, 5));
    }

    [Fact]
    public void WhenNotModalThePreferredHandlesAreIgnored()
    {
        var windows = new[] { W(7, children: new[] { "Static", "Button" }), W(8, children: new[] { "Button" }) };
        Assert.Equal(7, Pick(windows, false, 8));
    }

    [Fact]
    public void WhenNotModalAModelessDialogIsNeverPicked()
    {
        var windows = new[] { W(7, children: new[] { "Static", "Button", "Edit" }) };
        Assert.Null(Pick(windows, false));
    }

    [Fact]
    public void ADisabledWindowUnderTheRealModalIsNotPicked()
    {
        // dialog on dialog: the lower one is disabled while the upper is open
        var windows = new[] { W(8, enabled: true), W(7, enabled: false) };
        Assert.Equal(8, Pick(windows, true));
        Assert.Equal(8, Pick(windows, true, 7));
    }
}
