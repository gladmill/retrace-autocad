using System.Text.Json;
using Retrace.Core;

namespace Retrace.Tests;

/// <summary>A ribbon button whose LISP runs a SCRIPT file: the script's commands are part of the click's step (Plan.ScriptChain). Synthetic recordings only (the Retrace.Samples DRAWCHAINDLG button).</summary>
public class ScriptChainTests
{
    static List<Step> Build(IEnumerable<string> lines) => Plan.Build(lines.Select(l => JsonDocument.Parse(l).RootElement.Clone()));

    const string Click = """{"t":1000,"n":2,"type":"ribbon","id":"CHAIN_BTN","text":"Chain...","itemType":"RibbonCommandButton","macro":"^C^C(C:CHAINADD)","tabId":"SAMPLES_TAB","tab":"Retrace Samples","panel":"Dialogs"}""";

    /// <summary>Ribbon click, its LISP: UCS then (command "SCRIPT" file); the script types DRAWCHAINDLG, which shows two dialogs ("Draw items", then "Item options").</summary>
    static string[] Chain() => new[]
    {
        """{"t":500,"n":1,"type":"tab","id":"SAMPLES_TAB","title":"Retrace Samples"}""",
        Click,
        """{"t":1010,"n":3,"type":"prompt","kind":"selection","input":true,"by":"mouse","status":"None"}""",
        """{"t":1020,"n":4,"type":"lisp_start","firstLine":"(C:CHAINADD)","startup":false}""",
        """{"t":1030,"n":5,"type":"cmd_start","command":"UCS","origin":"ribbon","via":"CHAIN_BTN"}""",
        """{"t":1040,"n":6,"type":"prompt","kind":"point","input":false,"message":"Specify origin of UCS...","status":"None"}""",
        """{"t":1050,"n":7,"type":"cmd_end","command":"UCS"}""",
        """{"t":1100,"n":8,"type":"cmd_start","command":"SCRIPT","origin":"typed/other"}""",
        """{"t":1100,"n":9,"type":"prompt","kind":"string","input":false,"message":"Enter script file...","status":"OK","value":"c:/x/chain.scr"}""",
        """{"t":1150,"n":10,"type":"cmd_end","command":"SCRIPT"}""",
        """{"t":1150,"n":11,"type":"lisp_end"}""",
        """{"t":1150,"n":12,"type":"prompt","kind":"selection","input":false,"status":"None"}""",
        """{"t":1200,"n":13,"type":"cmd_start","command":"DRAWCHAINDLG","origin":"typed/other"}""",
        """{"t":1300,"n":14,"type":"modal_enter","title":"AutoCAD - [Drawing1.dwg]"}""",
        """{"t":1400,"n":15,"type":"modal_title","title":"Draw items","windowClass":"WindowsForms10.Window.8.app.0.1a2b3c"}""",
        """{"t":1500,"n":16,"type":"dialog_click","for":15,"button":"Options...","id":1026}""",
        """{"t":1600,"n":17,"type":"dialog_values","for":15,"title":"Draw items","source":"win32","closedWith":"ok","fields":[],"read":3}""",
        """{"t":1610,"n":18,"type":"modal_leave"}""",
        """{"t":1700,"n":19,"type":"modal_enter","title":"AutoCAD - [Drawing1.dwg]"}""",
        """{"t":1800,"n":20,"type":"modal_title","title":"Item options","windowClass":"WindowsForms10.Window.8.app.0.1a2b3c"}""",
        """{"t":1900,"n":21,"type":"dialog_values","for":20,"title":"Item options","source":"win32","closedWith":"ok","fields":[],"read":3}""",
        """{"t":1910,"n":22,"type":"modal_leave"}""",
    };

    static string Pick(int n, long t, bool input) =>
        $$"""{"t":{{t}},"n":{{n}},"type":"prompt","kind":"point","input":{{(input ? "true" : "false")}},"by":"mouse","message":"Specify insertion point:","status":"OK","value":[1,2,0]}""";

    [Fact]
    public void A_ribbon_LISP_that_runs_a_script_is_one_ribbon_step_plus_the_dialogs()
    {
        var lines = Chain().Concat(new[] { Pick(23, 3000, false), """{"t":3010,"n":24,"type":"cmd_end","command":"DRAWCHAINDLG"}""" });
        var steps = Build(lines);
        Assert.Equal(new[] { "tab", "ribbon", "dialog", "dialog" }, steps.Select(s => s.Kind));
        var ribbon = steps[1];
        Assert.True(ribbon.ChainClick);
        Assert.Null(ribbon.Command);            // the click is replayed, no command is typed for it
        Assert.Empty(ribbon.Answers);           // input:false answers of the absorbed commands need no replay
        Assert.Equal(new[] { "Draw items", "Item options" }, steps.Where(s => s.Kind == "dialog").Select(s => s.DialogTitle));
        Assert.All(steps.Where(s => s.Kind == "dialog"), d => Assert.Equal("WindowsForms10.Window.8.app.0.1a2b3c", d.DialogClass));
        Assert.Equal("\u0003\u0003(C:CHAINADD)\n", Plan.InputText(ribbon));     // the macro only (the fallback when the click cannot be invoked): no UCS, SCRIPT or DRAWCHAINDLG typed
    }

