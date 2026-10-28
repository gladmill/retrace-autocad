using System.Text;

namespace Retrace.Core;

/// <summary>Exports the plain-command parts of a session as an AutoCAD .scr script (one line per Enter).</summary>
public static class ScriptExport
{
    public static string Build(List<Step> steps, string sourceName)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"; Retrace export of {sourceName}");
        sb.AppendLine("; Plain commands and typed values only. Lines starting with ; are comments (use SCRIPT to run).");
        sb.AppendLine("; Handles in (handent \"..\") belong to the drawing the session was recorded in.");
        for (int i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            switch (s.Kind)
            {
                case "tab":
                    sb.AppendLine($"; tab: {s.Tab}");
                    break;
                case "mark":
                    sb.AppendLine($"; CHECK: {s.Note}");
                    break;
                case "dialog":
                    sb.AppendLine($"; MANUAL: dialog \"{s.DialogTitle}\" - not scriptable, fill in by hand and continue");
                    break;
                case "palette":
                    sb.AppendLine($"; MANUAL: {s.Title} - not scriptable, do it in the palette by hand and continue");
                    // it opened a drawing: a script cannot go on there (see "open")
                    if (s.Drawing != null) { sb.AppendLine("; The rest of the recording happened in the drawing it opened and is not exported."); return sb.ToString(); }
                    break;
                case "open":
                    // a script keeps running in the drawing it started in, so it cannot go on in the one it opens: the export ends here
                    sb.AppendLine($"; MANUAL: open the drawing {s.Drawing}. The rest of the recording happened in that drawing and is not exported.");
                    return sb.ToString();
                case "ribbon" or "command" or "lisp":
                {
                    sb.AppendLine($"; {s.Title}");
                    if (s.HadDialog) { sb.AppendLine($"; MANUAL: {s.Command} opened a dialog; run it by hand (use -{s.Command} for the command-line form if one exists)"); break; }
                    var text = Plan.InputText(s);
                    if (string.IsNullOrEmpty(text)) { sb.AppendLine("; (no replayable input recorded)"); break; }
                    // .scr: each line is Enter-terminated, so the "\n"-joined text maps one to one. Scripts cannot send Esc: drop it.
                    foreach (var line in text[..^1].Split('\n'))
                    {
                        var clean = line.Replace("\u0003", "");
                        if (clean.Length == 0 && line.Length > 0) continue; // was only an Esc
                        sb.AppendLine(clean);
                    }
                    break;
                }
            }
        }
        return sb.ToString();
    }
}
