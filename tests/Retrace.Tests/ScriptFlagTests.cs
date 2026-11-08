using System.Text.Json;
using Retrace.Core;

namespace Retrace.Tests;

/// <summary>Script chains decided by the recorder's `script` flag (CMDACTIVE bit 4), for every kind of step that runs SCRIPT; and the rules for recordings without `input` flags. Synthetic recordings only.</summary>
public class ScriptFlagTests
{
    static List<Step> Build(IEnumerable<string> lines) => Plan.Build(lines.Select(l => JsonDocument.Parse(l).RootElement.Clone()));

    /// <summary>A ribbon macro that is SCRIPT itself; the script's commands start 350 and 490 ms apart (far over the 250 ms guess), the second shows two dialogs and a person picks a point in it.</summary>
    static string[] MacroChain() => new[]
    {
        """{"t":1000,"n":1,"type":"ribbon","id":"RUN_BTN","text":"Run","itemType":"RibbonCommandButton","macro":"^C^C_SCRIPT c:/x/chain.scr","tabId":"SAMPLES_TAB","tab":"Retrace Samples","panel":"Dialogs"}""",
        """{"t":1010,"n":2,"type":"cmd_start","command":"SCRIPT","origin":"ribbon","via":"RUN_BTN","script":false}""",
        """{"t":1020,"n":3,"type":"prompt","kind":"string","input":false,"message":"Enter script file...","status":"OK","value":"c:/x/chain.scr"}""",
        """{"t":1050,"n":4,"type":"cmd_end","command":"SCRIPT"}""",
        """{"t":1400,"n":5,"type":"cmd_start","command":"UCS","origin":"typed/other","script":true}""",
        """{"t":1410,"n":6,"type":"cmd_end","command":"UCS"}""",
        """{"t":1900,"n":7,"type":"cmd_start","command":"DRAWCHAINDLG","origin":"typed/other","script":true}""",
        """{"t":2000,"n":8,"type":"modal_enter","title":"AutoCAD - [Drawing1.dwg]"}""",
        """{"t":2100,"n":9,"type":"modal_title","title":"Draw items","windowClass":"#32770"}""",
        """{"t":2200,"n":10,"type":"dialog_values","for":9,"title":"Draw items","source":"win32","closedWith":"ok","fields":[],"read":3}""",
        """{"t":2210,"n":11,"type":"modal_leave"}""",
        """{"t":2400,"n":12,"type":"prompt","kind":"point","input":true,"by":"mouse","message":"Specify insertion point:","status":"OK","value":[1,2,0]}""",
        """{"t":2500,"n":13,"type":"cmd_end","command":"DRAWCHAINDLG"}""",
        """{"t":2600,"n":14,"type":"cmd_start","command":"LINE","origin":"typed/other","script":false}""",
        """{"t":2610,"n":15,"type":"cmd_end","command":"LINE"}""",
    };

    [Fact]
    public void A_macro_that_is_SCRIPT_owns_the_commands_the_recorder_flags_however_slow_the_script_is()
    {
        var steps = Build(MacroChain());
        Assert.Equal(new[] { "ribbon", "dialog", "command" }, steps.Select(s => s.Kind));
        var ribbon = steps[0];
        Assert.True(ribbon.ChainClick);
        Assert.Equal("LINE", steps[2].Command);                       // a person's command, 100 ms after the script's last: script:false
        Assert.Single(ribbon.Answers);
        var mine = ribbon.ChainAnswers.Single();
        Assert.Equal("point", mine.Kind);
    }

    [Fact]
    public void Without_the_flag_the_same_slow_script_is_split_as_before()
    {
        var steps = Build(MacroChain().Select(l => l.Replace(",\"script\":true", "").Replace(",\"script\":false", "")));
        Assert.Contains(steps, s => s.Kind == "command" && s.Command == "UCS");
    }