    [Fact]
    public void An_answer_a_person_gave_after_the_first_dialog_stays_with_the_ribbon_step()
    {
        var steps = Build(Chain().Concat(new[] { Pick(23, 3000, true), """{"t":3010,"n":24,"type":"cmd_end","command":"DRAWCHAINDLG"}""" }));
        Assert.Equal(new[] { "tab", "ribbon", "dialog", "dialog" }, steps.Select(s => s.Kind));
        var a = Assert.Single(steps[1].Answers);
        Assert.Equal("point", a.Kind);
    }

    [Fact]
    public void A_person_answer_before_any_dialog_leaves_the_steps_split()
    {
        var lines = Chain().Select(l => l.Contains("\"n\":9,") ? l.Replace("\"input\":false", "\"input\":true") : l);
        var steps = Build(lines);
        Assert.Contains(steps, s => s.Kind == "command" && s.Command == "SCRIPT");
        Assert.False(steps.First(s => s.Kind == "ribbon").ChainClick);
    }

    [Fact]
    public void The_security_prompt_is_not_the_chains_first_dialog()
    {
        // a person's answer between AutoCAD's security prompt and the first real dialog is before any dialog the replay can show: the steps stay split
        var lines = Chain().Take(12).Concat(new[]
        {
            """{"t":1200,"n":13,"type":"cmd_start","command":"DRAWCHAINDLG","origin":"typed/other"}""",
            """{"t":1250,"n":14,"type":"modal_enter","title":"Security - Loading a file","windowClass":"#32770"}""",
            """{"t":1260,"n":15,"type":"modal_leave"}""",
            Pick(16, 1270, true),
        }).Concat(Chain().Skip(12).Select(l => l)).ToList();
        var steps = Build(lines);
        Assert.False(steps.First(s => s.Kind == "ribbon").ChainClick);
        // with the security prompt only (no answer around it) the chain holds
        var ok = Chain().Take(12).Concat(new[]
        {
            """{"t":1200,"n":13,"type":"cmd_start","command":"DRAWCHAINDLG","origin":"typed/other"}""",
            """{"t":1250,"n":14,"type":"modal_enter","title":"AutoCAD"}""",
            """{"t":1260,"n":15,"type":"modal_title","title":"Security - Loading a file","windowClass":"#32770"}""",
            """{"t":1270,"n":16,"type":"modal_leave"}""",
        }).Concat(Chain().Skip(13));
        Assert.True(Build(ok).First(s => s.Kind == "ribbon").ChainClick);
    }

    [Fact]
    public void A_context_menu_inside_the_chains_dialog_does_not_break_the_chain()
    {
        var lines = Chain().ToList();
        lines.Insert(15, """{"t":1450,"n":50,"type":"context_menu","for":15,"kind":"winforms","target":"name:ItemList","label":"Items","targetKind":"list","path":["Edit","Rename"],"fields":[],"read":3}""");
        lines.Add(Pick(23, 3000, false));
        lines.Add("""{"t":3010,"n":24,"type":"cmd_end","command":"DRAWCHAINDLG"}""");
        var steps = Build(lines);
        Assert.Equal(new[] { "tab", "ribbon", "dialog", "dialog" }, steps.Select(s => s.Kind));
        Assert.True(steps[1].ChainClick);
    }

    [Fact]
    public void A_context_menu_of_a_palette_ends_the_chain()
    {
        var lines = Chain().Take(12).Concat(new[]
        {
            """{"t":1170,"n":13,"type":"context_menu","palette":"Menus","set":"Retrace Samples","kind":"winforms","path":["Properties..."]}""",
            """{"t":1200,"n":14,"type":"cmd_start","command":"LINE","origin":"typed/other"}""",
            """{"t":1210,"n":15,"type":"cmd_end","command":"LINE"}""",
        });
        var steps = Build(lines);
        Assert.Equal("LINE", steps.Last().Command);
        Assert.Equal("command", steps.Last().Kind);
    }

