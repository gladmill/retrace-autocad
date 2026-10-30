using System.Text.Json;
using Retrace.Core;

namespace Retrace.Tests;

/// <summary>Tdoc: the sentences and legend lines a recorded step turns into. Steps are built from synthetic recordings, the way the replay builds them.</summary>
public class TdocPlanTests
{
    static List<Step> Build(params string[] lines) => Plan.Build(lines.Select(l => JsonDocument.Parse(l).RootElement.Clone()));

    const string RibbonBlock = """{"t":1000,"n":1,"type":"ribbon","id":"KITBLOCK","text":"Create Block","macro":"^C^C_KITBLOCK ","tabId":"INSERT","tab":"Insert","panel":"Block"}""";

    static readonly string[] BlockSession =
    [
        RibbonBlock,
        """{"t":1010,"n":2,"type":"cmd_start","command":"KITBLOCK","origin":"ribbon"}""",
        """{"t":1020,"n":3,"type":"modal_enter","title":"Block Definition"}""",
        """{"t":2000,"n":4,"type":"dialog_values","for":3,"title":"Block Definition","source":"win32","read":4,"closedWith":"ok","fields":[{"key":"label:Name#1","label":"Name","kind":"edit","before":"","after":"KitBlock"},{"key":"label:Snap#1","label":"Snap","kind":"check","before":"off","after":"on"}]}""",
        """{"t":2500,"n":5,"type":"prompt","kind":"point","input":true,"message":"\nSpecify insertion base point:","status":"OK","value":[10,20,0]}""",
        """{"t":2600,"n":6,"type":"prompt","kind":"string","input":true,"message":"Enter a note:","status":"None"}""",
        """{"t":2700,"n":7,"type":"cmd_end","command":"KITBLOCK"}""",
        """{"t":3000,"n":8,"type":"mark","note":"block_made"}""",
    ];

    [Theory]
    [InlineData(true, null, "OK")]
    [InlineData(false, null, "Cancel")]
    [InlineData(false, "OK", "OK")]          // a LISP alert's only button reads OK but has Cancel's id
    [InlineData(true, "&Save", "Save")]
    [InlineData(true, "  ", "OK")]
    public void The_close_button_goes_by_its_own_text_when_known(bool ok, string? shown, string expected) =>
        Assert.Equal(expected, TdocPlan.CloseCallout(ok, shown));

    [Theory]
    [InlineData("(C:KITDIALOG)", "KITDIALOG")]       // a command defined in LISP is typed by its name
    [InlineData("( c:kit-alert )", "KIT-ALERT")]
    [InlineData("(load \"kit.lsp\")", "(load \"kit.lsp\")")]   // any other expression is typed as it is
    [InlineData("", "the LISP expression")]
    public void A_lisp_step_types_what_a_person_types(string lisp, string typed)
    {
        var s = new Step { Kind = "lisp", Lisp = lisp };
        Assert.Equal(typed, TdocPlan.LispTyped(s));
        Assert.Equal($"Type {typed} at the command line and press Enter.", TdocPlan.Instructions(s)[0]);
    }

    [Fact]
    public void A_ribbon_step_says_which_button_on_which_tab_and_what_to_type_at_the_prompts()
    {
        var steps = Build(BlockSession);
        Assert.Equal(new[] { "ribbon", "dialog", "mark" }, steps.Select(s => s.Kind));
        Assert.Equal(
            new[]
            {
                "Click Create Block on the Insert tab.",
                "At the prompt 'Specify insertion base point', type 10,20.",
                "At the prompt 'Enter a note', press Enter.",
            },
            TdocPlan.Instructions(steps[0]));
    }

    [Fact]
    public void A_dialog_step_lists_each_changed_field_then_the_button_that_closes_it()
    {
        var d = Build(BlockSession)[1];
        Assert.Equal(new[] { "In the Block Definition dialog:", "Set Name to KitBlock.", "Tick Snap.", "Click OK." }, TdocPlan.Instructions(d));
        Assert.Equal("Block Definition dialog", TdocPlan.StepTitle(d));
    }

    [Fact]
    public void A_dialog_closed_with_cancel_or_with_no_recorded_values_is_told_plainly()
    {
        var cancel = new Step { Kind = "dialog", DialogTitle = "Units", ClosedWith = "cancel", Values = [new FieldChange("k", "Rows", "edit", "1", "2")] };
        Assert.Equal(new[] { "In the Units dialog, click Cancel." }, TdocPlan.Instructions(cancel));
        var unknown = new Step { Kind = "dialog", DialogTitle = "Units" };
        Assert.Equal(new[] { "In the Units dialog, fill in the fields, then click OK." }, TdocPlan.Instructions(unknown));
        var same = new Step { Kind = "dialog", DialogTitle = "Units", ClosedWith = "ok", Values = [], ValuesRead = 3 };
        Assert.Equal(new[] { "In the Units dialog, leave the values as they are and click OK." }, TdocPlan.Instructions(same));
        Assert.Equal(new[] { "In the dialog, fill in the fields, then click OK." }, TdocPlan.Instructions(new Step { Kind = "dialog" }));
    }

