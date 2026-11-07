using System.Text.Json;
using Retrace.Core;
using Retrace.Engine.Recordings;

namespace Retrace.Tests;

/// <summary>AutoCAD's security prompt ("Security - Unsigned Executable File"): never a test step, in the plugin's plan and in the engine's script.</summary>
public class SecurityPromptTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("retrace-test-sec").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    const string Header = """{"type":"header","schema":1,"name":"t","started":"2026-10-17T10:00:00"}""";

    // NETLOAD of an untrusted dll: the prompt opens inside the command (its title arrives as modal_title, as the recorder writes it)
    static readonly string[] Netload =
    {
        """{"t":100,"n":1,"type":"cmd_start","command":"NETLOAD","origin":"typed/other"}""",
        """{"t":110,"n":2,"type":"modal_enter","title":"AutoCAD"}""",
        """{"t":400,"n":3,"type":"modal_title","title":"Security - Unsigned Executable File","windowClass":"#32770"}""",
        """{"t":1800,"n":4,"type":"modal_leave"}""",
        """{"t":1810,"n":5,"type":"dialog_values","for":3,"title":"Security - Unsigned Executable File","source":"win32","fields":[],"read":0,"closedWith":null}""",
        """{"t":1900,"n":6,"type":"cmd_end","command":"NETLOAD"}""",
    };

    [Theory]
    [InlineData("Security - Unsigned Executable File", true)]
    [InlineData("Security - Signed Executable File", true)]
    [InlineData("  security - unsigned executable file ", true)]
    [InlineData("File Loading - Security Concern", true)]
    [InlineData("Signed File Not in Trusted Folder", true)]
    [InlineData("Draw grid", false)]
    [InlineData("Security", false)]
    [InlineData("Network Security - Settings", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsTitle_knows_the_security_prompts(string? title, bool expected)
    {
        Assert.Equal(expected, SecurityPrompt.IsTitle(title));
        Assert.Equal(expected, RecordingReader.IsSecurityPrompt(title));   // the engine keeps the same rule
    }

    [Fact]
    public void Message_says_what_to_do()
    {
        var m = SecurityPrompt.Message("Security - Unsigned Executable File ");
        Assert.StartsWith("blocked by AutoCAD's security prompt \"Security - Unsigned Executable File\"", m);
        Assert.Contains("Load Once", m);
        Assert.Contains("Trusted Locations", m);
    }

    [Fact]
    public void The_plan_has_no_dialog_step_for_a_security_prompt()
    {
        var steps = Plan.Build(Netload.Select(l => JsonDocument.Parse(l).RootElement.Clone()));
        Assert.DoesNotContain(steps, s => s.Kind == "dialog");
        var cmd = Assert.Single(steps, s => s.Kind == "command");
        Assert.False(cmd.HadDialog);   // its input is typed in one go, not split around a dialog
    }

    [Fact]
    public void A_real_dialog_after_a_security_prompt_is_still_a_step()
    {
        var lines = Netload.Concat(new[]
        {
            """{"t":2000,"n":7,"type":"cmd_start","command":"DRAWGRID","origin":"typed/other"}""",
            """{"t":2010,"n":8,"type":"modal_enter","title":"AutoCAD"}""",
            """{"t":2200,"n":9,"type":"modal_title","title":"Draw grid"}""",
            """{"t":2800,"n":10,"type":"modal_leave"}""",
            """{"t":2900,"n":11,"type":"cmd_end","command":"DRAWGRID"}""",
        });
        var steps = Plan.Build(lines.Select(l => JsonDocument.Parse(l).RootElement.Clone()));
        var d = Assert.Single(steps, s => s.Kind == "dialog");
        Assert.Equal("Draw grid", d.DialogTitle);
    }

    [Fact]
    public void A_plugin_dialog_called_Security_is_still_a_step()
    {
        Assert.True(SecurityPrompt.IsRecorded("Security - Unsigned Executable File", "#32770"));
        Assert.True(SecurityPrompt.IsRecorded("Security - Unsigned Executable File", null));
        Assert.False(SecurityPrompt.IsRecorded("Security - Settings", "WindowsForms10.Window.8.app.0.1"));
        var lines = new[]
        {
            """{"t":100,"n":1,"type":"cmd_start","command":"MYSETTINGS","origin":"typed/other"}""",
            """{"t":110,"n":2,"type":"modal_enter","title":"AutoCAD"}""",
            """{"t":300,"n":3,"type":"modal_title","title":"Security - Settings","windowClass":"WindowsForms10.Window.8.app.0.1"}""",
            """{"t":900,"n":4,"type":"modal_leave"}""",
            """{"t":950,"n":5,"type":"cmd_end","command":"MYSETTINGS"}""",
        };
        var steps = Plan.Build(lines.Select(l => JsonDocument.Parse(l).RootElement.Clone()));
        Assert.Single(steps, s => s.Kind == "dialog");
        var p = Path.Combine(_dir, "w.jsonl");
        File.WriteAllLines(p, new[] { Header }.Concat(lines));
        Assert.Contains("A dialog opens: **Security - Settings**", ScriptWriter.Build(RecordingReader.Load(p), null, _dir, new List<string>()));
    }

    [Fact]
    public void A_nested_dialog_does_not_change_what_dropping_the_dialog_it_was_opened_from_restores()
    {
        // FOO has no dialog yet. Its dialog opens (HadDialog becomes true), a click on it opens a nested dialog (attached to the click, not a step), and the first dialog then turns out to be a
        // security prompt (its title arrives late): dropping it gives FOO back its own HadDialog, false, not the true the nested dialog's modal_enter saw
        var lines = new[]
        {
            """{"t":100,"n":1,"type":"cmd_start","command":"FOO","origin":"typed/other"}""",
            """{"t":110,"n":2,"type":"modal_enter","title":"AutoCAD"}""",
            """{"t":200,"n":3,"type":"modal_title","title":"Draw items"}""",
            """{"t":1000,"n":4,"type":"dialog_click","for":3,"button":"Options...","fields":[],"read":2}""",
            """{"t":1100,"n":5,"type":"modal_enter","title":"Item options"}""",
            """{"t":2000,"n":6,"type":"dialog_values","for":5,"title":"Item options","source":"win32","parent":3,"fields":[],"read":2,"closedWith":"ok"}""",
            """{"t":2500,"n":7,"type":"modal_title","title":"Security - Unsigned Executable File","windowClass":"#32770"}""",
            """{"t":3000,"n":8,"type":"modal_leave"}""",
            """{"t":3100,"n":9,"type":"cmd_end","command":"FOO"}""",
        };
        var steps = Plan.Build(lines.Select(l => JsonDocument.Parse(l).RootElement.Clone()));
        Assert.Empty(steps.Where(s => s.Kind == "dialog"));
        Assert.False(steps.Single(s => s.Kind == "command").HadDialog);
        // and the same dialog kept (its title is no security prompt) leaves the command with a dialog
        var kept = Plan.Build(lines.Select(l => JsonDocument.Parse(l.Replace("Security - Unsigned Executable File", "Draw items")).RootElement.Clone()));
        Assert.True(kept.Single(s => s.Kind == "command").HadDialog);
    }

    [Fact]
    public void A_late_title_restores_HadDialog_on_the_command_that_opened_the_prompt()
    {
        // A had a real dialog; the next modal opens under A, but its (security) title arrives from the recorder's 300 ms timer after B started.
        var lines = new[]
        {
            """{"t":100,"n":1,"type":"cmd_start","command":"DRAWGRID","origin":"typed/other"}""",
            """{"t":110,"n":2,"type":"modal_enter","title":"AutoCAD"}""",
            """{"t":200,"n":3,"type":"modal_title","title":"Draw grid"}""",
            """{"t":500,"n":4,"type":"modal_leave"}""",
            """{"t":600,"n":5,"type":"modal_enter","title":"AutoCAD"}""",
            """{"t":650,"n":6,"type":"cmd_end","command":"DRAWGRID"}""",
            """{"t":700,"n":7,"type":"cmd_start","command":"LINE","origin":"typed/other"}""",
            """{"t":900,"n":8,"type":"modal_title","title":"Security - Unsigned Executable File","windowClass":"#32770"}""",
            """{"t":1500,"n":9,"type":"modal_leave"}""",
            """{"t":1600,"n":10,"type":"cmd_end","command":"LINE"}""",
        };
        var steps = Plan.Build(lines.Select(l => JsonDocument.Parse(l).RootElement.Clone()));
        Assert.True(steps.Single(s => s.Kind == "command" && s.Title.Contains("DRAWGRID")).HadDialog);
        Assert.False(steps.Single(s => s.Kind == "command" && s.Title.Contains("LINE")).HadDialog);
        Assert.Equal("Draw grid", Assert.Single(steps, s => s.Kind == "dialog").DialogTitle);
    }

    [Fact]
    public void The_test_script_does_not_ask_a_person_to_fill_in_a_security_prompt()
    {
        var p = Path.Combine(_dir, "s.jsonl");
        File.WriteAllLines(p, new[] { Header }.Concat(Netload));
        var md = ScriptWriter.Build(RecordingReader.Load(p), null, _dir, new List<string>());
        Assert.Contains("NETLOAD", md);
        Assert.DoesNotContain("Security", md);
        Assert.DoesNotContain("A dialog opens", md);
    }
}
