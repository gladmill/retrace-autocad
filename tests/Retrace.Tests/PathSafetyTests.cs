using Retrace.Engine.Scan.Forms;

namespace Retrace.Tests;

/// <summary>
/// Names parsed out of files the user owns (.rc, .cuix, recordings) reach System.IO.Path. .NET 8 never throws for " &lt; &gt; | or control
/// characters or a ':' in the middle; .NET Framework (AutoCAD 2024) does, which would lose a whole scan for one bad name. These run on both
/// targets (the net48 project compiles them with Compat/NetPath.cs: the name Path is that class there).
/// </summary>
public class PathSafetyTests
{
    public static IEnumerable<object[]> BadNames() => new[]
    {
        "a\"b.ico", "a<b.ico", "a>b.ico", "a|b.ico", "a\u0001b.ico", "a\tb.ico", "a\nb.ico",
        "sub\\a:b.ico", "C:\\x\\a:b.ico", "res/a<b|c.ico", "..\\res\\\"q\".bmp", "a\"<>|b",
    }.Select(n => new object[] { n });

    [Theory, MemberData(nameof(BadNames))]
    public void Path_apis_do_not_throw_on_invalid_characters(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "retrace-bad");
        Path.Combine(root, name);
        Path.Combine(root, "res", name);
        Path.Combine(name, "x");
        Path.GetFullPath(Path.Combine(root, name));
        Path.GetFullPath(name);
        Path.GetFileName(name);
        Path.GetFileNameWithoutExtension(name);
        Path.GetExtension(name);
        Path.GetDirectoryName(Path.Combine(root, name));
        Path.ChangeExtension(name, ".x");
        Path.IsPathRooted(name);
        Path.GetPathRoot(Path.Combine(root, name));
        Path.GetRelativePath(root, Path.Combine(root, name));
    }

    [Fact]
    public void Results_are_the_dotnet8_ones()
    {
        var root = OperatingSystem.IsWindows() ? "C:\\proj" : "/proj";
        var s = Path.DirectorySeparatorChar;
        Assert.Equal(root + s + "a<b.ico", Path.Combine(root, "a<b.ico"));
        Assert.Equal("a<b.ico", Path.GetFileName(Path.Combine(root, "a<b.ico")));
        Assert.Equal(".ico", Path.GetExtension("res" + s + "a|b.ico"));
        Assert.Equal("a|b", Path.GetFileNameWithoutExtension("res" + s + "a|b.ico"));
        Assert.Equal(root, Path.GetDirectoryName(Path.Combine(root, "a\"b.ico")));
        Assert.Equal("a\"b.png", Path.GetFileName(Path.ChangeExtension(Path.Combine(root, "a\"b.ico"), ".png")));
        Assert.Equal("res" + s + "a<b.ico", Path.GetRelativePath(root, Path.Combine(root, "res", "a<b.ico")));
        Assert.Equal("", Path.GetExtension("noext"));
        Assert.Equal("", Path.GetExtension("trailing."));
        Assert.Null(Path.GetDirectoryName(""));
        Assert.Equal("", Path.GetFileName(root + s));
    }

    [Fact]
    public void Invalid_names_do_not_exist()
    {
        var root = Directory.CreateTempSubdirectory("retrace-test-badnames").FullName;
        try
        {
            foreach (var n in BadNames().Select(x => (string)x[0]))
            {
                Assert.False(File.Exists(Path.Combine(root, n)));
                Assert.False(Directory.Exists(Path.Combine(root, n)));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void One_bad_image_name_in_a_rc_file_does_not_lose_the_forms()
    {
        const string rc = @"
IDI_BAD ICON ""res\\a<b|c"":d.ico""
IDI_Q ICON ""..\\res\\""""q"""".ico""
IDD_ONE DIALOGEX 0, 0, 200, 100
CAPTION ""One""
BEGIN
    DEFPUSHBUTTON   ""&Close"",IDCANCEL,140,80,50,14
    ICON            IDI_BAD,IDC_STATIC,6,6,20,20
    ICON            IDI_Q,IDC_STATIC,30,6,20,20
END
IDD_TWO DIALOG 0, 0, 100, 50
CAPTION ""Two""
{
    PUSHBUTTON ""Run"",IDOK,10,10,40,14
}
";
        var forms = FormScanner.Scan(new[] { FormScannerTests.Src("res/demo.rc", Lang.Rc, rc, "Demo") }).ToList();
        Assert.Equal(2, forms.Count);
        Assert.All(forms.SelectMany(f => f.Controls).Where(c => c.Type == "ICON"), c => Assert.False(c.Extra.ContainsKey("image")));
    }
}