    [Fact]
    public void A_checkpoint_step_gives_the_note()
    {
        var m = Build(BlockSession)[2];
        Assert.Equal(new[] { "Checkpoint: block_made" }, TdocPlan.Instructions(m));
        Assert.Equal("Checkpoint: block_made", TdocPlan.StepTitle(m));
        Assert.Equal("Checkpoint", TdocPlan.StepTitle(new Step { Kind = "mark" }));
    }

    [Fact]
    public void A_typed_command_and_a_LISP_call_say_what_to_type()
    {
        var steps = Build(
            """{"t":100,"n":1,"type":"cmd_start","command":"LINE","origin":"typed/other"}""",
            """{"t":110,"n":2,"type":"prompt","kind":"point","input":true,"message":"Specify first point:","status":"OK","value":[0,0,0]}""",
            """{"t":120,"n":3,"type":"prompt","kind":"point","input":true,"message":"Specify next point or [Undo]:","status":"Keyword","keyword":"Undo"}""",
            """{"t":130,"n":4,"type":"cmd_end","command":"LINE"}""",
            """{"t":200,"n":5,"type":"lisp_start","firstLine":"(c:kitgrid)"}""",
            """{"t":210,"n":6,"type":"lisp_end"}""");
        Assert.Equal(new[] { "Type LINE at the command line and press Enter.", "At the prompt 'Specify first point', type 0,0.", "At the prompt 'Specify next point or [Undo]', type Undo." },
            TdocPlan.Instructions(steps[0]));
        Assert.Equal(new[] { "Type KITGRID at the command line and press Enter." }, TdocPlan.Instructions(steps[1]));
    }

    [Fact]
    public void Answers_that_cannot_be_typed_are_described_or_left_out()
    {
        var entity = new Answer("entity", "OK", JsonDocument.Parse("""{"handle":"2A"}""").RootElement, null, "Select object:");
        Assert.Equal("select the object", TdocPlan.AnswerAction(entity));
        Assert.Equal("At the prompt 'Select object', select the object.", Assert.Single(TdocPlan.AnswerLines([entity])));
        Assert.Equal("press Esc", TdocPlan.AnswerAction(new Answer("point", "Cancel", null, null, "Point:")));
        Assert.Null(TdocPlan.AnswerAction(new Answer("point", "Error", null, null, "Point:")));
        Assert.Empty(TdocPlan.AnswerLines([new Answer("point", "Error", null, null, "Point:")]));
        // no prompt text: a plain sentence
        Assert.Equal("Type 5.", Assert.Single(TdocPlan.AnswerLines([new Answer("integer", "OK", JsonDocument.Parse("5").RootElement, null, null)])));
    }

    [Fact]
    public void A_palette_step_says_the_values_and_the_button_a_native_one_says_the_action()
    {
        var p = new Step
        {
            Kind = "palette", DialogTitle = "Kit", Button = "Apply",
            Values = [new FieldChange("k", "Width", "edit", "1", "40"), new FieldChange("k2", "Metric", "radio", "off", "on")],
        };
        Assert.Equal(new[] { "In the Kit palette:", "Set Width to 40.", "Choose Metric.", "Click Apply." }, TdocPlan.Instructions(p));
        Assert.Equal(new[] { "In the Kit palette, click Apply." }, TdocPlan.Instructions(new Step { Kind = "palette", DialogTitle = "Kit", Button = "Apply" }));
        Assert.Equal(new[] { "Look at the Kit palette." }, TdocPlan.Instructions(new Step { Kind = "palette", DialogTitle = "Kit" }));
        Assert.Equal("The Kit palette", TdocPlan.Caption(p));
    }

    [Fact]
    public void Open_and_unknown_steps_have_a_sentence_too()
    {
        Assert.Equal(new[] { "Open demo01.dwg." }, TdocPlan.Instructions(new Step { Kind = "open", Drawing = @"C:\demo\sub\demo01.dwg" }));
        Assert.Equal(new[] { "Open demo02.dwg." }, TdocPlan.Instructions(new Step { Kind = "open", Drawing = "/tmp/x/demo02.dwg" }));
        Assert.Equal("other", TdocPlan.KindOf(new Step { Kind = "weird" }));
        Assert.Equal("palette", TdocPlan.KindOf(new Step { Kind = "palette" }));
    }

    [Theory]
    [InlineData("Click Insert > Block > Create Block", "Insert", "Block", "Create Block")]
    [InlineData("Click Insert > Create Block", "Insert", null, "Create Block")]
    [InlineData("Click Create Block", null, null, "Create Block")]
    [InlineData("Click Home > Draw > Dim > Linear", "Home", "Draw", "Dim > Linear")]   // a label with " > " in it keeps it
    public void The_label_is_the_button_text_without_tab_and_panel(string title, string? tab, string? panel, string expected) =>
        Assert.Equal(expected, TdocPlan.RibbonLabel(new Step { Kind = "ribbon", Title = title, Tab = tab, Panel = panel }));

