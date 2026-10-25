using System.IO.Compression;
using System.Xml.Linq;
using Retrace.Engine.Scan.CodeRibbon;
using Retrace.Engine.Scan.Commands;
using Retrace.Engine.Scan.Cuix;

namespace Retrace.Tests;

public class CuixRibbonTests
{
    [Theory]
    [InlineData("^C^C_mtext ", "MTEXT")]
    [InlineData("^C^C._-insert ", "-INSERT")]
    [InlineData("'_zoom", "ZOOM")]
    [InlineData("^C^C_zoom;_extents;", "ZOOM")]
    [InlineData("*^C^C_line", "LINE")]
    [InlineData("^C^C_+purge 1", "PURGE")]
    [InlineData("$M=$(if,$(eq,1,1),^C^C_line)", null)]
    [InlineData("^C^C(ai_tiledvp 1 nil)", null)]
    [InlineData("^B ", null)]
    [InlineData("^C^C ", null)]
    [InlineData("", null)]
    public void ParseCommand(string macro, string? expected) => Assert.Equal(expected, CuixReader.ParseCommand(macro));

    private const string MenuGroup = @"<MenuGroup Name=""TST"" DisplayName=""Test"">
  <MacroGroup Name=""M"">
    <MenuMacro UID=""ID_Line""><Macro type=""Draw""><Name>Line</Name><Command>^C^C_line </Command><HelpString>Draws a line</HelpString><SmallImage Name=""S_LINE""/><LargeImage Name=""L_LINE""/></Macro></MenuMacro>
    <MenuMacro UID=""ID_Circle""><Macro type=""Draw""><Name>Circle Center</Name><Command>^C^C._circle </Command></Macro></MenuMacro>
    <MenuMacro UID=""ID_Diesel""><Macro type=""Any""><Name>Diesel</Name><Command>$M=$(getvar,x)</Command></Macro></MenuMacro>
  </MacroGroup>
</MenuGroup>";

