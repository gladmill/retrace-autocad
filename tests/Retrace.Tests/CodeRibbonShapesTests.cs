using Retrace.Engine.Scan.CodeRibbon;

namespace Retrace.Tests;

/// <summary>
/// Shapes of ribbon-building code that CodeRibbonReader follows, each with a small invented fixture:
/// string fields, a panel method beside the tab's creator, a factory result kept in a variable, a factory picked by its class,
/// and text built from parts. Each test fails when the matching step of the reader is taken out.
/// </summary>
public class CodeRibbonShapesTests
{
    // ------------------------------------------------------------------ string fields

    [Fact]
    public void StringFieldsFillTabPanelAndButtonText()
    {
        const string code = """
            using Autodesk.Windows;
            namespace Demo.Ui {
              public class Names {
                public const string TabId = "GardenTab";
                public static readonly string SeedsTitle = "Seeds";
                public static readonly string SowText = "Sow";
                public static readonly string SowId = "SowSeeds";
              }
              public class GardenRibbon {
                private const string TabTitle = "Garden";
                public static RibbonTab Build() {
                  var tab = new RibbonTab { Title = TabTitle, Id = Names.TabId };
                  tab.Panels.Add(Seeds());
                  return tab;
                }
                private static RibbonPanel Seeds() {
                  var src = new RibbonPanelSource { Title = Names.SeedsTitle };
                  src.Items.Add(new RibbonButton { Text = Names.SowText, Id = Names.SowId });
                  var p = new RibbonPanel(); p.Source = src; return p;
                }
              }
            }
            """;
        var f = new SourceFile { AbsPath = "/x/Garden.cs", RelPath = "Proj/Garden.cs", Lang = Lang.CSharp, Project = "Proj", Text = code };
        var tab = Assert.Single(CodeRibbonReader.Read(new[] { f }));
        Assert.Equal("GardenTab", tab.Id); // Names.TabId: a const read through its class
        Assert.Equal("Garden", tab.Title); // TabTitle: a bare name
        var panel = Assert.Single(tab.Panels);
        Assert.Equal("Seeds", panel.Title); // a static readonly field
        var button = Assert.Single(Assert.Single(panel.Rows).Items);
        Assert.Equal("Sow", button.Text);
        Assert.Equal("SowSeeds", button.Id);
    }

    [Fact]
    public void AFieldOfTheSameFileWinsOverTheSameNameElsewhere()
    {
        const string first = """
            using Autodesk.Windows;
            namespace Demo.Ui {
              public class FirstRibbon {
                private const string Caption = "First";
                public static RibbonTab Build() {
                  return new RibbonTab { Title = Caption, Id = "FirstTab" };
                }
              }
            }
            """;
        const string second = """
            using Autodesk.Windows;
            namespace Demo.Ui {
              public class SecondRibbon {
                private const string Caption = "Second";
                public static RibbonTab Build() {
                  return new RibbonTab { Title = Caption, Id = "SecondTab" };
                }
              }
            }
            """;
        var a = new SourceFile { AbsPath = "/x/First.cs", RelPath = "Proj/First.cs", Lang = Lang.CSharp, Project = "Proj", Text = first };
        var b = new SourceFile { AbsPath = "/x/Second.cs", RelPath = "Proj/Second.cs", Lang = Lang.CSharp, Project = "Proj", Text = second };
        var tabs = CodeRibbonReader.Read(new[] { a, b });
        Assert.Equal(new[] { "First", "Second" }, tabs.Select(t => t.Title));
    }

    // ------------------------------------------------------------------ panels added by a sibling method

    [Fact]
    public void ASiblingMethodThatAddsPanelsBelongsToTheOnlyTabOfItsClass()
    {
        // NewTab() only makes the tab. FillPanels(tab) is not called from it, but it sits in the same class,
        // and the class makes one tab, so its panels are that tab's. A panel method of another class is not.
        const string code = """
            using Autodesk.Windows;
            namespace Demo.Ui {
              public class GardenRibbon {
                public static RibbonTab NewTab() {
                  return new RibbonTab { Title = "Garden", Id = "GardenTab" };
                }
                public static RibbonTab Build() {
                  var tab = NewTab();
                  FillPanels(tab);
                  return tab;
                }
                private static void FillPanels(RibbonTab tab) {
                  tab.Panels.Add(Seeds());
                  tab.Panels.Add(Tools());
                }
                private static RibbonPanel Seeds() {
                  var src = new RibbonPanelSource { Title = "Seeds" };
                  src.Items.Add(new RibbonButton { Text = "Sow", Id = "SowSeeds" });
                  var p = new RibbonPanel(); p.Source = src; return p;
                }
                private static RibbonPanel Tools() {
                  var src = new RibbonPanelSource { Title = "Tools" };
                  src.Items.Add(new RibbonButton { Text = "Rake", Id = "RakeBeds" });
                  var p = new RibbonPanel(); p.Source = src; return p;
                }
              }
              public class Elsewhere {
                public static void AddOdd(RibbonTab tab) { tab.Panels.Add(Odd()); }
                public static RibbonPanel Odd() {
                  var src = new RibbonPanelSource { Title = "Odd" };
                  var p = new RibbonPanel(); p.Source = src; return p;
                }
              }
            }
            """;
        var f = new SourceFile { AbsPath = "/x/Garden.cs", RelPath = "Proj/Garden.cs", Lang = Lang.CSharp, Project = "Proj", Text = code };
        var tab = Assert.Single(CodeRibbonReader.Read(new[] { f }));
        Assert.Equal("GardenTab", tab.Id);
        Assert.Equal(new[] { "Seeds", "Tools" }, tab.Panels.Select(p => p.Title));
        Assert.Equal("Sow", Assert.Single(tab.Panels[0].Rows).Items[0].Text);
        Assert.Equal("Rake", Assert.Single(tab.Panels[1].Rows).Items[0].Text);
    }