    [Fact]
    public void An_unknown_command_report_and_its_start_inside_a_chain_are_members()
    {
        var lines = MacroChain().Take(6).Concat(new[]
        {
            """{"t":1450,"n":20,"type":"cmd_unknown","command":"UNITS","script":true}""",
            """{"t":1550,"n":21,"type":"cmd_start","command":"UNITS","origin":"typed/other","script":true}""",
            """{"t":1560,"n":22,"type":"cmd_end","command":"UNITS"}""",
            """{"t":3000,"n":23,"type":"cmd_start","command":"LINE","origin":"typed/other","script":false}""",
            """{"t":3010,"n":24,"type":"cmd_end","command":"LINE"}""",
        });
        var steps = Build(lines);
        Assert.Equal(new[] { "ribbon", "command" }, steps.Select(s => s.Kind));
        Assert.Equal("LINE", steps[1].Command);
        // the same with the 250 ms guess (a recording without the flag), report 100 ms before the start
        var noFlag = Build(Timing(lines));
        Assert.Equal(new[] { "ribbon", "command" }, noFlag.Select(s => s.Kind));
    }

    /// <summary>The recording as an older recorder wrote it: no script flag, and commands 100 ms apart.</summary>
    static IEnumerable<string> Timing(IEnumerable<string> lines) => lines
        .Select(l => l.Replace(",\"script\":true", "").Replace(",\"script\":false", ""))
        .Select(l => l.Replace("\"t\":1400,", "\"t\":1100,").Replace("\"t\":1410,", "\"t\":1110,").Replace("\"t\":1450,", "\"t\":1200,").Replace("\"t\":1550,", "\"t\":1300,").Replace("\"t\":1560,", "\"t\":1310,"));

    [Fact]
    public void A_recording_without_input_flags_keeps_the_old_split_plan()
    {
        // an older recorder: no `input` on prompts, so any prompt may be a person's answer
        var lines = MacroChain().Select(l => l.Replace(",\"input\":false", "").Replace(",\"input\":true", "")).ToList();
        var steps = Build(Timing(lines));
        Assert.Contains(steps, s => s.Kind == "command" && s.Command == "DRAWCHAINDLG");   // a command that prompts is not taken
        // a net that cannot tell: a step holding an answer with no input flag stops it
        var plan = new List<Step>
        {
            new() { Kind = "ribbon" }, new() { Kind = "command", Command = "UCS", Answers = { new Answer("point", "OK", null, null, "p") } },
            new() { Kind = "command", Command = "DRAWCHAINDLG", HadDialog = true }, new() { Kind = "dialog", DialogTitle = "Draw items" },
        };
        Assert.Null(ReplayRules.DialogAfterChain(plan, 1, "Draw items", null, "Autodesk AutoCAD 2026"));
    }

    [Fact]
    public void The_export_lists_the_answers_a_person_gave_after_the_dialogs()
    {
        var text = ScriptExport.Build(Build(MacroChain()), "x");
        Assert.Contains("MANUAL: then give (1): point at \"Specify insertion point:\" [1,2,0]", text);
        Assert.Contains("_LINE", text);
    }

