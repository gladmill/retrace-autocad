using System.Text.Json;
using Retrace.Core;
using Retrace.Engine.Recordings;

namespace Retrace.Tests;

/// <summary>A modal state with no dialog window of its own (AutoCAD's main window in front): not a dialog step; the prompts around it stay the click's chain. Synthetic recordings only.</summary>
public class MainWindowModalTests
{
    const string Main = "AutoCAD Mechanical 2026 - [Drawing2.dwg]";
    const string MsgPoint = "Specify insertion point:";
    const string MsgAngle = "Specify rotation angle:";

    static string Prompt(int n, long t, string kind, string value, string msg, bool input, bool? drag = null, string by = "") =>
        $$"""{"t":{{t}},"n":{{n}},"type":"prompt","kind":"{{kind}}","input":{{(input ? "true" : "false")}},{{(by == "" ? "" : $"\"by\":\"{by}\",")}}{{(drag is { } d ? $"\"drag\":{(d ? "true" : "false")}," : "")}}"message":"{{msg}}","status":"OK","value":{{value}}}""";

    /// <summary>The recorded order: the item dialog resolves 1.3 s after its modal_enter (the main frame was in front first), a second modal episode has nothing in it, then the jig prompts.</summary>
    internal static List<string> Recording(bool leaveAfterPrompts, bool withNoWindowEvent = false)
    {
        var l = new List<string>
        {
            """{"t":1000,"n":1,"type":"ribbon","id":"RUN_BTN","text":"Add","macro":"^C^C_SCRIPT c:/x/chain.scr","tabId":"T1","tab":"Samples","panel":"Items"}""",
            """{"t":1010,"n":2,"type":"cmd_start","command":"SCRIPT","origin":"ribbon","via":"RUN_BTN","script":false}""",
            """{"t":1020,"n":3,"type":"prompt","kind":"string","input":false,"message":"Enter script file...","status":"OK","value":"c:/x/chain.scr"}""",
            """{"t":1050,"n":4,"type":"cmd_end","command":"SCRIPT"}""",
            """{"t":2750,"n":17,"type":"cmd_start","command":"ADDITEM","origin":"typed/other","script":true}""",
            $$"""{"t":2777,"n":18,"type":"modal_enter","title":"{{Main}}","windowClass":"AfxMDIFrame140u"}""",
            """{"t":4053,"n":20,"type":"modal_title","title":"Pick item","windowClass":"AfxFrameOrView140s"}""",
            """{"t":7844,"n":24,"type":"dialog_click","for":20,"button":"Quick","id":1024,"fields":[]}""",
            """{"t":7925,"n":25,"type":"dialog_values","for":20,"title":"Pick item","source":"win32","closedWith":"click","fields":[],"read":3}""",
            """{"t":7925,"n":26,"type":"modal_leave"}""",
            $$"""{"t":7979,"n":27,"type":"modal_enter","title":"{{Main}}","windowClass":"AfxMDIFrame140u"}""",
        };
        if (withNoWindowEvent) l.Add("""{"t":8300,"n":29,"type":"modal_nowindow","for":27}""");
        if (!leaveAfterPrompts) l.Add("""{"t":12225,"n":28,"type":"modal_leave"}""");
        int n = 100; long t = 13061;
        for (int i = 0; i < 6; i++) l.Add(Prompt(n++, t += 20, "point", $"[{1700 + i},4000,0]", MsgPoint, false, drag: i % 2 == 0));
        l.Add(Prompt(n++, t += 20, "point", "[1745.97,4038.74,0]", MsgPoint, true, by: "mouse"));
        for (int i = 0; i < 6; i++) l.Add(Prompt(n++, t += 20 + (i == 0 ? 400 : 0), "angle", $"{0.1 * i}", MsgAngle, false, drag: i % 2 == 0));
        l.Add(Prompt(n++, t += 20, "angle", "0.5346", MsgAngle, true, by: "mouse"));
        if (leaveAfterPrompts) l.Add($$"""{"t":{{t + 10}},"n":{{n++}},"type":"modal_leave"}""");
        l.Add($$"""{"t":{{t + 50}},"n":{{n}},"type":"cmd_end","command":"ADDITEM"}""");
        return l;
    }

    internal static List<Step> Build(IEnumerable<string> lines) => Plan.Build(lines.Select(l => JsonDocument.Parse(l).RootElement.Clone()));

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void A_modal_state_of_the_main_window_is_no_dialog_step_and_the_answers_follow_the_real_dialog(bool leaveAfterPrompts, bool noWindowEvent)
    {
        var steps = Build(Recording(leaveAfterPrompts, noWindowEvent));
        Assert.Equal(new[] { "ribbon", "dialog" }, steps.Select(s => s.Kind));
        Assert.Equal("Pick item", steps[1].DialogTitle);
        var due = ReplayRules.ChainAnswersAfter(steps[0].Answers, steps, 1);
        Assert.Equal(new[] { "point", "angle" }, due.Select(a => a.Kind));
        Assert.True(due.All(a => a.Input == true));
        Assert.Equal("0.5346", due[1].Value!.Value.GetRawText());
    }

    [Fact]
    public void The_steps_md_has_no_main_window_dialog_line()
    {
        var s = new RecordingSession { Name = "t" };
        foreach (var l in Recording(false))
        {
            var root = JsonDocument.Parse(l).RootElement.Clone();
            s.Events.Add(new RecordingEvent { T = root.GetProperty("t").GetInt64(), N = root.TryGetProperty("n", out var nn) ? nn.GetInt64() : null, Type = root.GetProperty("type").GetString()!, Raw = root });
        }
        RecordingReader.BuildSteps(s);
        Assert.Equal(new[] { "ribbon", "dialog" }, s.Steps.Select(x => x.Kind));
        var md = ScriptWriter.Build(s, null, Path.GetTempPath(), new List<string>());
        Assert.DoesNotContain("AutoCAD Mechanical", md);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(md, "A dialog opens"));
    }

    [Fact]
    public void A_main_window_modal_with_no_dialog_after_it_is_dropped_alone()
    {
        var lines = Recording(false).Where(l => !l.Contains("\"n\":20,") && !l.Contains("\"for\":20,") && !l.Contains("\"n\":26,") && !l.Contains("\"n\":18,")).ToList();
        Assert.DoesNotContain(Build(lines), s => s.Kind == "dialog");
    }

    [Theory]
    [InlineData("AutoCAD Mechanical 2026 - [Drawing2.dwg]", "AfxMDIFrame140u", true)]
    [InlineData("AutoCAD Electrical 2026 - [Drawing1.dwg]", null, true)]
    [InlineData("AutoCAD 2026 - [Drawing1.dwg]", "Afx:abc", true)]
    [InlineData("AutoCAD 2026", null, true)]
    [InlineData("Autodesk AutoCAD 2026 - [Drawing1.dwg]", "AfxMDIFrame140u", true)]
    [InlineData("AutoCAD", "#32770", false)]
    [InlineData("AutoCAD", null, false)]
    [InlineData("AutoCAD 2026 - [Drawing1.dwg]", "#32770", false)]
    [InlineData("Pick item", "AfxFrameOrView140s", false)]
    public void Main_window_titles(string title, string? cls, bool expected) => Assert.Equal(expected, MainWindowModal.IsRecorded(title, cls));
}