    [Fact]
    public void A_command_started_after_a_person_picked_a_point_in_the_chain_is_its_own_step()
    {
        var lines = Chain().Concat(new[]
        {
            Pick(23, 3000, true),
            """{"t":3010,"n":24,"type":"cmd_end","command":"DRAWCHAINDLG"}""",
            """{"t":3160,"n":25,"type":"cmd_start","command":"LINE","origin":"typed/other"}""",     // 150 ms later: inside the 250 ms window
            """{"t":3170,"n":26,"type":"cmd_end","command":"LINE"}""",
        });
        var steps = Build(lines);
        Assert.Equal(new[] { "tab", "ribbon", "dialog", "dialog", "command" }, steps.Select(s => s.Kind));
        Assert.Equal("LINE", steps[4].Command);
        Assert.Single(steps[1].Answers);
    }

    [Fact]
    public void A_keyword_re_ask_in_the_chain_is_one_answer_as_in_any_step()
    {
        var lines = Chain().Concat(new[]
        {
            """{"t":3000,"n":23,"type":"prompt","kind":"distance","input":true,"message":"Specify radius or [Diameter]:","status":"Keyword","keyword":"Diameter"}""",
            """{"t":3005,"n":24,"type":"prompt","kind":"keyword","input":true,"message":"Specify radius or [Diameter]:","status":"OK","value":"10"}""",
            """{"t":3010,"n":25,"type":"cmd_end","command":"DRAWCHAINDLG"}""",
        });
        var chain = Assert.Single(Build(lines), s => s.Kind == "ribbon");
        Assert.Single(chain.Answers);
        // the same events inside an ordinary command: the same single answer (one shared rule)
        var plain = Build(new[]
        {
            """{"t":100,"n":1,"type":"cmd_start","command":"CIRCLE","origin":"typed/other"}""",
            """{"t":200,"n":2,"type":"prompt","kind":"distance","input":true,"message":"Specify radius or [Diameter]:","status":"Keyword","keyword":"Diameter"}""",
            """{"t":205,"n":3,"type":"prompt","kind":"keyword","input":true,"message":"Specify radius or [Diameter]:","status":"OK","value":"10"}""",
            """{"t":300,"n":4,"type":"cmd_end","command":"CIRCLE"}""",
        }).Single();
        Assert.Equal(plain.Answers.Count, chain.Answers.Count);
    }

    [Theory]
    [InlineData("2500")]    // more than a second later
    [InlineData("1500")]    // 350 ms after the script ended: a person typing
    public void A_command_typed_later_stays_its_own_step(string startT)
    {
        var lines = new[]
        {
            Click,
            """{"t":1020,"n":4,"type":"lisp_start","firstLine":"(C:CHAINADD)","startup":false}""",
            """{"t":1100,"n":8,"type":"cmd_start","command":"SCRIPT","origin":"typed/other"}""",
            """{"t":1150,"n":10,"type":"cmd_end","command":"SCRIPT"}""",
            """{"t":1150,"n":11,"type":"lisp_end"}""",
            $$"""{"t":{{startT}},"n":13,"type":"cmd_start","command":"LINE","origin":"typed/other"}""",
            $$"""{"t":{{long.Parse(startT) + 5}},"n":14,"type":"cmd_end","command":"LINE"}""",
        };
        var steps = Build(lines);
        Assert.Equal(new[] { "ribbon", "command" }, steps.Select(s => s.Kind));
        Assert.Equal("LINE", steps[1].Command);
        Assert.True(steps[0].ChainClick);
    }

    [Fact]
    public void A_typed_command_within_a_second_of_a_macro_less_click_is_not_glued_to_its_chain_step()
    {
        // no macro on the click: the old "first command within 1 s is the click's" rule must not take a command typed after the chain
        var lines = Chain().Take(12).Select(l => l.Replace("\"macro\":\"^C^C(C:CHAINADD)\",", "")).Concat(new[]
        {
            """{"t":1500,"n":31,"type":"cmd_start","command":"LINE","origin":"typed/other"}""",
            """{"t":1510,"n":32,"type":"cmd_end","command":"LINE"}""",
        });
        var steps = Build(lines);
        var ribbon = steps.First(s => s.Kind == "ribbon");
        Assert.True(ribbon.ChainClick);
        Assert.Null(ribbon.Command);
        Assert.Equal("LINE", steps.Last().Command);
        Assert.Equal("command", steps.Last().Kind);
    }

    [Fact]
    public void A_command_typed_right_after_the_script_with_input_in_between_stays_its_own_step()
    {
        var lines = Chain().Take(12).Concat(new[]
        {
            """{"t":1200,"n":13,"type":"prompt","kind":"point","input":true,"by":"keys","message":"x","status":"None"}""",
            """{"t":1220,"n":14,"type":"cmd_start","command":"LINE","origin":"typed/other"}""",
            """{"t":1230,"n":15,"type":"cmd_end","command":"LINE"}""",
        });
        var steps = Build(lines);
        Assert.Equal("command", steps.Last().Kind);
        Assert.Equal("LINE", steps.Last().Command);
    }