    private const string Ribbon = @"<RibbonRoot>
  <RibbonPanelSourceCollection>
    <RibbonPanelSource UID=""P1"" Text=""Draw"">
      <Name>Draw Panel</Name>
      <DialogBoxLauncher UID=""L1"" CommandID=""ID_Line""/>
      <RibbonRow UID=""R1"">
        <RibbonSplitButton UID=""SB1"" Text=""Circle&#xD;Tools"" ButtonStyle=""LargeWithText"">
          <RibbonCommandButton UID=""B_C"" Text="""" ButtonStyle=""SmallWithoutText"" MenuMacroID=""ID_Circle""/>
        </RibbonSplitButton>
        <RibbonRowPanel UID=""RP1"">
          <RibbonRow UID=""R2"">
            <RibbonCommandButton UID=""B_L"" Text=""Line"" ButtonStyle=""SmallWithText"" MenuMacroID=""ID_Line"">
              <TooltipTitle>Line tip</TooltipTitle>
            </RibbonCommandButton>
            <RibbonSeparator UID=""SEP1""/>
          </RibbonRow>
        </RibbonRowPanel>
      </RibbonRow>
      <RibbonPanelBreak UID=""BRK""/>
      <RibbonRow UID=""R3"">
        <RibbonCommandButton UID=""B_D"" Text=""Hidden Diesel"" MenuMacroID=""ID_Diesel""/>
      </RibbonRow>
    </RibbonPanelSource>
    <RibbonPanelSource UID=""P2"" Text=""Misc"">
      <RibbonRow UID=""R4""><RibbonCommandButton UID=""B_X"" Text=""Plain"" ButtonStyle=""LargeWithText""/></RibbonRow>
    </RibbonPanelSource>
  </RibbonPanelSourceCollection>
  <RibbonTabSourceCollection>
    <RibbonTabSource UID=""T1"" Text=""Home "" KeyTip=""H"">
      <Name>Home - 2D</Name>
      <RibbonPanelSourceReference PanelId=""P1""/>
      <RibbonPanelSourceReference PanelId=""P2""/>
    </RibbonTabSource>
    <RibbonTabSource UID=""T2"" Text=""Ctx""><Name>Ctx</Name><RibbonPanelSourceReference PanelId=""P2""/></RibbonTabSource>
  </RibbonTabSourceCollection>
  <RibbonTabSelectors><RibbonTabSelector Rule=""AcCtx"" Tabs=""T2""/></RibbonTabSelectors>
</RibbonRoot>";

    private const string Workspace = @"<WorkspaceRoot>
  <WorkspaceConfigRoot><WorkspaceConfig UID=""c""><Name>Cfg</Name></WorkspaceConfig></WorkspaceConfigRoot>
  <Workspace UID=""W1""><Name>Drafting</Name><WSRibbonRoot>
    <WSRibbonTabSourceReference TabId=""T1"" Show=""true""/>
    <WSRibbonTabSourceReference TabId=""T2"" Show=""false""/></WSRibbonRoot></Workspace>
</WorkspaceRoot>";

    private const string Header = "<CustSection><Header><CommonConfiguration><CommonItems><PartialMenuFile>a.cuix</PartialMenuFile></CommonItems></CommonConfiguration></Header></CustSection>";

    private static string MakeCuix()
    {
        var path = Path.Combine(Path.GetTempPath(), "retrace-test-" + Guid.NewGuid().ToString("N") + ".cuix");
        using (var fs = File.Create(path))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            void Add(string n, string c) { using var w = new StreamWriter(zip.CreateEntry(n).Open()); w.Write(c); }
            Add("MenuGroup.cui", MenuGroup);
            Add("RibbonRoot.cui", Ribbon);
            Add("WorkspaceRoot.cui", Workspace);
            Add("Header.cui", Header);
        }
        return path;
    }

    [Fact]
    public void ReadsTinyCuix()
    {
        var path = MakeCuix();
        try
        {
            var m = CuixReader.Read(path, "t.cuix");
            Assert.Equal("TST", m.MenuGroup);
            Assert.Equal(new[] { "a.cuix" }, m.PartialMenus);
            Assert.Equal(new[] { "Drafting" }, m.Workspaces);
            Assert.Equal(2, m.Tabs.Count);
            var home = m.Tabs[0];
            Assert.Equal("Home", home.Title);
            Assert.Equal("Home - 2D", home.DisplayName);
            Assert.Equal(new[] { "Drafting" }, home.Workspaces);
            Assert.Equal(2, home.Panels.Count);
            Assert.Equal("AcCtx", m.Tabs[1].ContextualRule);
            Assert.Empty(m.Tabs[1].Workspaces);

            var draw = home.Panels[0];
            Assert.Equal("ID_Line", draw.DialogLauncherCommand);
            Assert.Equal("t.cuix!RibbonRoot.cui", draw.Source!.File);
            Assert.True(draw.Source.Line > 1);
            Assert.False(draw.Rows[0].IsSlideout);
            Assert.True(draw.Rows[1].IsSlideout);

            var split = draw.Rows[0].Items[0];
            Assert.Equal(RibbonItemKind.SplitButton, split.Kind);
            Assert.Equal("Circle Tools", split.Text);
            Assert.Equal(RibbonItemSize.Large, split.Size);
            var child = Assert.Single(split.Children);
            Assert.Equal("CIRCLE", child.Command);
            Assert.Equal("Circle Center", child.Text);
            Assert.False(child.ShowText);

            var rp = draw.Rows[0].Items[1];
            Assert.Equal(RibbonItemKind.RowPanel, rp.Kind);
            var line = rp.SubRows[0].Items[0];
            Assert.Equal("LINE", line.Command);
            Assert.Equal("^C^C_line", line.Macro);
            Assert.Equal("S_LINE", line.Image);
            Assert.Equal(RibbonItemKind.Separator, rp.SubRows[0].Items[1].Kind);
            Assert.Null(draw.Rows[1].Items[0].Command);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RendersParsableSvgWithAllText()
    {
        var path = MakeCuix();
        try
        {
            var m = CuixReader.Read(path, "t.cuix");
            var svg = RibbonRenderer.Render(m.Tabs[0]);
            var doc = XDocument.Parse(svg);
            string all = string.Concat(doc.Descendants().Where(e => e.Name.LocalName == "text").Select(e => e.Value + "|"));
            foreach (var s in new[] { "Home", "Circle", "Tools", "Line", "Plain", "Draw", "Misc" })
                Assert.Contains(s, all);
            Assert.Contains("Draw ▾", all); // slide-out marker
            Assert.Contains("↘", all);       // dialog launcher
            Assert.DoesNotContain("Hidden Diesel", all); // slide-out rows are not drawn
            Assert.Contains(doc.Descendants(), e => e.Attribute("data-source") != null);
        }
        finally { File.Delete(path); }
    }

    // ------------------------------------------------------------------ code ribbons

    private const string CodeSample = @"
using Autodesk.Windows;
namespace Demo.Ui {
  public class ActionFactory {
    public static RibbonButton Make(string caption, string key, Bitmap icon, string hint, string summary) {
      RibbonButton btn = new RibbonButton { Text = caption, Size = RibbonItemSize.Large };
      btn.Description = summary;
      btn.Id = key;
      btn.ToolTip = hint;
      btn.CommandHandler = new TypedCommandRunner();
      return btn;
    }
  }
  public class TypedCommandRunner {
    public void Execute(object p) { Doc().SendStringToExecute(((RibbonButton)p).Id + "" "", false, false, true); }
  }
  public class LinkFactory {
    public static RibbonButton Make(string caption, string asm, string typeName, string member) {
      var b = new RibbonButton();
      b.Text = caption;
      var target = new HandlerRef(asm, typeName, member, 0);
      b.CommandParameter = target;
      return b;
    }
  }
  public class DemoRibbon {
    public static RibbonTab Assemble() {
      var t = new RibbonTab { Title = ""Mine"", Id = ""TAB_Mine"" };
      Fill(t);
      return t;
    }
    private static void Fill(RibbonTab t) {
      var first = BuildFirst();
      t.Panels.Add(first);
      //var dropped = BuildDropped();
      //t.Panels.Add(dropped);
      t.Panels.Add(BuildSecond(""x""));
    }
    public static RibbonPanel BuildFirst() {
      var rowA = new RibbonRowPanel();
      var src = new RibbonPanelSource();
      src.Title = ""One"";
      src.Items.Add(rowA);
      rowA.Items.Add(ActionFactory.Make(""Alpha"", ""GD_Alpha"", Icons.alpha_16, ""tip a"", ""d""));
      rowA.Items.Add(new RibbonRowBreak());
      rowA.Items.Add(LinkButton());
      var p = new RibbonPanel(); p.Source = src; return p;
    }
    public static RibbonButton LinkButton() {
      return LinkFactory.Make(""Beta"", ""Demo.Asm"", ""Demo.Ui.Actions"", ""Launch"");
    }
    public static RibbonPanel BuildSecond(string v) {
      var src = new RibbonPanelSource { Title = ""Two"" };
      var stamp = new RibbonButton();
      stamp.Name = ""Stamp"";
      stamp.Text = ""["" + v + ""]"";
      src.Items.Add(stamp);
      src.Items.Add(new RibbonRowBreak());
      src.Items.Add(new RibbonButton { Text = ""Inline"", Id = ""inl"" });
      var p = new RibbonPanel(); p.Source = src; return p;
    }
    public static RibbonPanel BuildDropped() {
      var src = new RibbonPanelSource(); src.Title = ""Dropped""; var p = new RibbonPanel(); p.Source = src; return p;
    }
  }
}";

    [Fact]
    public void CodeRibbonReaderFollowsFactoriesAndHelpers()
    {
        var f = new SourceFile { AbsPath = "/x/DemoRibbon.cs", RelPath = "Proj/DemoRibbon.cs", Lang = Lang.CSharp, Project = "Proj", Text = CodeSample };
        var tabs = CodeRibbonReader.Read(new[] { f });
        var tab = Assert.Single(tabs);
        Assert.Equal("TAB_Mine", tab.Id);
        Assert.Equal("Mine", tab.Title);
        Assert.Equal(RibbonOrigin.Code, tab.Origin);
        Assert.Equal(new[] { "One", "Two" }, tab.Panels.Select(p => p.Title));
        Assert.All(tab.Panels, p => Assert.Equal("Proj/DemoRibbon.cs", p.Source!.File));

        var one = tab.Panels[0];
        var rp = Assert.Single(one.Rows).Items[0];
        Assert.Equal(RibbonItemKind.RowPanel, rp.Kind);
        Assert.Equal(2, rp.SubRows.Count);
        var alpha = rp.SubRows[0].Items[0];
        Assert.Equal("GD_Alpha", alpha.Id);
        Assert.Equal("Alpha", alpha.Text);
        Assert.Equal("tip a", alpha.Tooltip);
        Assert.Equal("d", alpha.Extra["description"]);
        Assert.Equal("GD_ALPHA", alpha.Command);
        Assert.Equal("Icons.alpha_16", alpha.Image);
        Assert.True(alpha.Source!.Line > 1);
        var beta = rp.SubRows[1].Items[0];
        Assert.Equal("Demo.Ui.Actions.Launch", beta.HandlerMethod);
        Assert.Equal("Demo.Asm", beta.Extra["assembly"]);
        Assert.Null(beta.Command);

        var two = tab.Panels[1];
        Assert.Equal(2, two.Rows.Count);
        Assert.Equal("[x]", two.Rows[0].Items[0].Text);
        Assert.Equal("Inline", two.Rows[1].Items[0].Text);
    }

    private const string HandlerRibbon = @"
using Autodesk.Windows;
namespace Demo.Ui {
  public class Tabs {
    public static RibbonTab Make() {
      var t = new RibbonTab { Title = ""T"", Id = ""TAB_T"" };
      t.Panels.Add(Only());
      return t;
    }
    public static RibbonPanel Only() {
      var src = new RibbonPanelSource { Title = ""P"" };
      src.Items.Add(new RibbonButton { Text = ""Go"", CommandParameter = new Options(""Demo.Asm"", ""Demo.Ui.Actions"", ""Go"") });
      src.Items.Add(new RibbonButton { Text = ""Bare"", CommandParameter = new Options(""Demo.Asm"", ""LooseActions"", ""RunGlobal"") });
      src.Items.Add(new RibbonButton { Text = ""Typed"", CommandParameter = new Handler<int>(""Demo.Asm"", ""Demo.Ui.Actions"", ""Typed"") });
      src.Items.Add(Link(""Linked"", new Options(""Demo.Asm"", ""Demo.Ui.Actions"", ""Linked"")));
      src.Items.Add(new RibbonButton { Text = ""Plain"", CommandParameter = new Options(""Layer"", ""Plot"", ""Name"") });
      src.Items.Add(new RibbonButton { Text = ""Door"", CommandParameter = new BlockInsert(""Library"", ""doors.dwg"", ""Door36"") });
      src.Items.Add(new RibbonButton { Text = ""Help"", CommandParameter = new HelpTopic(""Demo"", ""Demo.chm"", ""DrawGrid"") });
      var p = new RibbonPanel(); p.Source = src; return p;
    }
    static RibbonButton Link(string text, object target) {
      var b = new RibbonButton();
      b.Text = text;
      b.CommandParameter = target;
      return b;
    }
  }
}";
    private const string HandlerActions = @"
namespace Demo.Ui {
  public static class Actions {
    public static void Go() { }
    public static void Typed() { }
    public static void Linked() { }
    public static void DrawGrid() { }
  }
}
public static class LooseActions {
  public static void RunGlobal() { }
}";

    [Fact]
    public void CommandParameterObjectIsAHandlerOnlyWhenTheSourcesDeclareItsMethod()
    {
        var files = new[]
        {
            new SourceFile { AbsPath = "/x/Tabs.cs", RelPath = "Proj/Tabs.cs", Lang = Lang.CSharp, Project = "Proj", Text = HandlerRibbon },
            new SourceFile { AbsPath = "/x/Actions.cs", RelPath = "Proj/Actions.cs", Lang = Lang.CSharp, Project = "Proj", Text = HandlerActions },
        };
        var model = new EngineModel { InputRoot = "/x" };
        model.CodeTabs.AddRange(CodeRibbonReader.Read(files));
        var items = Assert.Single(Assert.Single(model.CodeTabs).Panels).Rows.SelectMany(r => r.Items).ToDictionary(i => i.Text);
        // the reader reports every object that reads as (assembly, class, method) ...
        Assert.Equal("doors.dwg.Door36", items["Door"].HandlerMethod);
        Assert.Equal("Demo.chm.DrawGrid", items["Help"].HandlerMethod);

        // ... and only the ones whose method the sources declare stay; a unique method name alone (DrawGrid) is not enough
        Linker.KeepDeclaredHandlers(model, new FlowAnalyzer(files, 4).Declares);
        Assert.Equal("Demo.Ui.Actions.Go", items["Go"].HandlerMethod);
        Assert.Equal("Demo.Asm", items["Go"].Extra["assembly"]);
        Assert.Equal("LooseActions.RunGlobal", items["Bare"].HandlerMethod);
        Assert.Equal("Demo.Ui.Actions.Typed", items["Typed"].HandlerMethod);
        Assert.Equal("Demo.Ui.Actions.Linked", items["Linked"].HandlerMethod);
        foreach (var name in new[] { "Plain", "Door", "Help" })
        {
            Assert.Null(items[name].HandlerMethod);
            Assert.False(items[name].Extra.ContainsKey("assembly"));
        }
        var warning = Assert.Single(model.Warnings);
        Assert.Contains("3 button(s)", warning);
        Assert.Contains("doors.dwg.Door36", warning);

        Linker.AddRibbonHandlers(model);
        Assert.Equal(new[] { "Demo.Ui.Actions.Go", "LooseActions.RunGlobal", "Demo.Ui.Actions.Typed", "Demo.Ui.Actions.Linked" },
            model.Commands.Select(c => c.Method));
    }

    [Fact]
    public void PipelineDropsHandlerObjectsTheSourcesDoNotDeclare()
    {
        var dir = Path.Combine(Path.GetTempPath(), "retrace-test-handlers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Tabs.cs"), HandlerRibbon);
            File.WriteAllText(Path.Combine(dir, "Actions.cs"), HandlerActions);
            var model = Pipeline.Analyze(new EngineOptions { Input = dir, Output = Path.Combine(dir, "out") });
            Assert.Equal(new[] { "Demo.Ui.Actions.Go", "LooseActions.RunGlobal", "Demo.Ui.Actions.Typed", "Demo.Ui.Actions.Linked" },
                model.Commands.Where(c => c.Kind == EntryKind.RibbonHandler).Select(c => c.Method));
            Assert.Contains(model.Warnings, w => w.Contains("doors.dwg.Door36"));
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
    }
}
