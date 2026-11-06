using System;
using AcadStatus.Core;

namespace AcadStatus.Tests;

/// <summary>The prompt state of one drawing: a frame per nesting level.</summary>
public class PromptStackTests
{
    [Fact]
    public void ACommandAtAPromptShowsItsPrompt()
    {
        var s = new PromptStack();
        s.EnterCommand();
        s.Push("Specify first point:");
        Assert.True(s.IsOpen);
        Assert.Equal("Specify first point:", s.ForSnapshot());
        Assert.True(s.HasCommand);
        Assert.False(s.HasLisp);
    }

    [Fact]
    public void NothingOpenIsNull()
    {
        var s = new PromptStack();
        Assert.Null(s.ForSnapshot());
        s.EnterCommand();
        Assert.Null(s.ForSnapshot());
    }

    [Fact]
    public void ATransparentCommandAtAPromptKeepsTheOuterPromptWhenItEnds()
    {
        var s = new PromptStack();
        s.EnterCommand();                        // LINE
        s.Push("Specify first point:");
        s.EnterCommand();                        // 'ZOOM typed at the prompt
        Assert.Equal("Specify first point:", s.ForSnapshot());   // ZOOM itself has not asked anything yet
        s.Push("Specify corner of window:");
        Assert.Equal("Specify corner of window:", s.ForSnapshot());
        s.Pop();
        s.Leave(FrameKind.Command);              // ZOOM ended
        Assert.Equal("Specify first point:", s.ForSnapshot());
        Assert.Equal(1, s.Depth);
    }

    [Fact]
    public void AnInnerCommandThatNeverPromptsLeavesTheOuterPromptAlone()
    {
        var s = new PromptStack();
        s.EnterCommand();
        s.Push("Specify point on side to offset:");
        s.EnterCommand();                        // our own ACADSTATUS
        s.Leave(FrameKind.Command);
        Assert.Equal("Specify point on side to offset:", s.ForSnapshot());
    }

    [Fact]
    public void ALispExpressionAtAPromptKeepsTheOuterPrompt()
    {
        var s = new PromptStack();
        s.EnterCommand();
        s.Push("Specify first point:");
        s.EnterLisp();                           // (getvar "cdate") typed at it
        Assert.True(s.HasLisp);
        s.Leave(FrameKind.Lisp);
        Assert.False(s.HasLisp);
        Assert.Equal("Specify first point:", s.ForSnapshot());
    }

    [Fact]
    public void ALispGetPointInsideACommandCallIsSeenAndGoneAgain()
    {
        var s = new PromptStack();
        s.EnterLisp();                           // (defun c:foo () ... (getpoint "Pick: ") ...)
        s.Push("Pick: ");
        Assert.Equal("Pick:", s.ForSnapshot());
        s.Pop();
        s.Leave(FrameKind.Lisp);
        Assert.Null(s.ForSnapshot());
        Assert.Equal(0, s.Depth);
    }

    [Fact]
    public void ACommandThatEndsWithoutAnswersLeavesNoPrompt()
    {
        var s = new PromptStack();
        s.EnterCommand();
        s.Push("Specify first point:");
        s.Leave(FrameKind.Command);   // cancelled: no Prompted event
        Assert.Null(s.ForSnapshot());
    }

    [Fact]
    public void AnEndWithNoFrameOfItsKindChangesNothing()
    {
        var s = new PromptStack();
        s.EnterCommand();
        s.Push("Specify first point:");
        s.Leave(FrameKind.Lisp);      // a LISP function that began before we looked
        Assert.Equal("Specify first point:", s.ForSnapshot());
        Assert.Equal(1, s.Depth);
    }

    [Fact]
    public void ALispFunctionThatStartedACommandAndReturnedLeavesTheCommandWaiting()
    {
        // (defun c:foo () (command "_.LINE")) : the function returns while LINE waits at its first point
        var s = new PromptStack();
        s.EnterLisp();
        s.EnterCommand();
        s.Push("Specify first point:");
        s.Leave(FrameKind.Lisp);      // the LISP function ends; the command it started does not
        Assert.Equal(1, s.Depth);
        Assert.True(s.HasCommand);
        Assert.False(s.HasLisp);
        Assert.Equal("Specify first point:", s.ForSnapshot());
        s.Leave(FrameKind.Command);   // ... and ends later on its own
        Assert.Equal(0, s.Depth);
        Assert.Null(s.ForSnapshot());
    }