    [Fact]
    public void A_ribbon_LISP_without_a_script_plans_as_before()
    {
        var lines = new[]
        {
            Click,
            """{"t":1020,"n":4,"type":"lisp_start","firstLine":"(C:CHAINADD)","startup":false}""",
            """{"t":1030,"n":5,"type":"cmd_start","command":"UCS","origin":"ribbon","via":"CHAIN_BTN"}""",
            """{"t":1050,"n":7,"type":"cmd_end","command":"UCS"}""",
            """{"t":1100,"n":11,"type":"lisp_end"}""",
            """{"t":1110,"n":13,"type":"cmd_start","command":"DRAWCHAINDLG","origin":"typed/other"}""",
            """{"t":1200,"n":14,"type":"cmd_end","command":"DRAWCHAINDLG"}""",
        };
        var steps = Build(lines);
        Assert.Equal(new[] { "ribbon", "lisp", "command", "command" }, steps.Select(s => s.Kind));
        Assert.All(steps, s => Assert.False(s.ChainClick));
    }

    [Fact]
    public void A_recording_without_a_ribbon_click_plans_as_before()
    {
        var lines = new[]
        {
            """{"t":100,"n":1,"type":"cmd_start","command":"SCRIPT","origin":"typed/other"}""",
            """{"t":110,"n":2,"type":"cmd_end","command":"SCRIPT"}""",
            """{"t":120,"n":3,"type":"cmd_start","command":"LINE","origin":"typed/other"}""",
            """{"t":130,"n":4,"type":"cmd_end","command":"LINE"}""",
        };
        Assert.Equal(new[] { "SCRIPT", "LINE" }, Build(lines).Select(s => s.Command));
    }

    // ---- the replay of a chain click ----

    [Fact]
    public void The_chains_answers_are_the_After_of_InputAroundDialog_for_the_first_dialog()
    {
        var steps = Build(Chain().Concat(new[] { Pick(23, 3000, true), """{"t":3010,"n":24,"type":"cmd_end","command":"DRAWCHAINDLG"}""" }));
        var ribbon = steps[1];
        var firstDialog = steps.First(s => s.Kind == "dialog");
        var (first, after) = Plan.InputAroundDialog(ribbon, firstDialog.T);
        Assert.Equal("\u0003\u0003(C:CHAINADD)\n", first);
        // the old separate implementation: a command step with the same answers, no start
        var reference = Plan.InputText(new Step { Kind = "command", Command = ribbon.Command, T = ribbon.T, Answers = ribbon.Answers.ToList() }, includeStart: false);
        Assert.False(string.IsNullOrEmpty(after));
        Assert.Equal(reference, after);
        // every answer of a chain was given after the first real dialog opened, so none is lost to First
        Assert.All(ribbon.Answers, a => Assert.True(a.T >= firstDialog.T));
    }

    [Fact]
    public void The_click_keeps_its_LISP_form_and_types_it_when_it_has_no_macro()
    {
        var steps = Build(Chain());
        Assert.Equal("(C:CHAINADD)", steps[1].Lisp);
        var noMacro = Build(Chain().Select(l => l.Replace("\"macro\":\"^C^C(C:CHAINADD)\",", "")));
        var ribbon = noMacro.First(s => s.Kind == "ribbon");
        Assert.True(ribbon.ChainClick);
        Assert.Equal("(C:CHAINADD)\n", Plan.InputText(ribbon));
        // an ordinary macro-less click with no chain types nothing
        Assert.Null(Plan.InputText(new Step { Kind = "ribbon", Lisp = "(x)" }));
    }

    [Fact]
    public void LISP_forms_the_script_runs_and_a_late_SCRIPT_end_do_not_cut_the_chain()
    {
        var lines = Chain().Take(12).Concat(new[]
        {
            """{"t":1200,"n":40,"type":"lisp_start","firstLine":"(setvar \"CMDECHO\" 0)","startup":false}""",
            """{"t":1210,"n":41,"type":"lisp_end"}""",
            Chain()[12].Replace("\"t\":1200", "\"t\":1250"),
        }).Concat(Chain().Skip(13)).Concat(new[] { """{"t":3010,"n":24,"type":"cmd_end","command":"DRAWCHAINDLG"}""" });
        var steps = Build(lines);
        Assert.Equal(new[] { "tab", "ribbon", "dialog", "dialog" }, steps.Select(s => s.Kind));
        Assert.True(steps[1].ChainClick);
        // SCRIPT's own end and the end of the LISP that ran it, recorded the other way round
        var swapped = Chain().ToList();
        (swapped[9], swapped[10]) = (swapped[10], swapped[9]);
        Assert.Equal(new[] { "tab", "ribbon", "dialog", "dialog" }, Build(swapped).Select(s => s.Kind));
        // a later LISP typed by a person is still its own step
        var later = Chain().Take(12).Concat(new[]
        {
            """{"t":2500,"n":40,"type":"lisp_start","firstLine":"(princ)","startup":false}""",
            """{"t":2510,"n":41,"type":"lisp_end"}""",
        });
        Assert.Equal("lisp", Build(later).Last().Kind);
    }

