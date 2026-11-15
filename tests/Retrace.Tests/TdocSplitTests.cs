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

    // a ribbon LISP that runs SCRIPT (a script chain); the script starts the command, which opens one dialog (a tree and a list set, a button closes it), a modal state with no window, a jig's
    // sampler prompts (input:false) and then the person's two answers
    static readonly string[] ChainSession =
    [
        """{"t":1000,"n":1,"type":"ribbon","id":"PARTCHAIN","text":"Add part","macro":"^C^C(C:PARTADD)","tabId":"HOME","tab":"Home","panel":"Parts"}""",
        """{"t":1010,"n":2,"type":"prompt","kind":"selection","input":true,"by":"mouse","status":"None"}""",
        """{"t":1020,"n":3,"type":"lisp_start","firstLine":"(C:PARTADD)","startup":false,"script":false}""",
        """{"t":1030,"n":4,"type":"cmd_start","command":"UCS","origin":"ribbon","via":"PARTCHAIN","script":false}""",
        """{"t":1040,"n":5,"type":"prompt","kind":"point","input":false,"message":"Specify origin:","status":"None"}""",
        """{"t":1050,"n":6,"type":"cmd_end","command":"UCS"}""",
        """{"t":1100,"n":7,"type":"cmd_start","command":"SCRIPT","origin":"typed/other","script":false}""",
        """{"t":1100,"n":8,"type":"prompt","kind":"string","input":false,"message":"Enter script file name:","status":"Keyword"}""",
        """{"t":1150,"n":9,"type":"cmd_end","command":"SCRIPT"}""",
        """{"t":1150,"n":10,"type":"lisp_end"}""",
        """{"t":1200,"n":11,"type":"cmd_start","command":"INSERTPART","origin":"typed/other","script":true}""",
        """{"t":1300,"n":12,"type":"modal_enter","title":"AutoCAD - [Drawing1.dwg]"}""",
        """{"t":1400,"n":13,"type":"modal_title","title":"Parts","windowClass":"WindowsForms10.Window.8.app.0.1a2b3c"}""",
        """{"t":2000,"n":14,"type":"dialog_click","for":13,"button":"Quick add","id":1024,"fields":[{"key":"id:1012","label":"#1012","kind":"tree","before":"","after":"Root › Child"},{"key":"id:1007","label":"#1007","kind":"listview","before":"","after":"Door","items":[{"text":"Door","row":2,"occurrence":1}]}]}""",
        """{"t":2100,"n":15,"type":"dialog_values","for":13,"title":"Parts","source":"win32","read":2,"closedWith":"click","fields":[]}""",
        """{"t":2110,"n":16,"type":"modal_leave"}""",
        """{"t":2200,"n":17,"type":"modal_enter","title":"AutoCAD - [Drawing1.dwg]","main":true}""",
        """{"t":2900,"n":18,"type":"modal_nowindow","for":17}""",
        """{"t":2910,"n":19,"type":"modal_leave"}""",
        """{"t":3000,"n":20,"type":"prompt","kind":"point","input":false,"drag":true,"message":"Insertion point:","status":"OK","value":[5,5,0]}""",
        """{"t":3100,"n":21,"type":"prompt","kind":"point","input":true,"by":"keys","message":"Insertion point:","status":"OK","value":[0,0,0]}""",
        """{"t":3200,"n":22,"type":"prompt","kind":"angle","input":false,"message":"Rotation angle <0>:","status":"OK","value":0.1}""",
        """{"t":3300,"n":23,"type":"prompt","kind":"angle","input":true,"by":"keys","message":"Rotation angle <0>:","status":"None"}""",
        """{"t":3400,"n":24,"type":"cmd_end","command":"INSERTPART"}""",
    ];

    [Fact]
    public void A_script_chain_click_keeps_only_the_click_and_the_prompts_after_the_dialog_are_their_own_step()
    {
        var steps = Build(ChainSession);
        Assert.True(steps[0].ChainClick);
        Assert.Equal(new[] { "ribbon", "dialog" }, steps.Select(s => s.Kind));   // the replay's two steps
        var entries = TdocSplit.Entries(steps);
        Assert.Equal(new[] { "ribbon", "dialog", "command" }, entries.Select(TdocSplit.Kind));
        Assert.Equal(new[] { false, false, true }, entries.Select(e => e.After));
        Assert.Equal(new[] { "At the prompt 'Insertion point', type 0,0.", "At the prompt 'Rotation angle <0>', press Enter." }, TdocSplit.Instructions(entries[2]));
        Assert.DoesNotContain(TdocSplit.Instructions(entries[0]), l => l.Contains("Insertion point"));
        Assert.Equal("INSERTPART: answer the prompts", TdocSplit.Title(entries[2]));
        Assert.Equal(1, entries[2].Index);
        Assert.True(entries[2].Last);
        Assert.Equal((1, 1), (entries[2].Group, entries[2].GroupEnd));   // typed when its own dialog is done
        Assert.Equal("Command line: type 0,0, then press Enter", TdocPlan.CommandLineText(entries[2].Shown, continuation: true));
    }

    [Fact]
    public void A_script_chain_click_with_no_answer_after_its_dialog_stays_as_it_is()
    {
        var steps = Build(ChainSession.Where(l => !l.Contains("\"input\":true,\"by\":\"keys\"")).ToArray());
        Assert.Equal(new[] { "ribbon", "dialog" }, TdocSplit.Entries(steps).Select(TdocSplit.Kind));
    }

    [Fact]
    public void The_step_is_named_after_the_command_still_open_at_the_dialog_not_one_it_ran_itself()
    {
        Assert.Equal("INSERTPART", Build(ChainSession)[0].ChainCommand);   // UCS ended before it, SCRIPT is not the command
        var lines = ChainSession.ToList();
        // the dialog command first runs a command of its own (nested) before it shows its dialog
        lines.Insert(11, """{"t":1210,"n":50,"type":"cmd_start","command":"-LAYER","origin":"typed/other","script":true}""");
        lines.Insert(12, """{"t":1220,"n":51,"type":"cmd_end","command":"-LAYER"}""");
        var step = Build(lines.ToArray())[0];
        Assert.Equal("INSERTPART", step.ChainCommand);
        Assert.Equal("INSERTPART: answer the prompts", TdocSplit.Title(TdocSplit.Entries(Build(lines.ToArray()))[2]));
        // a chain whose dialog opens with no command of the script open: no name guessed
        var none = ChainSession.Where(l => !l.Contains("\"command\":\"INSERTPART\"")).ToArray();
        Assert.Null(Build(none)[0].ChainCommand);
        Assert.Equal("Answer the prompts", TdocSplit.ContinuationTitle(Build(none)[0]));
    }

    [Fact]
    public void An_answer_given_while_the_dialog_was_open_is_listed_after_it()
    {
        var lines = ChainSession.ToList();
        // a pick-point button of the dialog: the person's point between the dialog's opening and its close
        lines.Insert(13, """{"t":1800,"n":60,"type":"prompt","kind":"point","input":true,"by":"mouse","message":"Pick point:","status":"OK","value":[3,4,0]}""");
        var entries = TdocSplit.Entries(Build(lines.ToArray()));
        Assert.Equal(new[] { "ribbon", "dialog", "command" }, entries.Select(TdocSplit.Kind));
        Assert.Contains(TdocSplit.Instructions(entries[2]), l => l.Contains("Pick point"));
        Assert.Equal(3, TdocSplit.Instructions(entries[2]).Count);
        Assert.DoesNotContain(TdocSplit.Instructions(entries[0]), l => l.Contains("Pick point"));
    }

    [Fact]
    public void Answers_a_person_gave_after_a_replay_pause_count_as_typed_for_their_own_dialog_only()
    {
        // the replay cannot type some answers itself (ChainDelivery.Unsafe: an object pick, Esc, text with a space) or the script does not ask in time: it pauses for a person, who gives them and continues
        // the replay types them dialog by dialog (a chain) or once a dialog of the command is done (a plain command); a person who continues after a pause counts as typed
        Assert.False(TdocSplit.IsTyped(new HashSet<int>(), 1, 1));
        Assert.True(TdocSplit.IsTyped(new HashSet<int> { 1 }, 1, 1));
        Assert.False(TdocSplit.IsTyped(new HashSet<int> { 1 }, 2, 2));   // chain: the second dialog's answers are not typed by the first's
        Assert.True(TdocSplit.IsTyped(new HashSet<int> { 2 }, 1, 2));    // command: any of its dialogs
    }

    [Fact]
    public void A_single_arrow_with_its_own_tag_in_the_avoid_list_stays_where_it_was()
    {
        // a palette button 75 x 23 with its number tag above its top-left corner: the usual up-left line runs over that tag and must not mind it
        var button = new PxRect(300, 200, 375, 223);
        var tag = Annotate.TagArea(new AnnotationBox(300, 200, 75, 23, 2), 900, 600);
        var other = new PxRect(40, 40, 200, 160);
        var arrows = ClickArrow.Place([new ClickArrow.ArrowTarget(button, false, [button, tag])], [button, tag, other], 900, 600, 80);
        Assert.Equal(ClickArrow.For(337, 211, new PxRect(0, 0, 900, 600), 80), arrows[0]);
    }

    [Fact]
    public void A_tree_or_list_with_no_label_is_the_tree_or_the_list_in_the_text()
    {
        var tree = new FieldChange("id:1012", "#1012", "tree", "", "Root › Child");
        var list = new FieldChange("id:1007", "#1007", ListSelection.Kind, "", "Door", "", [new ListItem("Door", 2, 1)]);
        Assert.Equal("Select Root › Child in the tree", TdocPlan.Describe(tree));
        Assert.Equal("Select Door in the list", TdocPlan.Describe(list));
        Assert.Equal("Tree: Root › Child", TdocPlan.FieldCallout(tree));
        Assert.Equal("List: Door", TdocPlan.FieldCallout(list));
        // a label of its own stays, and a field that is no tree or list keeps its id label
        Assert.Equal("Select Door in Cabinets", TdocPlan.Describe(list with { Label = "Cabinets" }));
        Assert.Equal("Parts: Door", TdocPlan.FieldCallout(tree with { Label = "Parts", After = "Door" }));
        Assert.Equal("Set #5 to x", TdocPlan.Describe(new FieldChange("id:5", "#5", "edit", "", "x")));
        Assert.Equal("the tree", TdocPlan.ControlName("#1012", "the tree"));
        Assert.Equal("Parts", TdocPlan.ControlName("Parts", "the tree"));
    }

    [Fact]
    public void The_dialog_sentences_name_an_unlabeled_tree_and_list_plainly()
    {
        var lines = TdocPlan.Instructions(Build(ChainSession)[1]);
        Assert.Contains(lines, l => l.Contains("Select Root › Child in the tree."));
        Assert.DoesNotContain(lines, l => l.Contains("#10"));
    }

    [Fact]
    public void Three_arrows_in_one_picture_do_not_run_over_each_other_or_the_boxes()
    {
        // a tree item (left), a list item (middle), a button (right), as in a dialog picture 900 x 600
        var tree = new PxRect(40, 200, 140, 216);
        var list = new PxRect(300, 160, 380, 230);
        var button = new PxRect(700, 150, 780, 178);
        var boxes = new[] { tree, list, button };
        var avoid = boxes.Concat(boxes.Select(b => new PxRect(b.Left - 3, b.Top - 24, b.Left + 12, b.Top - 3))).ToList();
        var targets = new[]
        {
            new ClickArrow.ArrowTarget(tree, true, [tree]), new ClickArrow.ArrowTarget(list, true, [list]), new ClickArrow.ArrowTarget(button, false, [button, avoid[5]]),
        };
        var arrows = ClickArrow.Place(targets, avoid, 900, 600, 80);
        Assert.Equal(3, arrows.Count);
        for (int a = 0; a < 3; a++)
        {
            Assert.InRange(arrows[a].StartX, 0, 899); Assert.InRange(arrows[a].StartY, 0, 599);
            for (int b = 0; b < 3; b++)
                if (b != a)
                    foreach (var (x, y) in Pts(arrows[a])) Assert.False(boxes[b].Contains(x, y), $"arrow {a} runs over box {b}");
            for (int b = a + 1; b < 3; b++)
                Assert.True(Pts(arrows[a]).All(p => Pts(arrows[b]).All(q => Math.Abs(p.X - q.X) >= 7 || Math.Abs(p.Y - q.Y) >= 7)), $"arrows {a} and {b} cross");
        }
        // the head of an item arrow is just inside the item's edge (on the item), not on its middle
        Assert.True(tree.Contains(arrows[0].TipX, arrows[0].TipY));
        Assert.True(list.Contains(arrows[1].TipX, arrows[1].TipY));
        Assert.True(button.Contains(arrows[2].TipX, arrows[2].TipY));
        Assert.Equal(((button.Left + button.Right) / 2, (button.Top + button.Bottom) / 2), (arrows[2].TipX, arrows[2].TipY));   // a button: the middle, as before
    }

    static IEnumerable<(int X, int Y)> Pts(ArrowLine a) => Enumerable.Range(0, 21).Select(i => (a.StartX + (a.TipX - a.StartX) * i / 20, a.StartY + (a.TipY - a.StartY) * i / 20));

    [Fact]
    public void A_single_arrow_with_nothing_in_its_way_is_where_it_always_was()
    {
        var button = new PxRect(700, 150, 780, 178);
        var arrows = ClickArrow.Place([new ClickArrow.ArrowTarget(button)], [button], 900, 600, 80);
        Assert.Equal(ClickArrow.For(740, 164, new PxRect(0, 0, 900, 600), 80), arrows[0]);
    }

    [Fact]
    public void A_lone_arrow_keeps_the_usual_place_whatever_is_in_the_way_but_the_second_of_two_goes_around()
    {
        var button = new PxRect(700, 300, 780, 328);
        var other = new PxRect(640, 240, 700, 300);   // sits where the up-left tail would run
        var usual = ClickArrow.For(740, 314, new PxRect(0, 0, 900, 600), 80);
        Assert.Equal(usual, ClickArrow.Place([new ClickArrow.ArrowTarget(button)], [button, other], 900, 600, 80)[0]);   // nothing changes for one arrow
        var first = new PxRect(100, 100, 180, 128);
        var two = ClickArrow.Place([new ClickArrow.ArrowTarget(button), new ClickArrow.ArrowTarget(first)], [button, other, first], 900, 600, 80);
        Assert.NotEqual(usual, two[0]);
        Assert.DoesNotContain(Pts(two[0]), p => other.Contains(p.X, p.Y));
    }

    [Fact]
    public void A_number_tag_is_kept_clear_of_an_item_arrow()
    {
        var tag = Annotate.TagArea(new AnnotationBox(100, 100, 50, 16, 1), 900, 600);
        Assert.Equal(97, tag.Left);
        Assert.True(tag.Bottom <= 97 && tag.Width > 0);
        Assert.Equal(0, Annotate.TagArea(new AnnotationBox(100, 100, 50, 16, 150), 900, 600).Width);
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