    [Fact]
    public void NoSiblingPanelsAreGuessedWhenAClassMakesSeveralTabs()
    {
        // Two tabs in one class: FillAny(tab) could belong to either, so neither gets it.
        // The first tab still gets what its own creator hands the tab to.
        const string code = """
            using Autodesk.Windows;
            namespace Demo.Ui {
              public class GardenRibbon {
                public static RibbonTab NewFirst() {
                  var tab = new RibbonTab { Title = "First", Id = "FirstTab" };
                  FillFirst(tab);
                  return tab;
                }
                public static RibbonTab NewSecond() {
                  return new RibbonTab { Title = "Second", Id = "SecondTab" };
                }
                private static void FillFirst(RibbonTab tab) {
                  tab.Panels.Add(Seeds());
                }
                private static void FillAny(RibbonTab tab) {
                  tab.Panels.Add(Tools());
                }
                private static RibbonPanel Seeds() {
                  var src = new RibbonPanelSource { Title = "Seeds" };
                  var p = new RibbonPanel(); p.Source = src; return p;
                }
                private static RibbonPanel Tools() {
                  var src = new RibbonPanelSource { Title = "Tools" };
                  var p = new RibbonPanel(); p.Source = src; return p;
                }
              }
            }
            """;
        var f = new SourceFile { AbsPath = "/x/Garden.cs", RelPath = "Proj/Garden.cs", Lang = Lang.CSharp, Project = "Proj", Text = code };
        var tabs = CodeRibbonReader.Read(new[] { f });
        Assert.Equal(new[] { "First", "Second" }, tabs.Select(t => t.Title));
        Assert.Equal(new[] { "Seeds" }, tabs[0].Panels.Select(p => p.Title));
        Assert.Empty(tabs[1].Panels);
    }

    // ------------------------------------------------------------------ factory result kept in a variable

    [Fact]
    public void AFactoryResultKeptInAVariableIsStillResolved()
    {
        // Rake(): the factory's button is returned through a variable.
        // Beds(): the factory's button is added through a variable, and a later assignment changes its tooltip.
        const string code = """
            using Autodesk.Windows;
            namespace Demo.Ui {
              public class Tools {
                public static RibbonButton MakeButton(string label, string key, string hint) {
                  return new RibbonButton { Text = label, Id = key, ToolTip = hint };
                }
              }
              public class GardenRibbon {
                public static RibbonTab Build() {
                  var tab = new RibbonTab { Title = "Garden", Id = "GardenTab" };
                  tab.Panels.Add(Beds());
                  return tab;
                }
                private static RibbonButton Rake() {
                  var b = Tools.MakeButton("Rake", "RakeBeds", "Rakes the beds");
                  return b;
                }
                private static RibbonPanel Beds() {
                  var src = new RibbonPanelSource { Title = "Beds" };
                  var hoe = Tools.MakeButton("Hoe", "BreakSoil", "Breaks the soil");
                  hoe.ToolTip = "Breaks hard soil";
                  src.Items.Add(hoe);
                  src.Items.Add(Rake());
                  var p = new RibbonPanel(); p.Source = src; return p;
                }
              }
            }
            """;
        var f = new SourceFile { AbsPath = "/x/Garden.cs", RelPath = "Proj/Garden.cs", Lang = Lang.CSharp, Project = "Proj", Text = code };
        var tab = Assert.Single(CodeRibbonReader.Read(new[] { f }));
        var items = Assert.Single(tab.Panels).Rows.SelectMany(r => r.Items).ToList();
        Assert.Equal(2, items.Count);

        var hoe = items[0];
        Assert.Equal("Hoe", hoe.Text);
        Assert.Equal("BreakSoil", hoe.Id);
        Assert.Equal("Breaks hard soil", hoe.Tooltip);
        Assert.False(hoe.Extra.ContainsKey("unresolved"));

        var rake = items[1];
        Assert.Equal("Rake", rake.Text);
        Assert.Equal("RakeBeds", rake.Id);
        Assert.Equal("Rakes the beds", rake.Tooltip);
        Assert.False(rake.Extra.ContainsKey("unresolved"));
    }