    [Fact]
    public void A_persons_event_inside_a_later_command_ends_the_chain_before_that_command()
    {
        var lines = Chain().Take(12).Concat(new[]
        {
            """{"t":1200,"n":13,"type":"cmd_start","command":"FOO","origin":"typed/other"}""",
            """{"t":1210,"n":14,"type":"palette_click","palette":"Menus","set":"Retrace Samples","button":"Go"}""",
            """{"t":1220,"n":15,"type":"cmd_end","command":"FOO"}""",
        });
        var steps = Build(lines);
        var ribbon = steps.First(s => s.Kind == "ribbon");
        Assert.True(ribbon.ChainClick);                                  // the LISP + SCRIPT core is kept
        Assert.Contains(steps, s => s.Kind == "command" && s.Command == "FOO");
        Assert.DoesNotContain(steps, s => s.Kind == "command" && s.Command is "UCS" or "SCRIPT");
    }

    [Fact]
    public void The_export_of_a_chain_click_is_a_manual_note_because_a_script_inside_a_script_would_end_it()
    {
        var lines = Chain().Concat(new[]
        {
            """{"t":2400,"n":29,"type":"cmd_end","command":"DRAWCHAINDLG"}""",
            """{"t":2800,"n":30,"type":"cmd_start","command":"LINE","origin":"typed/other"}""",
            """{"t":2810,"n":31,"type":"cmd_end","command":"LINE"}""",
        });
        foreach (var l in new[] { lines, lines.Select(x => x.Replace("\"macro\":\"^C^C(C:CHAINADD)\",", "")) })
        {
            var text = ScriptExport.Build(Build(l), "x");
            Assert.Contains("MANUAL: run \"Retrace Samples > Dialogs > Chain...\" by hand: it starts a script, which would end this one", text);
            Assert.DoesNotContain("(C:CHAINADD)", text);          // neither the macro nor the LISP form
            Assert.DoesNotContain("UCS", text);
            Assert.DoesNotContain("MANUAL:  opened", text);
            Assert.True(text.Contains("_LINE"), text);                        // the steps after it are still exported
        }
    }

    [Fact]
    public void Empty_lines_of_a_plain_step_stay_in_the_export()
    {
        // ";;" is two Enters: the empty lines are kept (only a line that held nothing but ^C is dropped)
        var step = new Step { Kind = "ribbon", Title = "Click x", Macro = "^C^C_line;1,1;;2,2" };
        var text = ScriptExport.Build(new List<Step> { step }, "x");
        Assert.True(text.Replace("\r\n", "\n").Contains("1,1\n\n2,2"), text);
    }

    [Fact]
    public void WithAnswers_copies_the_step_and_replaces_only_its_answers()
    {
        var s = new Step { Kind = "ribbon", Macro = "m", Lisp = "(l)", ChainClick = true, HadDialog = true, T = 7 };
        s.Answers.Add(new Answer("point", "OK", null, null, "p", true));
        var c = s.WithAnswers(Array.Empty<Answer>());
        Assert.Empty(c.Answers);
        Assert.Single(s.Answers);
        Assert.Equal(("m", "(l)", true, true, 7L), (c.Macro, c.Lisp, c.ChainClick, c.HadDialog, c.T));
    }

    [Fact]
    public void Plan_records_a_commands_origin_and_end_for_the_safety_net()
    {
        var steps = Build(new[]
        {
            """{"t":100,"n":1,"type":"cmd_start","command":"LINE","origin":"typed/other"}""",
            """{"t":180,"n":2,"type":"cmd_end","command":"LINE"}""",
        });
        Assert.Equal(("typed/other", 180L), (steps[0].Origin, steps[0].EndT));
    }

    static Step Cmd(string name, long t, long end, string origin = "typed/other") => new() { Kind = "command", Command = name, T = t, EndT = end, Origin = origin };

