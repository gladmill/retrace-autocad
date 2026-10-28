using System.IO.Compression;
using Retrace.Engine.Scan.Cuix;

namespace Retrace.Tests;

/// <summary>Finding the customization files to document: legacy .cui files, the partials a file names, and the files AutoCAD has loaded
/// (EngineOptions.Customization). Synthetic files only.</summary>
public class CustomizationFilesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("retrace-cust").FullName;
    private string Src => Path.Combine(_root, "src");
    private string Loaded => Path.Combine(_root, "loaded");
    public void Dispose() => Directory.Delete(_root, true);

    static string Ribbon(string tab, string panel) => $"""
        <RibbonRoot><RibbonPanelSourceCollection><RibbonPanelSource UID="P_{panel}" Text="{panel}"><RibbonRow UID="R_{panel}">
          <RibbonCommandButton UID="B_{panel}" Text="Go" MenuMacroID="ID_Go"/></RibbonRow></RibbonPanelSource></RibbonPanelSourceCollection>
          <RibbonTabSourceCollection><RibbonTabSource UID="T_{tab}" Text="{tab}"><RibbonPanelSourceReference PanelId="P_{panel}"/></RibbonTabSource></RibbonTabSourceCollection></RibbonRoot>
        """;
    const string Menu = """<MenuGroup Name="G"><MacroGroup Name="M"><MenuMacro UID="ID_Go"><Macro type="Any"><Name>Go</Name><Command>^C^C_go </Command></Macro></MenuMacro></MacroGroup></MenuGroup>""";
    static string Header(params string[] partials) =>
        "<CustSection><Header><CommonConfiguration><CommonItems>" + string.Concat(partials.Select(p => $"<PartialMenuFile>{p}</PartialMenuFile>")) + "</CommonItems></CommonConfiguration></Header></CustSection>";

    static void Cuix(string path, string tab, params string[] partials)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        void Add(string n, string c) { using var w = new StreamWriter(zip.CreateEntry(n).Open()); w.Write(c); }
        Add("MenuGroup.cui", Menu);
        Add("RibbonRoot.cui", Ribbon(tab, tab + "Panel"));
        Add("Header.cui", Header(partials));
    }

    /// <summary>A legacy .cui: the same sections in one XML file.</summary>
    static void Cui(string path, string tab)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"<?xml version=\"1.0\"?>\n<CustSection>\n{Header()}\n{Menu}\n{Ribbon(tab, tab + "Panel")}\n</CustSection>\n");
    }

    EngineModel Analyze(params string[] customization) => Analyze(true, customization);

    EngineModel Analyze(bool follow, params string[] customization) =>
        Pipeline.Analyze(new EngineOptions { Input = Src, Output = Path.Combine(_root, "docs"), Customization = customization, FollowPartials = follow });

    [Fact]
    public void A_legacy_cui_file_is_read_with_its_ribbon_and_line_numbers()
    {
        Cui(Path.Combine(Src, "old.cui"), "Legacy");
        var c = Assert.Single(Analyze().Cuix);
        var tab = Assert.Single(c.Tabs);
        Assert.Equal("Legacy", tab.Title);
        Assert.Equal("old.cui", tab.Source!.File);
        Assert.True(tab.Source.Line > 1);
        Assert.Equal("^C^C_go", Assert.Single(c.Macros).Value.Macro);
    }

    [Fact]
    public void Partials_named_in_a_header_are_read_once_and_a_missing_one_is_a_warning()
    {
        Cuix(Path.Combine(Src, "main.cuix"), "Main", "part.cuix", "gone.cuix");
        Cuix(Path.Combine(Src, "part.cuix"), "Part", "main.cuix");   // under the source folder too, and naming main back
        var m = Analyze();
        Assert.Equal(new[] { "Main", "Part" }, m.Cuix.SelectMany(c => c.Tabs).Select(t => t.Title).OrderBy(t => t));
        Assert.Contains(m.Warnings, w => w.Contains("partial customization file not found") && w.Contains("gone.cuix"));
    }

    [Fact]
    public void Loaded_files_outside_the_source_folder_are_read_with_their_partials()
    {
        Directory.CreateDirectory(Src);
        Cuix(Path.Combine(Loaded, "acad.cuix"), "Home", "electrical.cuix");
        Cuix(Path.Combine(Loaded, "electrical.cuix"), "Schematic");
        Cui(Path.Combine(Loaded, "old.cui"), "Legacy");
        var m = Analyze(Path.Combine(Loaded, "acad.cuix"), Path.Combine(Loaded, "old.cui"), Path.Combine(Loaded, "acad.cuix"), Path.Combine(Loaded, "notes.txt"));
        Assert.Equal(new[] { "acad.cuix", "electrical.cuix", "old.cui" }, m.Cuix.Select(c => c.File).OrderBy(f => f));
        Assert.Equal(new[] { "Home", "Legacy", "Schematic" }, m.Cuix.SelectMany(c => c.Tabs).Select(t => t.Title).OrderBy(t => t));
    }

    [Fact]
    public void A_loaded_file_that_is_also_in_the_source_folder_is_read_once()
    {
        Cuix(Path.Combine(Src, "plugin.cuix"), "Plugin");
        Assert.Single(Analyze(Path.Combine(Src, "plugin.cuix")).Cuix);
    }

    [Fact]
    public void Two_loaded_files_with_the_same_name_keep_apart()
    {
        Directory.CreateDirectory(Src);
        Cuix(Path.Combine(Loaded, "a", "custom.cuix"), "A");
        Cuix(Path.Combine(Loaded, "b", "custom.cuix"), "B");
        var m = Analyze(Path.Combine(Loaded, "a", "custom.cuix"), Path.Combine(Loaded, "b", "custom.cuix"));
        Assert.Equal(new[] { "b/custom.cuix", "custom.cuix" }, m.Cuix.Select(c => c.File).OrderBy(f => f));   // the folder that differs, no full path
        // their tabs are filed apart too: different containers, so different tab ids and docs folders
        Assert.Equal(2, m.Cuix.SelectMany(c => c.Tabs).Select(t => t.Container).Distinct().Count());
    }

    [Fact]
    public void Partials_are_followed_only_when_asked()
    {
        Cuix(Path.Combine(Src, "main.cuix"), "Main", "../elsewhere/part.cuix");
        Cuix(Path.Combine(_root, "elsewhere", "part.cuix"), "Part");
        Assert.Single(Analyze(false).Cuix);
        Assert.Equal(2, Analyze(true).Cuix.Count);
    }

    [Fact]
    public void A_partial_that_was_found_elsewhere_is_not_reported_missing()
    {
        Cuix(Path.Combine(Loaded, "acad.cuix"), "Home", "acetmain.cuix");          // named relative, found by AutoCAD in another folder
        Cuix(Path.Combine(_root, "express", "acetmain.cuix"), "Express");
        Directory.CreateDirectory(Src);
        var m = Analyze(Path.Combine(Loaded, "acad.cuix"), Path.Combine(_root, "express", "acetmain.cuix"));
        Assert.Equal(2, m.Cuix.Count);
        Assert.DoesNotContain(m.Warnings, w => w.Contains("not found"));
    }

    [Fact]
    public void Loose_parts_of_an_unzipped_cuix_are_not_read_as_legacy_files()
    {
        Directory.CreateDirectory(Src);
        File.WriteAllText(Path.Combine(Src, "RibbonRoot.cui"), Ribbon("Loose", "LoosePanel"));
        File.WriteAllText(Path.Combine(Src, "MenuGroup.cui"), Menu);
        Assert.Empty(Analyze().Cuix);
    }

    [Fact]
    public void A_loaded_file_whose_name_looks_like_a_secret_is_skipped()
    {
        Directory.CreateDirectory(Src);
        Cuix(Path.Combine(Loaded, "secrets.cuix"), "Secret");
        var m = Analyze(Path.Combine(Loaded, "secrets.cuix"));
        Assert.Empty(m.Cuix);
        Assert.Contains(m.Warnings, w => w.Contains("secrets.cuix: skipped"));
    }

    [Theory]
    [InlineData(@"C:\Users\x\AppData\Roaming\Autodesk\AutoCAD Electrical 2026\R25.1\enu\support\acad.cuix",
                @"C:\Users\x\AppData\Roaming\Autodesk\AutoCAD 2026\R25.1\enu\Support\acad.cuix", "AutoCAD Electrical 2026/acad.cuix")]
    [InlineData("/a/b/c.cuix", "/a/x/c.cuix", "b/c.cuix")]
    public void Same_named_files_are_told_apart_by_the_first_folder_that_differs(string path, string other, string label) =>
        Assert.Equal(label, Pipeline.Distinct(path, other));
}
