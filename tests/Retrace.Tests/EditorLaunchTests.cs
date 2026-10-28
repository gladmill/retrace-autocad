namespace Retrace.Tests;

/// <summary>The arguments that open a file at a line in the editor from the settings.</summary>
public class EditorLaunchTests
{
    [Theory]
    [InlineData("code", @"-g ""C:\src\a.cs:42""")]
    [InlineData("code.cmd", @"-g ""C:\src\a.cs:42""")]
    [InlineData(@"C:\Users\x\AppData\Local\Programs\Microsoft VS Code\bin\code.cmd", @"-g ""C:\src\a.cs:42""")]
    [InlineData(@"C:\Users\x\AppData\Local\Programs\Microsoft VS Code\Code.exe", @"-g ""C:\src\a.cs:42""")]
    [InlineData("code-insiders", @"-g ""C:\src\a.cs:42""")]
    [InlineData(@"C:\Program Files\Microsoft VS Code Insiders\Code - Insiders.exe", @"-g ""C:\src\a.cs:42""")]
    [InlineData("cursor", @"-g ""C:\src\a.cs:42""")]
    [InlineData(@"C:\Users\x\AppData\Local\Programs\cursor\Cursor.exe", @"-g ""C:\src\a.cs:42""")]
    [InlineData("codium", @"-g ""C:\src\a.cs:42""")]
    [InlineData("VSCodium.exe", @"-g ""C:\src\a.cs:42""")]
    [InlineData("/usr/local/bin/code", @"-g ""C:\src\a.cs:42""")]
    public void VS_Code_and_its_forks_take_g_with_file_and_line(string editor, string expected) =>
        Assert.Equal(expected, EditorLaunch.Arguments(editor, @"C:\src\a.cs", 42));

    [Theory]
    [InlineData("notepad++")]
    [InlineData("notepad++.exe")]
    [InlineData(@"C:\Program Files\Notepad++\notepad++.exe")]
    [InlineData(@"""C:\Program Files\Notepad++\Notepad++.exe""")]
    public void Notepad_plus_plus_takes_n_and_the_line(string editor) =>
        Assert.Equal(@"-n42 ""C:\src\a.cs""", EditorLaunch.Arguments(editor, @"C:\src\a.cs", 42));

    [Theory]
    [InlineData("subl")]
    [InlineData("subl.exe")]
    [InlineData("sublime_text")]
    [InlineData(@"C:\Program Files\Sublime Text\sublime_text.exe")]
    public void Sublime_Text_takes_the_file_and_the_line_joined_by_a_colon(string editor) =>
        Assert.Equal(@"""C:\src\a.cs:42""", EditorLaunch.Arguments(editor, @"C:\src\a.cs", 42));

    [Theory]
    [InlineData("notepad")]
    [InlineData(@"C:\Windows\notepad.exe")]
    [InlineData("devenv.exe")]
    [InlineData("")]
    [InlineData("codeblocks")]            // starts with "code" but is another editor
    [InlineData("mycode.exe")]
    [InlineData(@"C:\code\tools\other.exe")]   // "code" is a folder here, not the program
    public void Any_other_editor_gets_just_the_quoted_file(string editor) =>
        Assert.Equal(@"""C:\src\a.cs""", EditorLaunch.Arguments(editor, @"C:\src\a.cs", 42));

    [Theory]
    [InlineData("code")]
    [InlineData("notepad++")]
    [InlineData("subl")]
    [InlineData("notepad")]
    public void Without_a_line_every_editor_gets_just_the_quoted_file(string editor)
    {
        Assert.Equal(@"""C:\src\a.cs""", EditorLaunch.Arguments(editor, @"C:\src\a.cs", 0));
        Assert.Equal(@"""C:\src\a.cs""", EditorLaunch.Arguments(editor, @"C:\src\a.cs", -3));
    }

    [Fact]
    public void A_path_with_spaces_stays_in_one_quoted_argument()
    {
        Assert.Equal(@"-g ""C:\my code\a b.cs:7""", EditorLaunch.Arguments("code", @"C:\my code\a b.cs", 7));
        Assert.Equal(@"-n7 ""C:\my code\a b.cs""", EditorLaunch.Arguments("notepad++", @"C:\my code\a b.cs", 7));
    }
}
