namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project). It only builds the arguments; the plugin starts the process.

/// <summary>The command-line arguments that open a file at a line in the editor from the settings (RetraceSettings.Editor), for the editors
/// that can: VS Code and its forks (code, code-insiders, cursor, codium: -g "file:line"), Notepad++ (-n42 "file"), Sublime Text (subl,
/// sublime_text: "file:42"). Any other editor, or no line, gets just the quoted file.</summary>
public static class EditorLaunch
{
    /// <param name="editor">the editor's path or name, as in the settings ("code.cmd", "C:\...\notepad++.exe")</param>
    /// <param name="line">1-based; 0 or less = no line</param>
    public static string Arguments(string editor, string path, int line)
    {
        if (line <= 0) return $"\"{path}\"";
        return Name(editor) switch
        {
            "code" or "code-insiders" or "code - insiders" or "cursor" or "codium" or "vscodium" => $"-g \"{path}:{line}\"",
            "notepad++" => $"-n{line} \"{path}\"",
            "subl" or "sublime_text" => $"\"{path}:{line}\"",
            _ => $"\"{path}\"",
        };
    }

    /// <summary>Whether <see cref="Arguments"/> opens a file at its line in <paramref name="editor"/> (else the file opens at the top).</summary>
    public static bool JumpsToLine(string editor) => Arguments(editor, "x", 1) != "\"x\"";

    /// <summary>The editor's file name without folder and extension, lower case. Both separators count, and surrounding quotes are ignored:
    /// the tests run where Path knows only one of them.</summary>
    static string Name(string editor)
    {
        var e = (editor ?? "").Trim().Trim('"').Trim();
        e = e[(e.LastIndexOfAny(new[] { '\\', '/' }) + 1)..];
        return Path.GetFileNameWithoutExtension(e).ToLowerInvariant();
    }
}