    [Fact]
    public void The_net_also_skips_the_later_commands_the_script_typed_within_the_gap()
    {
        var plan = OldPlan();
        plan[4].EndT = 5000;                                   // DRAWCHAINDLG ended
        plan[5].T = 2000; plan[6].T = 3000;
        plan.Add(Cmd("ZOOM", 5100, 5150));                    // 100 ms after: the script's
        plan.Add(Cmd("REGEN", 5300, 5310));                   // 150 ms after ZOOM: the script's
        plan.Add(Cmd("LINE", 6500, 6600));                    // a second later: a person's
        var (skip, unsafeWhy) = ReplayRules.LaterScriptCommands(plan, 5);
        Assert.Null(unsafeWhy);
        Assert.Equal(new[] { 7, 8 }, skip);
        // a command typed by a person (origin) or too late is never skipped
        var late = OldPlan(); late[4].EndT = 5000; late.Add(Cmd("ZOOM", 5100, 5150, origin: "palette"));
        Assert.Empty(ReplayRules.LaterScriptCommands(late, 5).Skip);
        // not decidable: the step holds an answer a person gave -> stop for a person
        var answered = OldPlan(); answered[4].EndT = 5000;
        var zoom = Cmd("ZOOM", 5100, 5150); zoom.Answers.Add(new Answer("point", "OK", null, null, "p", true));
        answered.Add(zoom);
        Assert.NotNull(ReplayRules.LaterScriptCommands(answered, 5).Unsafe);
        // no end time known for the owner: nothing skipped
        var noEnd = OldPlan(); noEnd.Add(Cmd("ZOOM", 5100, 5150));
        Assert.Empty(ReplayRules.LaterScriptCommands(noEnd, 5).Skip);
    }

    // ---- the live step count follows the plan ----