    [Fact]
    public void Steps_md_uses_the_flag_too()
    {
        var dir = Directory.CreateTempSubdirectory("retrace-test-flag").FullName;
        try
        {
            var p = Path.Combine(dir, "recording.jsonl");
            File.WriteAllLines(p, new[] { """{"type":"header","schema":1,"name":"t","started":"2026-10-17T10:00:00"}""" }.Concat(MacroChain()));
            var session = Retrace.Engine.Recordings.RecordingReader.Load(p);
            Assert.Equal(Build(MacroChain()).Count(x => x.Kind is "ribbon" or "command" or "lisp"), session.Steps.Count(x => x.Kind is "ribbon" or "command" or "lisp"));
            Assert.DoesNotContain(session.Steps, x => x.Command is "UCS" or "DRAWCHAINDLG");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_live_count_follows_the_flag_and_ignores_a_LISP_inside_a_chain_command()
    {
        int Live(IEnumerable<string> lines)
        {
            var tally = new StepTally();
            int cmd = 0, lisp = 0;
            foreach (var l in lines)
            {
                var e = JsonDocument.Parse(l).RootElement;
                string? S(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                bool? B(string k) => e.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
                var type = S("type")!;
                tally.Note(type, S("origin"), cmd, lisp, null, S("tabId"), null, null, e.GetProperty("t").GetInt64(), S("command"), B("input"), B("script"));
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
        var chain = MacroChain();
        Assert.Equal(Build(chain).Count(s => s.Kind is "ribbon" or "command" or "lisp"), Live(chain));
        // a LISP (script false) inside the chain's command DRAWCHAINDLG: part of it
        var nested = chain.Take(7).Concat(new[]
        {
            """{"t":1950,"n":30,"type":"lisp_start","firstLine":"(alert \"x\")","startup":false,"script":true}""",
            """{"t":1960,"n":31,"type":"lisp_end"}""",
        }).Concat(chain.Skip(7)).ToList();
        Assert.Equal(Live(chain), Live(nested));
        Assert.Equal(Build(nested).Count(s => s.Kind is "ribbon" or "command" or "lisp"), Live(nested));
    }

    [Fact]
    public void The_clicks_own_input_prompt_before_a_macro_SCRIPT_is_not_an_answer_to_SCRIPT()
    {
        // the click that starts the macro is claimed by a selection prompt (input true) before SCRIPT starts; that is not a person's answer at SCRIPT's prompt
        var lines = MacroChain().ToList();
        lines.Insert(1, """{"t":1005,"n":16,"type":"prompt","kind":"selection","input":true,"by":"mouse","status":"None"}""");
        var steps = Build(lines);
        Assert.True(steps.First(s => s.Kind == "ribbon").ChainClick);
        Assert.Equal(new[] { "ribbon", "dialog", "command" }, steps.Select(s => s.Kind));
    }

    [Fact]
    public void A_person_who_typed_the_file_name_of_a_macro_SCRIPT_leaves_the_click_split()
    {
        // FILEDIA 0: SCRIPT asks for the file name at the command line and the person types it: an answer before any dialog
        var lines = MacroChain().Select(l => l.Contains("\"n\":3,") ? l.Replace("\"input\":false", "\"input\":true") : l);
        var steps = Build(lines);
        Assert.False(steps.First(s => s.Kind == "ribbon").ChainClick);
        Assert.Contains(steps, s => s.Kind == "command" && s.Command == "DRAWCHAINDLG");
    }

    [Fact]
    public void A_script_end_recorded_long_after_the_LISP_end_is_still_a_member_in_a_flagged_recording()
    {
        string[] Lines(string end) => new[]
        {
            """{"t":1000,"n":1,"type":"ribbon","id":"CHAIN_BTN","text":"Chain...","itemType":"RibbonCommandButton","macro":"^C^C(C:CHAINADD)","tabId":"SAMPLES_TAB","tab":"Retrace Samples","panel":"Dialogs"}""",
            """{"t":1020,"n":2,"type":"lisp_start","firstLine":"(C:CHAINADD)","startup":false,"script":false}""",
            """{"t":1100,"n":3,"type":"cmd_start","command":"SCRIPT","origin":"typed/other","script":false}""",
            """{"t":1150,"n":4,"type":"lisp_end"}""",
            end,
            """{"t":1800,"n":6,"type":"cmd_start","command":"DRAWCHAINDLG","origin":"typed/other","script":true}""",
            """{"t":1900,"n":7,"type":"modal_enter","title":"AutoCAD - [Drawing1.dwg]"}""",
            """{"t":2000,"n":8,"type":"modal_title","title":"Draw items","windowClass":"#32770"}""",
            """{"t":2100,"n":9,"type":"dialog_values","for":8,"title":"Draw items","source":"win32","closedWith":"ok","fields":[],"read":3}""",
            """{"t":2110,"n":10,"type":"modal_leave"}""",
            """{"t":2200,"n":11,"type":"cmd_end","command":"DRAWCHAINDLG"}""",
        };
        // SCRIPT's end comes 600 ms after the LISP's: with the flag it is a member, whatever the gap
        var steps = Build(Lines("""{"t":1750,"n":5,"type":"cmd_end","command":"SCRIPT"}"""));
        Assert.Equal(new[] { "ribbon", "dialog" }, steps.Select(s => s.Kind));
        Assert.True(steps[0].ChainClick);
    }

    [Fact]
    public void In_a_recording_without_input_flags_a_prompt_that_may_be_a_persons_is_kept_as_a_chain_answer()
    {
        // an older recorder: no `input`, no `script`, and SCRIPT asks nothing (a macro with the file name): the click is one step and the prompt of the script's command is a possible answer
        var lines = new[]
        {
            """{"t":1000,"n":1,"type":"ribbon","id":"RUN_BTN","text":"Run","macro":"^C^C_SCRIPT c:/x/chain.scr","tabId":"SAMPLES_TAB","tab":"Retrace Samples","panel":"Dialogs"}""",
            """{"t":1010,"n":2,"type":"cmd_start","command":"SCRIPT","origin":"ribbon"}""",
            """{"t":1050,"n":4,"type":"cmd_end","command":"SCRIPT"}""",
            """{"t":1100,"n":5,"type":"cmd_start","command":"DRAWCHAINDLG","origin":"typed/other"}""",
            """{"t":1200,"n":6,"type":"modal_enter","title":"AutoCAD - [Drawing1.dwg]"}""",
            """{"t":1300,"n":7,"type":"modal_title","title":"Draw items","windowClass":"#32770"}""",
            """{"t":1400,"n":8,"type":"dialog_values","for":7,"title":"Draw items","source":"win32","closedWith":"ok","fields":[],"read":3}""",
            """{"t":1410,"n":9,"type":"modal_leave"}""",
            """{"t":1500,"n":10,"type":"prompt","kind":"point","message":"Specify insertion point:","status":"OK","value":[1,2,0]}""",
            """{"t":1600,"n":11,"type":"cmd_end","command":"DRAWCHAINDLG"}""",
        };
        var steps = Build(lines);
        var ribbon = steps.First(s => s.Kind == "ribbon");
        Assert.True(ribbon.ChainClick);
        var a = Assert.Single(ribbon.ChainAnswers);
        Assert.Null(a.Input);
        Assert.NotNull(ReplayRules.ChainAnswersNeedPerson(ribbon));                                     // RETRACEREPLAY stops and names it
        Assert.Single(ReplayRules.ChainAnswersAfter(ribbon.Answers, steps, steps.FindIndex(s => s.Kind == "dialog")));   // RETRACERUN pauses for it
        Assert.Contains("MANUAL: then give (1)", ScriptExport.Build(steps, "x"));                        // the export lists it
    }

    [Fact]
    public void A_scripted_OPEN_inside_the_chain_is_the_scripts_not_a_persons()
    {
        var lines = MacroChain().Take(4).Concat(new[]
        {
            """{"t":1300,"n":20,"type":"cmd_start","command":"OPEN","origin":"typed/other","script":true}""",
            """{"t":1400,"n":21,"type":"doc_created","path":"C:\\x\\other.dwg"}""",
            """{"t":1410,"n":22,"type":"doc_active","doc":"other.dwg"}""",
            """{"t":1420,"n":23,"type":"lisp_start","firstLine":"(load \"acaddoc.lsp\")","startup":true,"script":true}""",
            """{"t":1430,"n":24,"type":"lisp_end"}""",
            """{"t":1440,"n":25,"type":"doc_closing","doc":"other.dwg"}""",
            """{"t":1450,"n":26,"type":"cmd_end","command":"OPEN"}""",
            """{"t":3000,"n":27,"type":"cmd_start","command":"LINE","origin":"typed/other","script":false}""",
            """{"t":3010,"n":28,"type":"cmd_end","command":"LINE"}""",
        });
        var steps = Build(lines);
        Assert.Equal(new[] { "ribbon", "command" }, steps.Select(s => s.Kind));      // no "open" step, no OPEN command step
        Assert.True(steps[0].ChainClick);
        Assert.Equal("LINE", steps[1].Command);
    }

    [Fact]
    public void A_chain_click_checks_only_the_answers_it_will_type()
    {
        var s = new Step { Kind = "ribbon", Macro = "^C^C_line", ChainClick = true, ChainFromT = 100 };
        s.Answers.Add(new Answer("string", "OK", JsonDocument.Parse("\"A 1\"").RootElement, null, "Layer name:", false, null, 50));    // before the script: typed
        s.Answers.Add(new Answer("string", "OK", JsonDocument.Parse("\"two words\"").RootElement, null, "Text:", true, null, 200));    // inside the script: asked for, never typed
        var before = s.WithAnswers(s.Answers.Where(a => a.T < s.ChainFromT));
        Assert.Null(Plan.Problem(before));
        Assert.Equal(s.Answers.Count - 1, before.Answers.Count);
        Assert.Equal(new[] { 50L }, before.Answers.Select(a => a.T));
    }

    [Fact]
    public void Script_and_LISP_bits_that_were_set_before_do_not_count()
    {
        Assert.Equal(0, ReplayRules.NewScriptBits(4, 4));          // a replay run under a script wrapper: bit 4 on the whole run
        Assert.Equal(32, ReplayRules.NewScriptBits(4 | 32, 4));    // a LISP the step started
        Assert.Equal(4, ReplayRules.NewScriptBits(1 | 4, 0));      // CMDACTIVE 1 (a command) is not a script
        Assert.Equal(0, ReplayRules.NewScriptBits(1, 0));
        Assert.Equal(0, ReplayRules.NewScriptBits(null, 0));       // unreadable
    }

    [Fact]
    public void The_net_also_skips_later_LISP_steps_of_the_script()
    {
        var plan = new List<Step>
        {
            new() { Kind = "ribbon" }, new() { Kind = "command", Command = "DRAWCHAINDLG", HadDialog = true, EndT = 5000 }, new() { Kind = "dialog", DialogTitle = "Draw items" },
            new() { Kind = "lisp", Lisp = "(setvar \"X\" 1)", Origin = "typed/other", Script = true, T = 5400, EndT = 5410 },
            new() { Kind = "command", Command = "ZOOM", Origin = "typed/other", Script = true, T = 5800, EndT = 5810 },
            new() { Kind = "lisp", Lisp = "(princ)", Origin = "typed/other", Script = false, T = 5900, EndT = 5905 },
        };
        var (skip, why) = ReplayRules.LaterScriptCommands(plan, 2);
        Assert.Null(why);
        Assert.Equal(new[] { 3, 4 }, skip);          // flagged: the 400 ms gaps do not matter; the person's LISP (script:false) stays
    }

    [Fact]
    public void Input_given_while_the_dialog_was_open_belongs_to_its_own_fill()
    {
        var steps = new List<Step> { new() { Kind = "dialog", T = 2000, ClosedT = 2200 } };
        var answers = new List<Answer> { new("point", "OK", null, null, "p", true, null, 2100), new("point", "OK", null, null, "q", true, null, 2400) };
        Assert.Equal(new[] { 2400L }, ReplayRules.ChainAnswersAfter(answers, steps, 0).Select(a => a.T));
    }

    [Fact]
    public void The_answer_is_worded_by_its_kind()
    {
        Answer A(string kind, string? msg, string? json, string? kw = null) => new(kind, "OK", json == null ? null : JsonDocument.Parse(json).RootElement, kw, msg, true);
        Assert.Equal("point at \"Specify a point:\" [1,2,0]", ReplayRules.AnswerText(A("point", "Specify a point:", "[1,2,0]")));
        Assert.Equal("distance at \"Radius:\" 10", ReplayRules.AnswerText(A("distance", "Radius:", "10")));
        Assert.Equal("keyword at \"Yes/No:\" Yes", ReplayRules.AnswerText(A("keyword", "Yes/No:", null, "Yes")));
        Assert.Equal("text at \"Name:\" abc", ReplayRules.AnswerText(A("string", "Name:", "\"abc\"")));
        Assert.Equal("selection at \"Select objects:\"", ReplayRules.AnswerText(A("selection", "Select objects:", null)));
        Assert.Equal("number at \"Count:\" 3", ReplayRules.AnswerText(A("integer", "Count:", "3")));
        Assert.Equal("Retrace Samples > Dialogs > Chain...", ReplayRules.ButtonName(new Step { Title = "Click Retrace Samples > Dialogs > Chain..." }));
    }

    [Fact]
    public void The_pause_message_names_the_script_and_the_input()
    {
        var steps = Build(MacroChain());
        var due = ReplayRules.ChainAnswersAfter(steps[0].Answers, steps, 1);
        Assert.Single(due);
        var msg = ReplayRules.ChainPauseMessage(steps[0], due);
        Assert.Equal("the script \"Retrace Samples > Dialogs > Run\" started asks for input a person gave while recording (point at \"Specify insertion point:\" [1,2,0]); give it, then press Next step", msg);
    }
}
