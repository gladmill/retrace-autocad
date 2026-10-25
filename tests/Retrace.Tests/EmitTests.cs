using System.Text.Json;
using Retrace.Engine.Emit;

namespace Retrace.Tests;

/// <summary>Emitters on a hand-built model (no scanners involved).</summary>
public class EmitTests : IDisposable
{
    private readonly string _out = Directory.CreateTempSubdirectory("retrace-test-out").FullName;

    public void Dispose() => Directory.Delete(_out, true);

    internal static EngineModel SampleModel()
    {
        var m = new EngineModel { InputRoot = "/in" };
        var tab = new RibbonTab { Id = "TAB_Garden", Title = "Garden", Origin = RibbonOrigin.Code, Container = "Plugin", Source = new("Plugin/Tabs/GardenTab.cs", 20) };
        var panel = new RibbonPanel { Id = "Beds", Title = "Beds", Source = new("Plugin/Tabs/GardenTab.cs", 200) };
        var row = new RibbonRow();
        row.Items.Add(new RibbonItem { Id = "DEMO_Settings", Text = "Settings", Command = "DEMO_SETTINGS", Tooltip = "Shows settings", Source = new("Plugin/Tabs/GardenTab.cs", 210) });
        row.Items.Add(new RibbonItem { Id = "Pts", Text = "List Points", HandlerMethod = "Plugin.Actions.ListPoints", Source = new("Plugin/Tabs/GardenTab.cs", 220) });
        panel.Rows.Add(row);
        var slide = new RibbonRow { IsSlideout = true };
        slide.Items.Add(new RibbonItem { Id = "More", Text = "More", Macro = "^C^C_more" });
        panel.Rows.Add(slide);
        tab.Panels.Add(panel);
        m.CodeTabs.Add(tab);

        var cmd = new CommandEntry
        {
            Name = "DEMO_SETTINGS", Kind = EntryKind.CommandMethod, Lang = Lang.CSharp, Project = "Plugin",
            Source = new("Plugin/Commands.cs", 12), Method = "Plugin.Commands.Settings", Flags = "Session",
        };
        cmd.Flow.Add(new FlowStep { Kind = FlowKind.Prompt, Text = "Pick a block to tag:", Source = new("Plugin/Commands.cs", 15), Extra = { ["allowed"] = "BlockReference", ["reject"] = "That is not a block." } });
        cmd.Flow.Add(new FlowStep { Kind = FlowKind.Call, Text = "Dialogs.Show", Target = "Plugin.Dialogs.Show", Source = new("Plugin/Commands.cs", 16) });
        cmd.Flow.Add(new FlowStep { Kind = FlowKind.Dialog, Target = "FormSettings", Depth = 1, Source = new("Plugin/Dialogs.cs", 5) });
        m.Commands.Add(cmd);

        var form = new UiForm { Id = "winform.Plugin.FormSettings", Title = "Settings | Main", Kind = UiFormKind.WinForm, Project = "Plugin", Source = new("Plugin/FormSettings.Designer.cs", 3) };
        form.ClassNames.Add("Plugin.FormSettings");
        form.Controls.Add(new UiControl { Name = "okButton", Type = "Button", Text = "OK", Events = { "Click -> okButton_Click" } });
        form.Controls.Add(new UiControl { Name = "nameBox", Type = "TextBox" });
        m.Forms.Add(form);
        return m;
    }

    [Fact]
    public void WritesLinkedDocsSourceMapAndTests()
    {
        var m = SampleModel();
        var opt = new EngineOptions { Input = "/in", Output = _out };
        Linker.AddRibbonHandlers(m);
        Linker.Link(m);

        Assert.Equal("winform.Plugin.FormSettings", m.Commands[0].Flow[2].UiFormId);
        var handler = Assert.Single(m.Commands, c => c.Kind == EntryKind.RibbonHandler);
        Assert.Equal("List Points", handler.Name);

        Writer.WriteAll(m, opt);
        string Read(string rel) => File.ReadAllText(Path.Combine(_out, rel));

        using var map = JsonDocument.Parse(Read("data/sourcemap.json"));
        var entries = map.RootElement.GetProperty("entries").EnumerateArray().ToList();
        var button = entries.Single(e => e.GetProperty("id").GetString() == "ribbon:code:Plugin:TAB_Garden/Beds/DEMO_Settings");
        Assert.Equal("command:Plugin:DEMO_SETTINGS", button.GetProperty("opens")[0].GetString());
        Assert.Contains("Click 'Settings'", button.GetProperty("reach").EnumerateArray().Select(x => x.GetString()));
        var more = entries.Single(e => e.GetProperty("title").GetString() == "More");
        Assert.Contains(more.GetProperty("reach").EnumerateArray(), x => x.GetString()!.Contains("slide-out"));
        var form = entries.Single(e => e.GetProperty("kind").GetString() == "form");
        Assert.Equal("command:Plugin:DEMO_SETTINGS", form.GetProperty("openedBy")[0].GetString());
        Assert.Contains(form.GetProperty("reach").EnumerateArray(), x => x.GetString()!.Contains("Pick a block to tag"));

        var cmdDoc = Read("reference/commands/plugin.md");
        Assert.Contains("<a id=\"cmd-demo-settings\"></a>", cmdDoc);
        Assert.Contains("[Settings | Main](../dialogs/plugin/formsettings.md)".Replace("|", "\\|"), cmdDoc);
        Assert.Contains("```mermaid", cmdDoc);

        var tabDoc = Read("reference/ribbon/code/plugin/garden.md");
        Assert.Contains("[DEMO_SETTINGS](../../../commands/plugin.md#cmd-demo-settings)", tabDoc);
        Assert.Contains("(slide-out)", tabDoc);

        var tests = Read("test-plans/commands/plugin.md");
        Assert.Contains("The drawing contains at least one BlockReference.", tests);
        Assert.Contains("That is not a block.", tests);
        Assert.Contains("Click the 'OK' button.", Read("test-plans/dialogs/plugin.md"));
        Assert.Contains("(../../../commands/plugin.md#tc-cmd-", Read("test-plans/ribbon/code/plugin/garden.md"));
        Assert.Contains("Ribbon tabs from code | 1", Read("README.md"));
    }
}
