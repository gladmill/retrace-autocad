using Autodesk.Windows;

namespace Retrace.AutoCad;

/// <summary>
/// The Retrace ribbon tab, built in code with AdWindows. It is kept in the plain shape that Retrace's own code-ribbon scanner
/// reads (Engine/Scan/CodeRibbon/CodeRibbonReader.cs), so Build docs on this source documents the tab too: the tab's Id,
/// Title and Name set from literals or a string constant, one method per panel, and every button made by <see cref="Button"/>
/// with literal arguments. CommandNamesTests checks that the scanner still reads it.
/// A button's Id is the command it runs.
/// </summary>
public static class RetraceTab
{
    /// <summary>The tab's Id. Must equal <c>OwnCommands.TabId</c>.</summary>
    public const string TabId = "RETRACE_TAB";

    /// <summary>A new Retrace tab with all its panels.</summary>
    public static RibbonTab Build()
    {
        var tab = new RibbonTab();
        tab.Id = TabId;
        tab.Title = "Retrace";
        tab.Name = "Retrace";
        tab.Panels.Add(RecordPanel());
        tab.Panels.Add(TestPanel());
        tab.Panels.Add(GuidePanel());
        tab.Panels.Add(OpenPanel());
        tab.Panels.Add(ToolsPanel());
        return tab;
    }

    static RibbonPanel RecordPanel()
    {
        var src = new RibbonPanelSource();
        src.Title = "Record";
        src.Items.Add(Button("RETRACERECORD", "Record", "record", "^C^C_RETRACERECORD ", "Start recording a manual test. Asks for a name."));
        src.Items.Add(Button("RETRACEMARK", "Checkpoint", "flag", "^C^C_RETRACEMARK ", "Add a checkpoint now: a snapshot of the drawing that is checked when the test is run. No typing: it is named for you (checkpoint1_draw_grid_button_ok); rename it after Stop."));
        src.Items.Add(Button("RETRACESTOP", "Stop", "stop", "^C^C_RETRACESTOP ", "Stop recording, close the recording and write steps.md."));
        src.Items.Add(Button("RETRACECHECKPOINTS", "Name checkpoints", "flag", "^C^C_RETRACECHECKPOINTS ", "See the checkpoints of the newest test with their pictures and rename them."));
        src.Items.Add(Button("RETRACESTEPS", "Steps", "book", "^C^C_RETRACESTEPS ", "Open the steps and screenshots written for the newest recording."));
        src.Items.Add(Button("RETRACESTATUS", "Status", "info", "^C^C_RETRACESTATUS ", "Show whether a recording is running, and where files go."));
        return Wrap(src);
    }

    static RibbonPanel TestPanel()
    {
        var src = new RibbonPanelSource();
        src.Title = "Test";
        src.Items.Add(Button("RETRACERUN", "Run test", "play", "^C^C_RETRACERUN ", "Pick recording.jsonl in a test folder, open a new drawing, replay it there and show the verdict."));
        src.Items.Add(Button("RETRACESTEP", "Step", "step", "^C^C_RETRACESTEP ", "Pick a recording and replay it one step at a time in this drawing."));
        return Wrap(src);
    }

    static RibbonPanel GuidePanel()
    {
        var src = new RibbonPanelSource();
        src.Title = "Guide";
        src.Items.Add(Button("RETRACEGUIDE", "Guide", "book", "^C^C_RETRACEGUIDE ", "Read and edit the guide in a palette inside AutoCAD."));
        src.Items.Add(Button("RETRACEDOCS", "Build docs", "build", "^C^C_RETRACEDOCS ", "Run the engine on your code, captures and recordings and write the guide, reference and test plans."));
        src.Items.Add(Button("RETRACESETTINGS", "Settings", "sliders", "^C^C_RETRACESETTINGS ", "Set the folders and the editor."));
        return Wrap(src);
    }

    static RibbonPanel OpenPanel()
    {
        var src = new RibbonPanelSource();
        src.Title = "Open";
        src.Items.Add(Button("RETRACEOPEN", "Open test", "opentest", "^C^C_RETRACEOPEN ", "Pick one of your recorded tests and open its steps, Word or Excel file, screenshots or results, or run it."));
        src.Items.Add(Button("RETRACEFOLDER_TESTS", "Tests", "open", "^C^C_RETRACEFOLDER Tests ", "Open the tests folder: one folder per recorded test."));
        src.Items.Add(Button("RETRACEFOLDER_CAPTURES", "Captures", "open", "^C^C_RETRACEFOLDER Captures ", "Open the folder with the dialog captures."));
        src.Items.Add(Button("RETRACEFOLDER_DOCS", "Docs", "open", "^C^C_RETRACEFOLDER Docs ", "Open the folder the docs are written to."));
        return Wrap(src);
    }

    static RibbonPanel ToolsPanel()
    {
        var src = new RibbonPanelSource();
        src.Title = "Tools";
        src.Items.Add(Button("RETRACECAPTUREDCL", "Capture DCL dialogs", "camera", "^C^C_RETRACECAPTUREDCL ", "Open each DCL dialog and save a picture of it."));
        src.Items.Add(Button("RETRACECLOSEALL", "Close all drawings", "closeall", "^C^C_RETRACECLOSEALL ", "Close every open drawing without saving (asks once first). Handy between test runs."));
        return Wrap(src);
    }

    static RibbonPanel Wrap(RibbonPanelSource src)
    {
        var panel = new RibbonPanel();
        panel.Source = src;
        return panel;
    }

    /// <summary>A large button with its icon above the label. <paramref name="macro"/> goes to <see cref="RibbonCommandHandler"/>.</summary>
    static RibbonButton Button(string id, string label, string icon, string macro, string tip)
    {
        var b = new RibbonButton();
        b.Id = id;
        b.Text = label;
        b.ToolTip = tip;
        b.Description = tip;
        b.Size = RibbonItemSize.Large;
        b.Orientation = System.Windows.Controls.Orientation.Vertical;
        b.Image = RibbonIcons.Make(icon, 16);
        b.LargeImage = RibbonIcons.Make(icon, 32);
        b.ShowImage = true;
        b.ShowText = true;
        b.CommandParameter = macro;
        b.CommandHandler = new RibbonCommandHandler();
        return b;
    }
}