    static int LiveCount(IEnumerable<string> lines)
    {
        var tally = new StepTally();
        int cmd = 0, lisp = 0;
        foreach (var l in lines)
        {
            var e = JsonDocument.Parse(l).RootElement;
            string? S(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var type = S("type")!;
            bool? input = e.TryGetProperty("input", out var iv) && iv.ValueKind is JsonValueKind.True or JsonValueKind.False ? iv.GetBoolean() : null;
            tally.Note(type, S("origin"), cmd, lisp, null, S("tabId") ?? S("id"), null, null, e.GetProperty("t").GetInt64(), S("command"), input);
            switch (type)
            {
                case "cmd_start": cmd++; break;
                case "cmd_end" or "cmd_cancel" or "cmd_fail": if (cmd > 0) cmd--; break;
                case "lisp_start": lisp++; break;
                case "lisp_end" or "lisp_cancel": if (lisp > 0) lisp--; break;
            }
        }
        return tally.Count;
    }

    static int PlanCount(IEnumerable<string> lines) => Build(lines).Count(s => s.Kind is "ribbon" or "command" or "lisp" or "tab");

    [Fact]
    public void The_live_step_count_counts_a_chain_click_as_one_step_like_the_plan()
    {
        var chain = Chain();
        Assert.Equal(PlanCount(chain), LiveCount(chain));
        // a person's point inside the chain's command, then a command 150 ms later: its own step
        var more = chain.Concat(new[]
        {
            Pick(23, 3000, true),
            """{"t":3010,"n":24,"type":"cmd_end","command":"DRAWCHAINDLG"}""",
            """{"t":3160,"n":25,"type":"cmd_start","command":"LINE","origin":"typed/other"}""",
            """{"t":3170,"n":26,"type":"cmd_end","command":"LINE"}""",
        }).ToList();
        Assert.Equal(PlanCount(more), LiveCount(more));
        // a command a second later, and a LISP click without SCRIPT, count as before
        var later = Chain().Concat(new[] { """{"t":2500,"n":30,"type":"cmd_start","command":"LINE","origin":"typed/other"}""", """{"t":2510,"n":31,"type":"cmd_end","command":"LINE"}""" }).ToList();
        Assert.Equal(PlanCount(later), LiveCount(later));
        var noScript = new[]
        {
            Click,
            """{"t":1020,"n":4,"type":"lisp_start","firstLine":"(C:CHAINADD)","startup":false}""",
            """{"t":1030,"n":5,"type":"cmd_start","command":"UCS","origin":"typed/other"}""",
            """{"t":1050,"n":7,"type":"cmd_end","command":"UCS"}""",
            """{"t":1100,"n":11,"type":"lisp_end"}""",
            """{"t":1110,"n":13,"type":"cmd_start","command":"DRAWCHAINDLG","origin":"typed/other"}""",
            """{"t":1200,"n":14,"type":"cmd_end","command":"DRAWCHAINDLG"}""",
        };
        Assert.Equal(PlanCount(noScript), LiveCount(noScript));
        // LISP lines in the script
        var lispLine = Chain().Take(12).Concat(new[]
        {
            """{"t":1200,"n":40,"type":"lisp_start","firstLine":"(setvar \"CMDECHO\" 0)","startup":false}""",
            """{"t":1210,"n":41,"type":"lisp_end"}""",
            Chain()[12].Replace("\"t\":1200", "\"t\":1250"),
            """{"t":1900,"n":24,"type":"cmd_end","command":"DRAWCHAINDLG"}""",
        }).ToList();
        Assert.Equal(PlanCount(lispLine), LiveCount(lispLine));
    }

    [Fact]
    public void A_chain_dialog_belongs_to_the_ribbon_step_in_steps_md_with_the_answer_after_it_and_a_cancel_shown()
    {
        var dir = Directory.CreateTempSubdirectory("retrace-test-chain").FullName;
        try
        {
            string Md(IEnumerable<string> lines)
            {
                var p = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".jsonl");
                File.WriteAllLines(p, new[] { """{"type":"header","schema":1,"name":"t","started":"2026-10-17T10:00:00"}""" }.Concat(lines));
                return Retrace.Engine.Recordings.ScriptWriter.Build(Retrace.Engine.Recordings.RecordingReader.Load(p), null, dir, new List<string>());
            }
            var answered = Chain().Concat(new[] { Pick(23, 3000, true), """{"t":3010,"n":24,"type":"cmd_end","command":"DRAWCHAINDLG"}""" });
            var md = Md(answered);
            var dlg = md.IndexOf("Draw items", StringComparison.Ordinal);
            var pt = md.IndexOf("insertion point", StringComparison.OrdinalIgnoreCase);
            Assert.True(dlg >= 0 && pt >= 0, md);
            Assert.True(dlg < pt, "the answer given after OK is listed after the dialog:\n" + md);
            var cancelled = Md(Chain().Concat(new[] { """{"t":3010,"n":24,"type":"cmd_cancel","command":"DRAWCHAINDLG"}""" }));
            Assert.Contains("cancelled", cancelled, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Steps_md_shows_one_step_for_the_chain_click_like_the_replay_plan()
    {
        var dir = Directory.CreateTempSubdirectory("retrace-test-chain").FullName;
        try
        {
            var p = Path.Combine(dir, "recording.jsonl");
            var lines = Chain().Concat(new[] { Pick(23, 3000, true), """{"t":3010,"n":24,"type":"cmd_end","command":"DRAWCHAINDLG"}""" });
            File.WriteAllLines(p, new[] { """{"type":"header","schema":1,"name":"t","started":"2026-10-17T10:00:00"}""" }.Concat(lines));
            var session = Retrace.Engine.Recordings.RecordingReader.Load(p);
            var ribbons = session.Steps.Where(x => x.Kind == "ribbon").ToList();
            Assert.Single(ribbons);
            Assert.DoesNotContain(session.Steps, x => x.Kind is "command" or "lisp");
            Assert.Equal(Build(lines).Count(x => x.Kind is "ribbon" or "command" or "lisp"), session.Steps.Count(x => x.Kind is "ribbon" or "command" or "lisp"));
            Assert.Single(ribbons[0].Answers);                      // the person's point
            var md = Retrace.Engine.Recordings.ScriptWriter.Build(session, null, dir, new List<string>());
            Assert.DoesNotContain("SCRIPT", md);
            Assert.DoesNotContain("UCS", md);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_window_class_is_compared_without_the_parts_that_change_between_runs()
    {
        Assert.Equal("HwndWrapper", ReplayRules.NormalizeClass("HwndWrapper[acad.exe;;3f2a1c4e-0000-0000-0000-000000000001]"));
        Assert.Equal("WindowsForms10.Window", ReplayRules.NormalizeClass("WindowsForms10.Window.8.app.0.141b42a_r6_ad1"));
        Assert.Equal("WindowsForms10.BUTTON", ReplayRules.NormalizeClass("WindowsForms10.BUTTON.app.0.1a2b3c_r9_ad1"));
        Assert.Equal("Afx", ReplayRules.NormalizeClass("Afx:00400000:b:00010011:00000006:0A1B2C3D"));
        Assert.Equal("AfxFrameOrView140s", ReplayRules.NormalizeClass("AfxFrameOrView140s"));
        Assert.Equal("AfxFrameOrView140u", ReplayRules.NormalizeClass("AfxFrameOrView140u"));
        Assert.Equal("#32770", ReplayRules.NormalizeClass("#32770"));
        var plan = OldPlan();
        plan[5].DialogClass = "WindowsForms10.Window.8.app.0.141b42a_r6_ad1";
        Assert.Equal(5, ReplayRules.DialogAfterChain(plan, 1, "Draw items", "WindowsForms10.Window.8.app.0.9f8e7d_r12_ad1", Main));
        plan[5].DialogClass = "HwndWrapper[acad.exe;;aaaa]";
        Assert.Equal(5, ReplayRules.DialogAfterChain(plan, 1, "Draw items", "HwndWrapper[acad.exe;;bbbb]", Main));
        Assert.Null(ReplayRules.DialogAfterChain(plan, 1, "Draw items", "#32770", Main));
    }

    [Fact]
    public void A_non_interactive_replay_names_the_answers_it_cannot_type()
    {
        var steps = Build(Chain().Concat(new[] { Pick(23, 3000, true), """{"t":3010,"n":24,"type":"cmd_end","command":"DRAWCHAINDLG"}""" }));
        var msg = ReplayRules.ChainAnswersNeedPerson(steps[1]);
        Assert.NotNull(msg);
        Assert.Contains("Specify insertion point:", msg);
        Assert.Contains("[1,2,0]", msg);
        var none = Build(Chain());
        Assert.Null(ReplayRules.ChainAnswersNeedPerson(none[1]));
        Assert.Null(ReplayRules.ChainAnswersNeedPerson(new Step { Kind = "ribbon" }));
    }

    // ---- the replayer's safety net for recordings the timing rule rejects (planned the old way) ----

    const string Main = "Autodesk AutoCAD 2026 - [Drawing1.dwg]";

    static List<Step> OldPlan() => new()
    {
        new Step { Kind = "ribbon" }, new Step { Kind = "lisp" }, new Step { Kind = "command", Command = "UCS" }, new Step { Kind = "command", Command = "SCRIPT" },
        new Step { Kind = "command", Command = "DRAWCHAINDLG", HadDialog = true }, new Step { Kind = "dialog", DialogTitle = "Draw items", DialogClass = "WindowsForms10.Window.8.app.0.1a2b3c" },
        new Step { Kind = "dialog", DialogTitle = "Item options" },
    };

    const string Cls = "WindowsForms10.Window.8.app.0.1a2b3c";

    [Fact]
    public void An_open_dialog_after_a_click_skips_the_command_steps_its_chain_ran()
    {
        Assert.Equal(5, ReplayRules.DialogAfterChain(OldPlan(), 1, "Draw items", Cls, Main));
    }

    [Fact]
    public void The_net_does_nothing_unless_it_is_safe()
    {
        Assert.Null(ReplayRules.DialogAfterChain(OldPlan(), 1, "Another dialog", Cls, Main));    // not the next recorded dialog
        Assert.Null(ReplayRules.DialogAfterChain(OldPlan(), 1, "Draw items", "#32770", Main));   // same title, other window class
        var typed = OldPlan(); typed[0].Kind = "command";                                         // the step just sent was typed, not a click
        Assert.Null(ReplayRules.DialogAfterChain(typed, 1, "Draw items", Cls, Main));
        var answered = OldPlan(); answered[3].Answers.Add(new Answer("point", "OK", null, null, "p", true));    // a person answered in a skipped step
        Assert.Null(ReplayRules.DialogAfterChain(answered, 1, "Draw items", Cls, Main));
        var mark = OldPlan(); mark[3].Kind = "mark";                                              // a checkpoint is between
        Assert.Null(ReplayRules.DialogAfterChain(mark, 1, "Draw items", Cls, Main));
        var noOwner = OldPlan(); noOwner[4].HadDialog = false;
        Assert.Null(ReplayRules.DialogAfterChain(noOwner, 1, "Draw items", Cls, Main));
        Assert.Null(ReplayRules.DialogAfterChain(OldPlan(), 5, "Draw items", Cls, Main));         // already at the dialog step: nothing to skip
    }

    [Theory]
    [InlineData("AutoCAD")]
    [InlineData(Main)]
    public void A_click_that_raises_a_generic_error_box_is_not_taken_for_the_recorded_dialog(string title)
    {
        var plan = OldPlan();
        plan[5].DialogTitle = "AutoCAD";        // even a recording whose dialog was titled like the error box
        plan[5].DialogClass = null;
        Assert.Null(ReplayRules.DialogAfterChain(plan, 1, title, "#32770", Main));
        var plain = OldPlan();
        Assert.Null(ReplayRules.DialogAfterChain(plain, 1, title, "#32770", Main));
    }

    [Fact]
    public void Titles_are_compared_exactly_not_by_a_fuzzy_match()
    {
        Assert.Null(ReplayRules.DialogAfterChain(OldPlan(), 1, "Draw items - copy", Cls, Main));
        Assert.Equal(5, ReplayRules.DialogAfterChain(OldPlan(), 1, "draw items ", Cls, Main));
    }
}