    [Fact]
    public void Without_a_title_the_label_is_the_item_id()
    {
        Assert.Equal("KITBLOCK", TdocPlan.RibbonLabel(new Step { Kind = "ribbon", Title = "x", ItemId = "KITBLOCK" }));
        Assert.Null(TdocPlan.RibbonLabel(new Step { Kind = "ribbon", Title = "x" }));
        Assert.Equal(new[] { "Click KITBLOCK." }, TdocPlan.Instructions(new Step { Kind = "ribbon", Title = "x", ItemId = "KITBLOCK" }));
    }

    // ---- callouts ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_ribbon_step_with_answers_gets_a_button_box_then_a_command_line_box()
    {
        var plans = TdocPlan.StepCallouts(Build(BlockSession)[0]);
        Assert.Equal(new[] { CalloutRole.RibbonItem, CalloutRole.CommandLine }, plans.Select(p => p.Role));
        Assert.Equal("Click Create Block", plans[0].Text);
        Assert.Equal("Command line: type 10,20, then press Enter", plans[1].Text);
    }

    [Fact]
    public void A_command_step_has_one_command_line_box_that_names_the_command_and_the_answers()
    {
        var s = Build(
            """{"t":100,"n":1,"type":"cmd_start","command":"LINE","origin":"typed/other"}""",
            """{"t":110,"n":2,"type":"prompt","kind":"point","input":true,"message":"First:","status":"OK","value":[0,0,0]}""",
            """{"t":120,"n":3,"type":"prompt","kind":"point","input":true,"message":"Next:","status":"OK","value":[5,5,0]}""",
            """{"t":130,"n":4,"type":"cmd_end","command":"LINE"}""")[0];
        var plan = Assert.Single(TdocPlan.StepCallouts(s));
        Assert.Equal(CalloutRole.CommandLine, plan.Role);
        Assert.Equal("Command line: type LINE, then 0,0, then 5,5", plan.Text);
    }

    [Fact]
    public void A_long_command_line_is_cut_after_six_parts()
    {
        var s = new Step { Kind = "command", Command = "PLINE" };
        for (int i = 0; i < 9; i++) s.Answers.Add(new Answer("integer", "OK", JsonDocument.Parse(i.ToString()).RootElement, null, "n:"));
        Assert.Equal("Command line: type PLINE, then 0, then 1, then 2, then 3, then 4, ...", TdocPlan.CommandLineText(s));
    }

    [Fact]
    public void A_ribbon_step_with_nothing_to_type_has_only_the_button_box_and_a_checkpoint_none()
    {
        var plan = Assert.Single(TdocPlan.StepCallouts(new Step { Kind = "ribbon", Title = "Click Insert > Block > Create Block", Tab = "Insert", Panel = "Block" }));
        Assert.Equal(CalloutRole.RibbonItem, plan.Role);
        Assert.Empty(TdocPlan.StepCallouts(new Step { Kind = "mark", Note = "x" }));
        Assert.Null(TdocPlan.CommandLineText(new Step { Kind = "ribbon" }));
    }

    [Theory]
    [InlineData("edit", "KitBlock", "Name: KitBlock")]
    [InlineData("edit", "", "Name: (empty)")]
    [InlineData("check", "on", "Name: on")]
    [InlineData("combo", "0.00", "Name: 0.00")]
    public void A_field_callout_reads_label_colon_value(string kind, string after, string expected) =>
        Assert.Equal(expected, TdocPlan.FieldCallout(new FieldChange("k", "&Name:", kind, "x", after)));

    [Fact]
    public void A_radio_callout_is_the_choice_and_a_long_value_is_cut()
    {
        Assert.Equal("Metric", TdocPlan.FieldCallout(new FieldChange("k", "Metric", "radio", "off", "on")));
        var cut = TdocPlan.FieldCallout(new FieldChange("k", "Note", "edit", "", new string('x', 200)));
        Assert.Equal(70, cut.Length);
        Assert.EndsWith("...", cut);
        Assert.Equal("OK", TdocPlan.CloseCallout(true));
        Assert.Equal("Cancel", TdocPlan.CloseCallout(false));
    }

    [Fact]
    public void Numbering_counts_only_the_boxes_that_were_found()
    {
        var numbered = TdocPlan.Number([new CalloutSpec("a", true), new CalloutSpec("b", false), new CalloutSpec("c", true), new CalloutSpec("d", true)]);
        Assert.Equal(new[] { (1, "a", 0), (2, "c", 2), (3, "d", 3) }, numbered.Select(n => (n.Number, n.Text, n.Index)));
        Assert.Empty(TdocPlan.Number([]));
        Assert.Empty(TdocPlan.Number([new CalloutSpec("a", false)]));
    }

    [Fact]
    public void A_dialog_legend_numbers_fields_in_order_and_the_close_button_last()
    {
        var d = Build(BlockSession)[1];
        var specs = d.Values!.Select(v => new CalloutSpec(TdocPlan.FieldCallout(v), true)).Append(new CalloutSpec(TdocPlan.CloseCallout(true), true)).ToList();
        Assert.Equal(new[] { "1 Name: KitBlock", "2 Snap: on", "3 OK" }, TdocPlan.Number(specs).Select(n => $"{n.Number} {n.Text}"));
    }
}
