// Manual test scripts: one suite per project for commands and dialogs, one per ribbon tab.
using System.Text;
using Retrace.Engine.Util;

namespace Retrace.Engine.Emit;

public sealed class ManualTestWriter(ModelIndex ix, OutputSink sink)
{
    private EngineModel M => ix.Model;

    public static string CommandTestDoc(string project) => $"{DocsLayout.TestPlans}/commands/{Slug.Of(project)}.md";
    public static string FormTestDoc(string project) => $"{DocsLayout.TestPlans}/dialogs/{Slug.Of(project)}.md";
    public static string RibbonTestDoc(ModelIndex ix, RibbonTab tab) => DocsLayout.TestPlans + ix.TabDoc[tab][DocsLayout.Reference.Length..];

    public void WriteAll()
    {
        foreach (var g in M.Commands.GroupBy(c => c.Project)) WriteCommandSuite(g.Key, g.OrderBy(c => ix.TestId[c]).ToList());
        foreach (var g in M.Forms.GroupBy(f => f.Project)) WriteFormSuite(g.Key, g.ToList());
        foreach (var tab in M.CodeTabs) WriteRibbonSuite(tab);
        if (ix.Options.CuixTests)
            foreach (var tab in M.Cuix.SelectMany(c => c.Tabs)) WriteRibbonSuite(tab);
        WriteIndex();
    }

    private const string Header = """
        How to use: run each case in order in a clean AutoCAD session with the plugin loaded.
        Mark each step Pass/Fail in the last column; note the build, AutoCAD version and drawing used.
        Screens referenced here are wireframes drawn from source: compare layout and labels, not pixels.

        """;

    // ------------------------------------------------------------ commands

    private void WriteCommandSuite(string project, List<CommandEntry> cmds)
    {
        var doc = CommandTestDoc(project);
        var sb = new StringBuilder($"# Manual tests: commands in {project}\n\n{Header}\n");
        sb.Append(Md.Table(new[] { "Test", "Command", "Kind", "Source" },
            cmds.Select(c => new[] { $"[{ix.TestId[c]}](#{ix.TestId[c].ToLowerInvariant()})", c.Name, c.Kind.ToString(), Md.Code(c.Source.ToString()) })));
        foreach (var c in cmds)
        {
            var id = ix.TestId[c];
            sb.Append($"\n<a id=\"{id.ToLowerInvariant()}\"></a>\n\n## {id}: {c.Name}\n\n");
            sb.Append($"Entry point {Md.Code(c.Method)} at {Md.Code(c.Source.ToString())} ([docs]({ix.CommandLink(doc, c)})).\n\n");
            sb.Append("**Preconditions**\n\n");
            foreach (var p in Preconditions(c)) sb.Append($"- {p}\n");
            sb.Append("\n**Steps**\n\n");
            var steps = Steps(doc, c);
            sb.Append(Md.Table(new[] { "#", "Action", "Expected result", "Pass/Fail" },
                steps.Select((s, i) => new[] { (i + 1).ToString(), s.action, s.expected, "" })));
            var neg = Negative(c);
            if (neg.Count > 0)
            {
                sb.Append("\n**Negative checks**\n\n");
                sb.Append(Md.Table(new[] { "#", "Action", "Expected result", "Pass/Fail" },
                    neg.Select((s, i) => new[] { $"N{i + 1}", s.action, s.expected, "" })));
            }
        }
        sink.Write(doc, sb.ToString());
    }

    private List<string> Preconditions(CommandEntry c)
    {
        var list = new List<string>
        {
            c.Lang == Lang.Cpp
                ? $"The {c.Project} ObjectARX module is loaded (APPLOAD / ARX LOAD)."
                : $"The {c.Project} assembly is loaded (NETLOAD or autoload bundle).",
            "A drawing is open and no other command is running.",
        };
        foreach (var allowed in c.Flow.Select(s => s.Extra.GetValueOrDefault("allowed")).Where(a => a != null).Distinct())
            list.Add($"The drawing contains at least one {allowed}.");
        if (c.Flow.Any(s => s.Kind == FlowKind.Selection)) list.Add("The drawing contains objects to select.");
        if (c.Flags.Contains("UsePickSet", StringComparison.OrdinalIgnoreCase)) list.Add("Optionally pre-select objects (the command accepts a pickfirst set).");
        return list;
    }

