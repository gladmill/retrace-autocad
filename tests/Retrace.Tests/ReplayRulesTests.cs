using Retrace.Core;

namespace Retrace.Tests;

public class ReplayRulesTests
{
    static List<Step> Steps(params string[] kinds) => kinds.Select(k => new Step { Kind = k }).ToList();

    [Fact]
    public void Nothing_is_left_when_the_steps_are_used_up_or_only_dialogs_remain()
    {
        var s = Steps("open", "mark", "command", "dialog");
        Assert.True(ReplayRules.NothingLeftToRun(s, 4));    // all done
        Assert.True(ReplayRules.NothingLeftToRun(s, 3));    // only the "Save changes?" dialog of the CLOSE is left
        Assert.False(ReplayRules.NothingLeftToRun(s, 2));   // the command is still to run
        Assert.True(ReplayRules.NothingLeftToRun(Steps("dialog", "dialog"), 0));
        Assert.True(ReplayRules.NothingLeftToRun(Steps(), 0));
    }

    [Fact]
    public void A_dialog_followed_by_a_command_is_not_nothing()
    {
        Assert.False(ReplayRules.NothingLeftToRun(Steps("dialog", "command"), 0));
    }
}