    [Fact]
    public void ACommandThatEndsLeavesALispFunctionAboveItAlone()
    {
        var s = new PromptStack();
        s.EnterCommand();
        s.EnterLisp();
        s.Push("Pick: ");
        s.Leave(FrameKind.Command);
        Assert.True(s.HasLisp);
        Assert.False(s.HasCommand);
        Assert.Equal("Pick:", s.ForSnapshot());
    }

    [Fact]
    public void OnlyTheInnermostFrameOfTheEndingKindGoes()
    {
        var s = new PromptStack();
        s.EnterCommand();             // A
        s.EnterLisp();
        s.EnterCommand();             // B, inside the LISP function
        s.Push("B prompt:");
        s.Leave(FrameKind.Command);   // B ends
        Assert.Equal(2, s.Depth);     // A and the LISP function stay
        Assert.True(s.HasLisp);
        Assert.True(s.HasCommand);
        Assert.Null(s.ForSnapshot());
    }

    [Fact]
    public void ADeadFrameLeftByAMissedEndIsCleanedUpWhenNothingIsActive()
    {
        var s = new PromptStack();
        s.EnterLisp();
        s.EnterCommand();             // end events we missed
        s.Push("Specify first point:");
        s.Leave(FrameKind.Lisp);
        Assert.Equal(1, s.Depth);
        Assert.True(s.DropStale(0));  // CMDACTIVE 0: nothing runs, so the command frame goes
        Assert.Equal(0, s.Depth);
        Assert.Null(s.ForSnapshot());
    }

    [Fact]
    public void WhenAnInnerFrameLeavesTheFrameBelowItIsAskedAgain()
    {
        var s = new PromptStack();
        s.EnterCommand();
        s.Push("Specify first point:");
        s.EnterLisp();                // typed at the prompt
        s.Leave(FrameKind.Lisp);
        s.Push("Specify first point:");   // asked again: the same prompt
        Assert.Equal("Specify first point:", s.ForSnapshot());
        s.Pop();
        Assert.Null(s.ForSnapshot());
    }

    [Fact]
    public void APromptWithNoFrameGetsARootFrame()
    {
        var s = new PromptStack();
        s.Push("Select object:");     // hooked while a command was already running
        Assert.Equal("Select object:", s.ForSnapshot());
        Assert.Equal(1, s.Depth);
        s.Pop();
        Assert.Null(s.ForSnapshot());
    }

    [Fact]
    public void NestedPromptsOfOneCommandNeedBothAnswered()
    {
        var s = new PromptStack();
        s.EnterCommand();
        s.Push("Specify opposite corner:");
        s.Push("Specify point:");
        Assert.Equal("Specify point:", s.ForSnapshot());
        s.Pop();
        Assert.Equal("Specify opposite corner:", s.ForSnapshot());
        s.Pop();
        Assert.Null(s.ForSnapshot());
    }

    [Fact]
    public void APromptWithoutTextIsOpenWithNoText()
    {
        var s = new PromptStack();
        s.EnterCommand();
        s.Push(null);
        Assert.True(s.IsOpen);
        Assert.Equal("", s.ForSnapshot());
        s.EnterCommand();
        s.Push("Inner:");
        Assert.Equal("Inner:", s.ForSnapshot());
    }

    [Fact]
    public void WhenTheInnerCommandEndsTheOuterPromptIsAskedAgainWithoutNesting()
    {
        var s = new PromptStack();
        s.EnterCommand();
        s.Push("Specify first point:");
        s.EnterCommand();             // 'ZOOM
        s.Leave(FrameKind.Command);
        s.Push("Specify first point:");   // AutoCAD asks LINE's prompt again
        Assert.Equal("Specify first point:", s.ForSnapshot());
        s.Pop();                           // the user answers it once
        Assert.Null(s.ForSnapshot());      // no ghost of the first asking is left behind
    }

    [Fact]
    public void AnOuterPromptThatIsNotAskedAgainIsStillThere()
    {
        var s = new PromptStack();
        s.EnterCommand();
        s.Push("Specify first point:");
        s.EnterCommand();
        s.Leave(FrameKind.Command);
        Assert.Equal("Specify first point:", s.ForSnapshot());
        s.Pop();                           // answered
        Assert.Null(s.ForSnapshot());
    }

    [Fact]
    public void TheFramesAreBounded()
    {
        var s = new PromptStack();
        for (int i = 0; i < 1000; i++) s.EnterCommand();
        Assert.Equal(PromptStack.MaxFrames, s.Depth);
    }

