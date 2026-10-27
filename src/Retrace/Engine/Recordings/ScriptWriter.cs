// One recording (in its test folder) -> a self-contained test script steps.md next to it.
using System.Text;
using Retrace.Engine.Emit;
using Retrace.Engine.Util;

namespace Retrace.Engine.Recordings;

/// <summary>A manual test script a person can follow to rerun a recorded session. Same step wording as the guide's How-to pages
/// (<see cref="StepWording"/>); screenshots are linked where the recorder left them (relative to the session file). The script is
/// written from scratch on every run: it is not an owner-edited page, so there are no merge markers.</summary>
public static class ScriptWriter
{
    /// <summary>The script's path: `steps.md` next to the recording.jsonl.</summary>
    public static string PathFor(string sessionFile) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sessionFile)) ?? "", TestFolders.StepsFile);

    /// <summary>Writes the script and returns its path. <paramref name="warnings"/> gets missing screenshots.
    /// It also names the pictures after the steps and rewrites recording.jsonl (<see cref="ShotNamer"/>).</summary>
    public static string Write(RecordingSession s, SourceMapIndex? map, List<string> warnings)
    {
        var path = PathFor(s.File);
        var dir = Path.GetDirectoryName(path)!;
        var namer = new ShotNamer(s, CountSteps(s));
        var text = Build(s, map, dir, warnings, namer);
        if (!namer.Apply(warnings)) text = Build(s, map, dir, new List<string>(), null, filesTable: true);   // pictures stay where they are
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    /// <summary>Reads the recording and writes its script (see <see cref="Write"/>). Returns the script's path.</summary>
    public static string WriteFor(string recordingFile, SourceMapIndex? map, List<string> warnings, out int badLines)
    {
        var one = RecordingReader.Load(Path.GetFullPath(recordingFile));
        RecordingAnalysis.Run([one], map);
        badLines = one.BadLines;
        return Write(one, map, warnings);
    }

    /// <summary>How many numbered steps the script will have.</summary>
    private static int CountSteps(RecordingSession s) => s.Steps.Count(IsNumbered);

    /// <summary>True for the steps the script numbers (the kinds <see cref="StepWording.Render"/> writes; a dialog a command opened is part of that command's step).</summary>
    public static bool IsNumbered(RecordingStep st) => st.Kind is "ribbon" or "command" or "lisp" or "mark" or "doc" || (st.Kind == "dialog" && st.OpenedIn == null);

    public static string Build(RecordingSession s, SourceMapIndex? map, string scriptDir, List<string> warnings, ShotNamer? namer = null, bool filesTable = false)
    {
        var sb = new StringBuilder($"# {Md.Cell(StepWording.SessionName(s))}\n\n");
        var meta = StepWording.MetaLine(s);
        if (meta.Length > 0) sb.Append(meta).Append(".\n\n");

        var start = s.InitialState is { Count: > 0 } st
            ? $"a drawing like the one recorded at the start ({Md.Cell(st.Summary())}). Open or build it before the first step"
            : "a new empty drawing";
        sb.Append($"**Start with:** {start}.\n\n");
        sb.Append("Do the steps in order. A picture under a step shows what the screen looked like when the test was recorded.\n\n");
        if (filesTable || namer != null) sb.Append(FilesSection);
        sb.Append("## Steps\n\n");

        WordingContext? ctx = null;
        ctx = new WordingContext
        {
            Script = true,
            RibbonHref = step => map != null && step.Join?.Entry.Doc is { } doc ? MapLink(map, scriptDir, doc) : null,
            ShotHref = shot =>
            {
                if (!File.Exists(shot.Abs)) { warnings.Add($"screenshot not found: {shot.Abs}"); return null; }
                return (namer?.Link(shot, ctx!.ShotRole) ?? shot.File).Replace(" ", "%20");
            },
        };
        int n = 0;
        var checks = new List<(int Number, string Note)>();
        foreach (var step in s.Steps)
        {
            namer?.BeginStep(n + 1);
            var text = StepWording.Render(step, n + 1, ctx);
            if (text == null) continue;
            n++;
            sb.Append(text).Append('\n');
            if (step.Kind == "mark") checks.Add((n, string.IsNullOrWhiteSpace(step.Note) ? "the drawing looks as expected" : Md.Cell(step.Note)));
        }
        if (n == 0) sb.Append("_No steps were recorded._\n\n");

        sb.Append("## Result\n\n");
        if (checks.Count == 0)
            sb.Append("This recording has no checkpoints. Write down what you saw: **pass** if the steps ran as described, **fail** otherwise.\n");
        else
        {
            sb.Append("Record the outcome of each check (step number in brackets) by ticking pass or fail. The test passes when every check passes.\n\n");
            for (int i = 0; i < checks.Count; i++)
                sb.Append($"- Check {i + 1} (step {checks[i].Number}): {checks[i].Note}: [ ] pass  [ ] fail\n");
            sb.Append("\nOverall: [ ] pass  [ ] fail\n");
        }
        return sb.ToString();
    }

    private const string FilesSection = """
        ## Files in this folder

        | File | What it is for |
        |---|---|
        | `steps.md` | This page: the steps to follow by hand. It is written again each time the recording is processed, so do not edit it. |
        | `recording.jsonl` | The recording itself, one event per line. This is what **Run test** replays. |
        | `screenshots` | Pictures of the screen, named after the step that shows them. `unused` holds the ones this page does not show. |
        | `results` | One file per automatic run, named by date and verdict (`2026-10-10_03-15-02_PASS.json`). `latest.json` is a copy of the newest. |

        To run this test automatically: in AutoCAD open the **Retrace** tab, click **Run test** and pick `recording.jsonl` in this folder.


        """;

    /// <summary>Link from the script's folder to a page of the engine output the sourcemap points at (doc may carry an #anchor).</summary>
    private static string? MapLink(SourceMapIndex map, string scriptDir, string doc)
    {
        var hash = doc.IndexOf('#');
        var file = hash >= 0 ? doc[..hash] : doc;
        var anchor = hash >= 0 ? doc[hash..] : "";
        var rel = Path.GetRelativePath(scriptDir, Path.Combine(map.MapDir, file.Replace('/', Path.DirectorySeparatorChar)));
        if (Path.IsPathRooted(rel)) return null; // another drive: no relative link exists
        return rel.Replace('\\', '/').Replace(" ", "%20") + anchor;
    }
}
