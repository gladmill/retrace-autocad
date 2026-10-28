using System.Text.Json;
using Retrace.Core;
using Retrace.Engine.Recordings;

namespace Retrace.Tests;

/// <summary>Native palettes (AutoCAD Electrical's Project Manager): tree items, toolbar buttons and drop-down lists as steps, in the replay
/// plan, steps.md, checkpoint names and the script export. Events follow what the recorder wrote on AutoCAD Electrical 2026 (2026-10-28).</summary>
public class NativePaletteTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("retrace-test-native").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    const string Header = """{"type":"header","schema":1,"name":"t","started":"2026-10-28T04:00:00"}""";
    const string Pm = "\"palette\":\"Projects\",\"set\":\"Project Manager\"";

    static string Tree(int n, long t, string action, params string[] path) =>
        $"{{\"t\":{t},\"n\":{n},\"type\":\"native_click\",{Pm},\"control\":\"tree\",\"action\":\"{action}\",\"path\":{JsonSerializer.Serialize(path)},\"index\":1,\"id\":0}}";

    // double-click demo01 in the tree: a click, then the double-click; the drawing opens, its start-up LISP runs; a checkpoint
    static readonly string[] OpenFromTree =
    {
        Tree(1, 1000, "click", "NFPADEMO", "SCHEMATIC", "demo01.dwg"),
        Tree(2, 1150, "dblclick", "NFPADEMO", "SCHEMATIC", "demo01.dwg"),
        """{"t":1400,"n":3,"type":"doc_created","doc":"DEMO01.DWG","path":"C:\\Projects\\Demo\\DEMO01.DWG"}""",
        """{"t":1600,"n":4,"type":"lisp_start","doc":"DEMO01.DWG","firstLine":"s::startup","startup":true}""",
        """{"t":1610,"n":5,"type":"lisp_end","doc":"DEMO01.DWG"}""",
        """{"t":5000,"n":6,"type":"mark","doc":"DEMO01.DWG","state":{"count":1,"byType":{"LINE":1},"byLayer":{"0":1},"layers":["0"],"hash":"aa"}}""",
    };

    static List<Step> Build(IEnumerable<string> lines) => Plan.Build(lines.Select(l => JsonDocument.Parse(l).RootElement.Clone()));

    string StepsMd(IEnumerable<string> lines)
    {
        var p = Path.Combine(_dir, "s.jsonl");
        File.WriteAllLines(p, new[] { Header }.Concat(lines));
        return ScriptWriter.Build(RecordingReader.Load(p), null, _dir, new List<string>());
    }

    [Fact]
    public void A_double_click_that_opens_a_drawing_is_one_palette_step()
    {
        var steps = Build(OpenFromTree);
        Assert.Equal(new[] { "palette", "mark" }, steps.Select(s => s.Kind));
        var p = steps[0];
        Assert.Equal("dblclick", p.Native!.Action);
        Assert.Equal(new[] { "NFPADEMO", "SCHEMATIC", "demo01.dwg" }, p.Native.Path);
        Assert.Equal(@"C:\Projects\Demo\DEMO01.DWG", p.Drawing);
        Assert.Equal("Projects", p.DialogTitle);
        Assert.Equal("Project Manager", p.Panel);
        Assert.Equal("Palette \"Projects\": double-click NFPADEMO › SCHEMATIC › demo01.dwg in the tree (opens DEMO01.DWG)", p.Title);
    }

    [Fact]
    public void Steps_md_says_what_to_double_click_and_what_it_opens()
    {
        var md = StepsMd(OpenFromTree);
        Assert.Contains("In the **Projects** palette (Project Manager), double-click **NFPADEMO › SCHEMATIC › demo01.dwg** in the tree. It opens `C:\\Projects\\Demo\\DEMO01.DWG`", md);
        Assert.DoesNotContain("Open drawing", md);
        Assert.DoesNotContain("s::startup", md);
    }

    [Fact]
    public void The_checkpoint_after_it_is_named_after_the_drawing()
    {
        var p = Path.Combine(_dir, "c.jsonl");
        File.WriteAllLines(p, new[] { Header }.Concat(OpenFromTree));
        Assert.Equal("checkpoint1_open_demo01_dwg", Assert.Single(Checkpoints.Read(p)).Name);
    }

    [Fact]
    public void A_selected_item_then_a_toolbar_button_that_runs_a_command()
    {
        var lines = new[]
        {
            Tree(1, 1000, "click", "NFPADEMO", "SCHEMATIC", "demo02.dwg"),
            $$"""{"t":2000,"n":2,"type":"native_click",{{Pm}},"control":"button","action":"click","index":4,"id":1204,"label":"Project Task List"}""",
            """{"t":2100,"n":3,"type":"cmd_start","doc":"A.DWG","command":"AETASK","origin":"palette","via":"Project Task List"}""",
            """{"t":2900,"n":4,"type":"cmd_end","doc":"A.DWG","command":"AETASK"}""",
            """{"t":4000,"n":5,"type":"mark","doc":"A.DWG"}""",
        };
        var steps = Build(lines);
        Assert.Equal(new[] { "palette", "palette", "mark" }, steps.Select(s => s.Kind));
        Assert.Equal("Palette \"Projects\": select NFPADEMO › SCHEMATIC › demo02.dwg in the tree", steps[0].Title);
        Assert.Equal("Palette \"Projects\": click the Project Task List button", steps[1].Title);
        Assert.Equal(1204, steps[1].Native!.Id);
        Assert.Equal("AETASK", steps[1].Command);   // started by the button: replay waits for it, does not type it
        var p = Path.Combine(_dir, "b.jsonl");
        File.WriteAllLines(p, new[] { Header }.Concat(lines));
        Assert.Equal("checkpoint1_projects_project_task_list", Assert.Single(Checkpoints.Read(p)).Name);
        Assert.Contains("click the **Project Task List** button", StepsMd(lines));
    }

    [Fact]
    public void A_button_without_a_tool_tip_is_named_by_its_position()
    {
        var e = JsonDocument.Parse($$"""{"type":"native_click",{{Pm}},"control":"button","action":"click","index":3,"id":0}""").RootElement;
        Assert.Equal("click toolbar button 3", NativeAction.From(e)!.Describe());
    }

    [Fact]
    public void A_project_picked_in_the_drop_down_list()
    {
        var lines = new[] { $$"""{"t":1000,"n":1,"type":"native_select",{{Pm}},"control":"combo","action":"select","index":1,"id":1001,"value":"IECDEMO"}""" };
        var step = Assert.Single(Build(lines));
        Assert.Equal("Palette \"Projects\": pick IECDEMO in the drop-down list", step.Title);
        Assert.Contains("pick **IECDEMO** in the drop-down list", StepsMd(lines));
    }

    [Fact]
    public void A_right_click_then_a_menu_command_is_one_step()
    {
        var lines = new[]
        {
            Tree(1, 1000, "rclick", "NFPADEMO", "SCHEMATIC", "demo03.dwg"),
            """{"t":2500,"n":2,"type":"cmd_start","doc":"A.DWG","command":"AEPROPERTIES","origin":"palette","via":"demo03.dwg"}""",
            """{"t":3000,"n":3,"type":"cmd_end","doc":"A.DWG","command":"AEPROPERTIES"}""",
        };
        var step = Assert.Single(Build(lines));
        Assert.Equal("rclick", step.Native!.Action);
        Assert.Equal("AEPROPERTIES", step.Command);
    }

    [Fact]
    public void Two_separate_clicks_stay_two_steps()
    {
        var lines = new[] { Tree(1, 1000, "click", "NFPADEMO"), Tree(2, 5000, "dblclick", "NFPADEMO", "SCHEMATIC", "demo01.dwg") };
        Assert.Equal(2, Build(lines).Count);
    }

    [Fact]
    public void The_script_export_stops_after_a_palette_step_that_opened_a_drawing()
    {
        var text = ScriptExport.Build(Build(OpenFromTree), "t");
        Assert.Contains("; MANUAL: Palette \"Projects\": double-click", text);
        Assert.Contains("is not exported", text);
        Assert.DoesNotContain("CHECK", text);
    }

    [Fact]
    public void Events_without_a_control_or_a_path_are_ignored()
    {
        var lines = new[]
        {
            $$"""{"t":1000,"n":1,"type":"native_click",{{Pm}},"control":"tree","action":"click","path":[]}""",
            $$"""{"t":1100,"n":2,"type":"native_click",{{Pm}},"control":"slider"}""",
        };
        Assert.Empty(Build(lines));
    }

    // picking IECDEMO in the Project Manager's project list (owner-drawn, no text): written at the click with its position, the
    // "Updating..." window and AEMTRELOAD follow, then native_value names the entry from the tree's bold item
    static readonly string[] PickProject =
    {
        $$"""{"t":19000,"n":3,"type":"native_select",{{Pm}},"control":"combo","action":"select","index":1,"id":206,"entry":6}""",
        """{"t":19718,"n":4,"type":"modal_enter","title":"AutoCAD Electrical 2026","windowClass":"AfxMDIFrame140u"}""",
        """{"t":20035,"n":5,"type":"modal_title","title":"Updating...","windowClass":"#32770"}""",
        """{"t":26794,"n":6,"type":"dialog_values","for":5,"title":"Updating...","source":"uia","fields":[],"read":0}""",
        """{"t":26799,"n":7,"type":"modal_leave"}""",
        """{"t":27319,"n":8,"type":"cmd_start","command":"AEMTRELOAD","origin":"palette","via":"entry 6"}""",
        """{"t":27473,"n":9,"type":"cmd_end","command":"AEMTRELOAD"}""",
        """{"t":27600,"n":10,"type":"native_value","for":3,"value":"IECDEMO"}""",
        """{"t":30000,"n":11,"type":"mark","note":""}""",
    };

    [Fact]
    public void A_project_picked_from_an_owner_drawn_list_is_one_step_with_its_window_and_command()
    {
        var steps = Build(PickProject);
        Assert.Equal(new[] { "palette", "mark" }, steps.Select(s => s.Kind));
        var p = steps[0];
        Assert.Equal(6, p.Native!.Entry);
        Assert.Equal("IECDEMO", p.Native.Value);
        Assert.Equal("AEMTRELOAD", p.Command);
        Assert.Equal("Palette \"Projects\": pick IECDEMO in the drop-down list", p.Title);
        var md = StepsMd(PickProject);
        Assert.Contains("pick **IECDEMO** in the drop-down list", md);
        Assert.DoesNotContain("Updating", md);
    }

    [Fact]
    public void A_dialog_long_after_a_native_action_is_still_a_step()
    {
        var lines = new[]
        {
            Tree(1, 1000, "click", "NFPADEMO"),
            """{"t":9000,"n":2,"type":"modal_enter","title":"AutoCAD"}""",
            """{"t":9200,"n":3,"type":"modal_title","title":"Project Properties"}""",
            """{"t":12000,"n":4,"type":"modal_leave"}""",
        };
        Assert.Equal(new[] { "palette", "dialog" }, Build(lines).Select(s => s.Kind));
    }

    [Fact]
    public void A_dialog_a_toolbar_button_opens_and_the_person_answers_is_a_step()
    {
        var lines = new[]
        {
            $$"""{"t":1000,"n":1,"type":"native_click",{{Pm}},"control":"button","action":"click","index":6,"id":210,"label":"Project Properties"}""",
            """{"t":1500,"n":2,"type":"modal_enter","title":"AutoCAD"}""",
            """{"t":1700,"n":3,"type":"modal_title","title":"Project Properties","windowClass":"#32770"}""",
            """{"t":6000,"n":4,"type":"dialog_values","for":3,"title":"Project Properties","source":"win32","fields":[],"read":12,"closedWith":"ok"}""",
            """{"t":6010,"n":5,"type":"modal_leave"}""",
        };
        Assert.Equal(new[] { "palette", "dialog" }, Build(lines).Select(s => s.Kind));
        Assert.Contains("Project Properties", StepsMd(lines));
    }
}