    // ---- ghosts

    [Fact]
    public void WithNoCommandActiveAndNoLispAnythingPendingIsAGhost()
    {
        var s = new PromptStack();
        s.EnterCommand();
        s.Push("Specify first point:");
        Assert.True(s.DropStale(0));
        Assert.Null(s.ForSnapshot());
        Assert.Equal(0, s.Depth);
    }

    [Fact]
    public void AnActiveCommandMeansNothingIsDropped()
    {
        var s = new PromptStack();
        s.EnterCommand();
        s.Push("Specify first point:");
        Assert.False(s.DropStale(1));
        Assert.False(s.DropStale(3));     // a transparent command is active too
        Assert.Equal("Specify first point:", s.ForSnapshot());
    }

    [Fact]
    public void ARunningLispFunctionMeansNothingIsDropped()
    {
        var s = new PromptStack();
        s.EnterLisp();
        s.Push("Pick: ");
        Assert.False(s.DropStale(32));   // CMDACTIVE bit 32: AutoLISP is active
        Assert.Equal("Pick:", s.ForSnapshot());
    }

    [Fact]
    public void ALeftOverLispFrameDoesNotDisableTheGhostCheckForGood()
    {
        var s = new PromptStack();
        s.EnterLisp();              // a LISP function whose end event we missed
        s.EnterCommand();
        s.Push("Specify first point:");
        Assert.True(s.HasLisp);
        Assert.True(s.DropStale(0));   // CMDACTIVE is 0: nothing runs at all, the Lisp frame goes too
        Assert.False(s.HasLisp);
        Assert.Equal(0, s.Depth);
        Assert.Null(s.ForSnapshot());
        // and the next command starts from a clean stack
        s.EnterCommand();
        s.Push("Select objects:");
        Assert.Equal("Select objects:", s.ForSnapshot());
        Assert.False(s.HasLisp);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(32)]
    [InlineData(33)]
    public void AnyActiveBitKeepsEveryFrame(int cmdActive)
    {
        var s = new PromptStack();
        s.EnterLisp();
        s.EnterCommand();
        s.Push("Specify first point:");
        Assert.False(s.DropStale(cmdActive));
        Assert.Equal(2, s.Depth);
    }

    [Fact]
    public void DroppingNothingReportsNothing()
    {
        Assert.False(new PromptStack().DropStale(0));
    }

    // ---- per drawing

    [Fact]
    public void EachDrawingHasItsOwnStack()
    {
        var a = new PromptStack();
        var b = new PromptStack();
        a.EnterCommand();
        a.Push("Specify first point:");
        b.EnterCommand();
        b.Push("Select objects:");
        // drawing b closes, or its command ends: a is untouched
        b.Leave(FrameKind.Command);
        b.Clear();
        Assert.Equal("Specify first point:", a.ForSnapshot());
        Assert.Null(b.ForSnapshot());
        // a stale check against the active drawing's CMDACTIVE only ever touches that drawing's stack
        Assert.True(b.DropStale(0) == false);
        Assert.Equal("Specify first point:", a.ForSnapshot());
    }
}

public class PromptTrackerRepromptTests
{
    [Fact]
    public void ExpectRepromptReplacesTheTopPromptOnTheNextPush()
    {
        var t = new PromptTracker();
        t.Push("A");
        t.ExpectReprompt();
        t.Push("A again");
        Assert.Equal(1, t.Depth);
        Assert.Equal("A again", t.Current);
    }

    [Fact]
    public void ExpectRepromptDoesNothingWhenNoPromptIsOpen()
    {
        var t = new PromptTracker();
        t.ExpectReprompt();
        t.Push("A");
        t.Push("B");
        Assert.Equal(2, t.Depth);
    }

    [Fact]
    public void ThePromptIsExpectedAgainOnlyOnce()
    {
        var t = new PromptTracker();
        t.Push("A");
        t.ExpectReprompt();
        t.Push("A");
        t.Push("B");   // a real nested prompt after that
        Assert.Equal(2, t.Depth);
    }

    [Fact]
    public void AnAnswerCancelsTheExpectation()
    {
        var t = new PromptTracker();
        t.Push("A");
        t.ExpectReprompt();
        t.Pop();
        t.Push("B");
        t.Push("C");
        Assert.Equal(2, t.Depth);
    }
}