    // ------------------------------------------------------------------ factory picked by its class

    [Fact]
    public void TheClassQualifierPicksTheFactory()
    {
        // Both factories are called Make and take two arguments. Only the qualifier tells them apart,
        // written plain (SeedButtons.Make) or with its namespace (Demo.Ui.ToolButtons.Make).
        const string code = """
            using Autodesk.Windows;
            namespace Demo.Ui {
              public class SeedButtons {
                public static RibbonButton Make(string label, string key) {
                  return new RibbonButton { Text = label, Id = key, ToolTip = "Seed button" };
                }
              }
              public class ToolButtons {
                public static RibbonButton Make(string label, string key) {
                  return new RibbonButton { Text = label, Id = key, ToolTip = "Tool button" };
                }
              }
              public class GardenRibbon {
                public static RibbonTab Build() {
                  var tab = new RibbonTab { Title = "Garden", Id = "GardenTab" };
                  tab.Panels.Add(Beds());
                  return tab;
                }
                private static RibbonPanel Beds() {
                  var src = new RibbonPanelSource { Title = "Beds" };
                  src.Items.Add(SeedButtons.Make("Sow", "SowSeeds"));
                  src.Items.Add(Demo.Ui.ToolButtons.Make("Rake", "RakeBeds"));
                  var p = new RibbonPanel(); p.Source = src; return p;
                }
              }
            }
            """;
        var f = new SourceFile { AbsPath = "/x/Garden.cs", RelPath = "Proj/Garden.cs", Lang = Lang.CSharp, Project = "Proj", Text = code };
        var tab = Assert.Single(CodeRibbonReader.Read(new[] { f }));
        var items = Assert.Single(tab.Panels).Rows.SelectMany(r => r.Items).ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal(("Sow", "SowSeeds", "Seed button"), (items[0].Text, items[0].Id, items[0].Tooltip));
        Assert.Equal(("Rake", "RakeBeds", "Tool button"), (items[1].Text, items[1].Id, items[1].Tooltip));
    }

    // ------------------------------------------------------------------ text built from parts

    [Fact]
    public void TextBuiltFromPartsLosesTheSpacesAtItsEnds()
    {
        const string code = """
            using Autodesk.Windows;
            namespace Demo.Ui {
              public class GardenRibbon {
                public static RibbonTab Build() {
                  var tab = new RibbonTab { Title = " " + "Garden" + " ", Id = "GardenTab" };
                  tab.Panels.Add(Beds());
                  return tab;
                }
                private static RibbonPanel Beds() {
                  var src = new RibbonPanelSource { Title = "Beds" };
                  src.Items.Add(new RibbonButton { Text = " " + "Sow" + " ", Id = "SowSeeds", ToolTip = " " + "Sows a row" + " " });
                  var p = new RibbonPanel(); p.Source = src; return p;
                }
              }
            }
            """;
        var f = new SourceFile { AbsPath = "/x/Garden.cs", RelPath = "Proj/Garden.cs", Lang = Lang.CSharp, Project = "Proj", Text = code };
        var tab = Assert.Single(CodeRibbonReader.Read(new[] { f }));
        var button = Assert.Single(Assert.Single(Assert.Single(tab.Panels).Rows).Items);
        Assert.Equal("Sow", button.Text);
        // The button text is trimmed a second time on its own; a tooltip and a tab title are only trimmed where the parts are joined.
        Assert.Equal("Sows a row", button.Tooltip);
        Assert.Equal("Garden", tab.Title);
    }

    [Fact]
    public void ButtonTextIsTrimmedEvenWhenItIsOneLiteral()
    {
        const string code = """
            using Autodesk.Windows;
            namespace Demo.Ui {
              public class GardenRibbon {
                public static RibbonTab Build() {
                  var tab = new RibbonTab { Title = "Garden", Id = "GardenTab" };
                  tab.Panels.Add(Beds());
                  return tab;
                }
                private static RibbonPanel Beds() {
                  var src = new RibbonPanelSource { Title = "Beds" };
                  src.Items.Add(new RibbonButton { Text = "  Water  ", Id = "WaterRows" });
                  var p = new RibbonPanel(); p.Source = src; return p;
                }
              }
            }
            """;
        var f = new SourceFile { AbsPath = "/x/Garden.cs", RelPath = "Proj/Garden.cs", Lang = Lang.CSharp, Project = "Proj", Text = code };
        var tab = Assert.Single(CodeRibbonReader.Read(new[] { f }));
        var button = Assert.Single(Assert.Single(Assert.Single(tab.Panels).Rows).Items);
        Assert.Equal("Water", button.Text);
    }
}