    private List<(string action, string expected)> Steps(string doc, CommandEntry c)
    {
        var steps = new List<(string, string)>();
        var start = ix.ReachCommand(c);
        steps.Add((start[0] + (start.Count > 1 ? $" ({string.Join("; ", start.Skip(1))})" : ""),
            "The command starts: no \"Unknown command\" message and no exception dialog."));
        string? last = null;
        foreach (var s in c.Flow)
        {
            (string, string)? step = s.Kind switch
            {
                FlowKind.Prompt => (PromptAction(s), $"The command line shows \"{Md.Cell(s.Text)}\"" + Keywords(s) + "; the input is accepted."),
                FlowKind.Selection => ($"Select objects{(s.Text.Length > 0 ? $" at \"{Md.Cell(s.Text)}\"" : "")} and press Enter.", "The selection is accepted."),
                FlowKind.Jig => ("Move the cursor, then pick the final point.", $"A live preview ({Md.Code(s.Target ?? "jig")}) follows the cursor; the result is placed where picked."),
                FlowKind.Dialog => (DialogAction(doc, s), "The dialog opens with the controls shown in the wireframe; closing it returns to the command."),
                FlowKind.Palette => (DialogAction(doc, s), "The palette opens (or comes to front) and stays open while you work in the drawing."),
                FlowKind.Message when s.Depth <= 2 && s.Text.Length > 0 => ("Read the command line / message box.", $"Shows \"{Md.Cell(DocsWriter.Trunc(s.Text, 120))}\" (when that branch runs)."),
                FlowKind.SendCommand => ("Wait for the command to continue.", $"AutoCAD runs {Md.Code(s.Target ?? s.Text)} on its own."),
                _ => null,
            };
            if (step is not { } st) continue;
            var key = st.Item1 + st.Item2;
            if (key == last) continue;
            last = key;
            steps.Add(st);
        }
        steps.Add(("Let the command finish (press Enter / Esc at the last prompt if needed).",
            "The command ends cleanly; the drawing shows the expected change; no error in the command line."));
        return steps;
    }

    private static string PromptAction(FlowStep s)
    {
        var allowed = s.Extra.GetValueOrDefault("allowed");
        var verb = s.Text.Contains("point", StringComparison.OrdinalIgnoreCase) || s.Target == "GetPoint" ? "Pick a point"
            : allowed != null ? $"Pick a {allowed}"
            : s.Text.Contains("select", StringComparison.OrdinalIgnoreCase) ? "Pick an object"
            : "Enter a valid value";
        return $"{verb} at the prompt \"{Md.Cell(s.Text)}\".";
    }

    private static string Keywords(FlowStep s) =>
        s.Extra.TryGetValue("keywords", out var k) ? $" with options [{k}]" : "";

    private string DialogAction(string doc, FlowStep s)
    {
        if (s.UiFormId != null && ix.FormsById.TryGetValue(s.UiFormId, out var f))
            return $"Check the {(f.Kind == UiFormKind.PaletteSet ? "palette" : "dialog")} [{Md.Cell(f.Title)}]({Md.Rel(doc, ix.FormDoc[f])}) against its wireframe, then use it (see [its UI test]({Md.Rel(doc, FormTestDoc(f.Project))})).";
        return $"Check that {Md.Code(s.Target ?? s.Text)} opens, then close it.";
    }

    private static List<(string action, string expected)> Negative(CommandEntry c)
    {
        var list = new List<(string, string)>();
        var first = c.Flow.FirstOrDefault(s => s.Kind is FlowKind.Prompt or FlowKind.Selection);
        if (first != null)
            list.Add(($"At \"{Md.Cell(first.Text)}\" press Esc.", "The command cancels cleanly: no exception dialog, no partial changes left in the drawing."));
        foreach (var s in c.Flow.Where(s => s.Extra.ContainsKey("reject")))
            list.Add(($"At \"{Md.Cell(s.Text)}\" pick an object of the wrong type.", $"The command line says \"{Md.Cell(s.Extra["reject"])}\" and prompts again."));
        if (c.Flow.Any(s => s.Kind == FlowKind.Dialog))
            list.Add(("When the dialog opens, press Cancel / close it.", "The command ends without changing the drawing."));
        list.Add(("Run the command twice in a row.", "The second run behaves like the first (no stale state, no duplicate palettes)."));
        return list;
    }

    // ------------------------------------------------------------ dialogs

    private void WriteFormSuite(string project, List<UiForm> forms)
    {
        var doc = FormTestDoc(project);
        var sb = new StringBuilder($"# Manual tests: dialogs and palettes in {project}\n\n{Header}\n");
        int n = 0;
        foreach (var f in forms.OrderBy(f => f.Kind).ThenBy(f => f.Title))
        {
            var id = $"TC-UI-{Slug.Of(project).ToUpperInvariant()}-{++n:000}";
            sb.Append($"\n<a id=\"{id.ToLowerInvariant()}\"></a>\n\n## {id}: {f.Title} ({f.Kind})\n\n");
            sb.Append($"![wireframe]({Md.Rel(doc, ix.FormAsset[f])})\n\n");
            sb.Append($"Defined at {Md.Code(f.Source.ToString())} ([docs]({Md.Rel(doc, ix.FormDoc[f])})).\n\n");
            var steps = new List<(string, string)>
            {
                ("Open it: " + string.Join(" → ", ix.ReachForm(f)), $"'{f.Title}' is shown, titled as in the wireframe."),
                ("Compare the window with the wireframe.", "Same controls, labels and rough layout; nothing clipped or overlapping."),
            };
            void Visit(IEnumerable<UiControl> cs)
            {
                foreach (var c in cs)
                {
                    var step = ControlStep(c);
                    if (step != null) steps.Add(step.Value);
                    Visit(c.Children);
                }
            }
            Visit(f.Controls);
            if (f.Kind != UiFormKind.PaletteSet)
            {
                steps.Add(("Press Tab repeatedly.", "Focus moves through the inputs in a sensible order."));
                steps.Add(("Press Esc or click Cancel.", "The dialog closes and nothing in the drawing changes."));
            }
            else
            {
                steps.Add(("Dock, undock and resize the palette.", "Content resizes; nothing is clipped."));
                steps.Add(("Close the palette and reopen it the same way.", "It reopens once (no duplicate palette)."));
            }
            sb.Append(Md.Table(new[] { "#", "Action", "Expected result", "Pass/Fail" },
                steps.Select((s, i) => new[] { (i + 1).ToString(), s.Item1, s.Item2, "" })));
        }
        sink.Write(doc, sb.ToString());
    }

    private static (string, string)? ControlStep(UiControl c)
    {
        var type = c.Type.ToLowerInvariant();
        var name = !string.IsNullOrWhiteSpace(c.Text) && !c.Text.StartsWith('{') ? $"'{c.Text}'" : Md.Code(c.Name.Length > 0 ? c.Name : c.Type);
        var handler = c.Events.Count > 0 ? $" Handler: {string.Join(", ", c.Events.Select(Md.Code))}." : "";
        if (type.Contains("button") && !type.Contains("radio"))
            return ($"Click the {name} button.", $"The button responds as its label says.{handler}");
        if (type.Contains("text") && (type.Contains("box") || type.Contains("edit")) || type is "edittext" or "edit_box")
            return ($"Type a valid value into {name}, then an invalid one (empty, letters in a number field).", $"Valid input is kept; invalid input is rejected or flagged.{handler}");
        if (type.Contains("combo") || type.Contains("popup_list") || type.Contains("dropdown"))
            return ($"Open {name} and pick each option.", $"The list has the expected items; the choice sticks.{handler}");
        if (type.Contains("check") || type.Contains("toggle"))
            return ($"Toggle {name} on and off.", $"The state changes and is applied.{handler}");
        if (type.Contains("radio"))
            return ($"Select {name}.", $"It becomes the only selected option in its group.{handler}");
        if (type.Contains("list") || type.Contains("grid") || type.Contains("tree"))
            return ($"Look at {name} and select a row.", $"It lists the expected items; selection works.{handler}");
        if (type.Contains("slider") || type.Contains("trackbar") || type.Contains("slider"))
            return ($"Move {name} to both ends.", $"The value follows the slider.{handler}");
        return null;
    }

    // ------------------------------------------------------------ ribbons

    private void WriteRibbonSuite(RibbonTab tab)
    {
        var doc = RibbonTestDoc(ix, tab);
        var sb = new StringBuilder($"# Manual tests: ribbon tab {tab.Title}\n\n{Header}\n");
        sb.Append($"![{ix.TabPictureName(tab)}]({Md.Rel(doc, ix.TabAsset[tab])})\n\n");
        sb.Append($"Setup: {string.Join(" → ", ix.ReachTab(tab))}.\n\n");
        sb.Append(Md.Table(new[] { "#", "Action", "Expected result", "Pass/Fail" }, new[]
        {
            new[] { "0", "Look at the tab.", $"Panels in this order: {string.Join(", ", tab.Panels.Select(p => p.Title))}. Labels match the wireframe.", "" },
        }));
        int n = 0;
        foreach (var p in tab.Panels)
        {
            sb.Append($"\n## Panel: {p.Title}\n\n");
            var rows = new List<string?[]>();
            foreach (var (panel, item, parent, slide) in Linker.Walk(tab).Where(w => w.panel == p))
            {
                if (item.Kind is RibbonItemKind.Separator or RibbonItemKind.RowPanel) continue;
                if (item.Kind == RibbonItemKind.SplitButton && item.Children.Count > 0) continue; // children get their own rows
                var label = ModelIndex.Label(item);
                var how = (slide ? "Expand the slide-out, then " : "") +
                          (parent != null ? $"open '{ModelIndex.Label(parent)}' ▾ and choose '{label}'" : $"click '{label}'");
                var entries = ix.EntriesFor(item).ToList();
                var expected = entries.Count > 0
                    ? $"Runs {string.Join(", ", entries.Select(e => $"[{Md.Cell(e.Name)}]({Md.Rel(doc, CommandTestDoc(e.Project))}#{ix.TestId[e].ToLowerInvariant()})"))}; continue with that test case."
                    : item.Command != null ? $"Command {Md.Code(item.Command)} starts (the command line shows its first prompt or dialog)."
                    : item.Macro != null ? $"Macro {Md.Code(DocsWriter.Trunc(item.Macro, 60))} runs."
                    : item.Kind is RibbonItemKind.ToggleButton ? "The toggle changes state and its setting applies."
                    : "The control responds (no command bound in source).";
                if (item.Tooltip != null) expected += $" Hover tooltip: \"{Md.Cell(item.Tooltip)}\".";
                rows.Add(new[] { $"R{++n}", how[..1].ToUpperInvariant() + how[1..] + ".", expected, "" });
            }
            sb.Append(rows.Count == 0 ? "No buttons.\n" : Md.Table(new[] { "#", "Action", "Expected result", "Pass/Fail" }, rows));
        }
        sink.Write(doc, sb.ToString());
    }

    // ------------------------------------------------------------ index

    private void WriteIndex()
    {
        const string doc = DocsLayout.TestPlans + "/index.md";
        var sb = new StringBuilder("# Manual test suites\n\n");
        sb.Append("Generated from the same scan as the docs. Each case links back to the source it tests.\n\n");
        sb.Append("## Commands\n\n");
        sb.Append(Md.Table(new[] { "Project", "Cases" }, M.Commands.GroupBy(c => c.Project).OrderBy(g => g.Key)
            .Select(g => new[] { $"[{g.Key}]({Md.Rel(doc, CommandTestDoc(g.Key))})", g.Count().ToString() })));
        sb.Append("\n## Dialogs and palettes\n\n");
        sb.Append(Md.Table(new[] { "Project", "Cases" }, M.Forms.GroupBy(f => f.Project).OrderBy(g => g.Key)
            .Select(g => new[] { $"[{g.Key}]({Md.Rel(doc, FormTestDoc(g.Key))})", g.Count().ToString() })));
        sb.Append("\n## Ribbon tabs\n\n");
        var tabs = M.CodeTabs.Concat(ix.Options.CuixTests ? M.Cuix.SelectMany(c => c.Tabs) : []).ToList();
        sb.Append(Md.Table(new[] { "Tab", "Origin", "Buttons" }, tabs.Select(t => new[]
        {
            $"[{Md.Cell(t.Title)}]({Md.Rel(doc, RibbonTestDoc(ix, t))})", t.Origin == RibbonOrigin.Code ? $"code ({t.Container})" : t.Container,
            Linker.Walk(t).Count(w => w.item.Kind is not (RibbonItemKind.Separator or RibbonItemKind.RowPanel)).ToString(),
        })));
        if (!ix.Options.CuixTests) sb.Append("\nStock CUIX ribbon suites were skipped (--no-cuix-tests).\n");
        sink.Write(doc, sb.ToString());
    }
}
