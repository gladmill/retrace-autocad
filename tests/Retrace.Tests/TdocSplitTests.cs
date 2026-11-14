using System.Text.Json;
using Retrace.Core;

namespace Retrace.Tests;

/// <summary>Tdoc: a command's prompts before and after its dialog are different steps, and an arrow points at a control or at nothing.</summary>
public class TdocSplitTests
{
    static List<Step> Build(params string[] lines) => Plan.Build(lines.Select(l => JsonDocument.Parse(l).RootElement.Clone()));

    // a ribbon click starts INSERTPART, which opens a dialog; then two prompts
    static readonly string[] Session =
    [
        """{"t":1000,"n":1,"type":"ribbon","id":"PARTADD","text":"Add part","macro":"^C^C_INSERTPART ","tabId":"HOME","tab":"Home","panel":"Parts"}""",
        """{"t":1010,"n":2,"type":"cmd_start","command":"INSERTPART","origin":"ribbon"}""",
        """{"t":1020,"n":3,"type":"modal_enter","title":"Parts"}""",
        """{"t":2000,"n":4,"type":"dialog_values","for":3,"title":"Parts","source":"win32","read":1,"closedWith":"ok","fields":[]}""",
        """{"t":2500,"n":5,"type":"prompt","kind":"point","input":true,"message":"Insertion point:","status":"OK","value":[0,0,0]}""",
        """{"t":2600,"n":6,"type":"prompt","kind":"angle","input":true,"message":"Rotation angle <0>:","status":"None"}""",
        """{"t":2700,"n":7,"type":"cmd_end","command":"INSERTPART"}""",
    ];

    [Fact]
    public void Prompts_after_a_dialog_are_a_step_after_the_dialog()
    {
        var entries = TdocSplit.Entries(Build(Session));
        Assert.Equal(new[] { "ribbon", "dialog", "command" }, entries.Select(TdocSplit.Kind));
        Assert.Equal(new[] { false, false, true }, entries.Select(e => e.After));
        Assert.Equal(new[] { "Click Add part on the Home tab." }, TdocSplit.Instructions(entries[0]));
        Assert.Equal(new[] { "At the prompt 'Insertion point', type 0,0.", "At the prompt 'Rotation angle <0>', press Enter." }, TdocSplit.Instructions(entries[2]));
        Assert.Equal("INSERTPART: answer the prompts", TdocSplit.Title(entries[2]));
        Assert.Equal(1, entries[2].Index);   // follows the dialog step
    }

    [Fact]
    public void Prompts_before_and_after_the_dialog_are_split_at_the_dialog()
    {
        var lines = Session.ToList();
        lines.Insert(2, """{"t":1015,"n":20,"type":"prompt","kind":"string","input":true,"message":"Part name:","status":"OK","value":"A"}""");
        var entries = TdocSplit.Entries(Build(lines.ToArray()));
        Assert.Equal(3, entries.Count);
        Assert.Equal(new[] { "Click Add part on the Home tab.", "At the prompt 'Part name', type A." }, TdocSplit.Instructions(entries[0]));
        Assert.Equal(2, TdocSplit.Instructions(entries[2]).Count);
        Assert.Equal("Command line: type 0,0, then press Enter", TdocPlan.CommandLineText(entries[2].Shown, continuation: true));
        Assert.DoesNotContain(TdocPlan.StepCallouts(entries[2].Shown, continuation: true), c => c.Role == CalloutRole.RibbonItem);
    }

    [Fact]
    public void A_command_with_no_dialog_or_no_answers_after_it_is_one_step()
    {
        var noDialog = Session.Where(l => !l.Contains("modal_enter") && !l.Contains("dialog_values")).ToArray();
        Assert.Equal(new[] { "ribbon" }, TdocSplit.Entries(Build(noDialog)).Select(TdocSplit.Kind));
        var noAfter = Session.Where(l => !l.Contains("\"type\":\"prompt\"")).ToArray();
        Assert.Equal(new[] { "ribbon", "dialog" }, TdocSplit.Entries(Build(noAfter)).Select(TdocSplit.Kind));
    }

    [Fact]
    public void With_two_dialogs_the_prompts_between_them_come_between_their_steps()
    {
        var lines = new[]
        {
            Session[0], Session[1], Session[2],
            """{"t":2000,"n":4,"type":"dialog_values","for":3,"title":"Parts","source":"win32","read":1,"closedWith":"ok","fields":[]}""",
            """{"t":2500,"n":5,"type":"prompt","kind":"point","input":true,"message":"Insertion point:","status":"OK","value":[0,0,0]}""",
            """{"t":3000,"n":6,"type":"modal_enter","title":"Finish"}""",
            """{"t":3500,"n":7,"type":"dialog_values","for":6,"title":"Finish","source":"win32","read":1,"closedWith":"ok","fields":[]}""",
            """{"t":4000,"n":8,"type":"prompt","kind":"angle","input":true,"message":"Rotation angle:","status":"None"}""",
            """{"t":4100,"n":9,"type":"cmd_end","command":"INSERTPART"}""",
        };
        var entries = TdocSplit.Entries(Build(lines));
        Assert.Equal(new[] { "ribbon", "dialog", "command", "dialog", "command" }, entries.Select(TdocSplit.Kind));
        Assert.Equal(new[] { "At the prompt 'Insertion point', type 0,0." }, TdocSplit.Instructions(entries[2]));
        Assert.Equal(new[] { "At the prompt 'Rotation angle', press Enter." }, TdocSplit.Instructions(entries[4]));
        Assert.Equal(new[] { false, false, true, false, true }, entries.Select(e => e.After));
        Assert.Equal(new[] { false, true }, entries.Where(e => e.After).Select(e => e.Last));   // only the last is pictured when the command has ended
        Assert.Equal(new[] { 1, 1 }, entries.Where(e => e.After).Select(e => e.Group));
        Assert.Equal(new[] { 2, 2 }, entries.Where(e => e.After).Select(e => e.GroupEnd));
    }

    [Fact]
    public void All_the_prompts_after_the_dialog_still_leave_the_click_step_with_none()
    {
        var entries = TdocSplit.Entries(Build(Session));
        Assert.Empty(entries[0].Shown.Answers);
        Assert.True(entries[2].Lead);   // the first answers of the command: where a SETVAR's variable name would go
    }

    [Theory]
    [InlineData(300, 250, 380, 280, true)]       // a button inside the dialog
    [InlineData(0, 260, 400, 290, true)]         // a full-width control in the client area
    [InlineData(10, 40, 100, 290, true)]         // a tall one
    [InlineData(0, 0, 400, 30, false)]           // the title bar
    [InlineData(0, 30, 400, 300, false)]         // all of the client area: the dialog itself
    [InlineData(0, 0, 400, 300, false)]          // the whole window, caption included
    [InlineData(0, 0, 0, 0, false)]              // no rectangle
    [InlineData(900, 900, 950, 930, false)]      // outside it
    public void An_arrow_points_only_at_a_control_inside_the_client_area(int l, int t, int r, int b, bool expected) =>
        Assert.Equal(expected, TdocGeometry.IsControlOf(l, t, r, b, 0, 30, 400, 300));
}
