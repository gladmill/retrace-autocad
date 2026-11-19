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
        if (!namer.Apply(warnings)) text = Build(s, map, dir, new List<string>(), null);   // pictures stay where they are
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
    public static bool IsNumbered(RecordingStep st) => st.Kind is "ribbon" or "command" or "lisp" or "palette" or "statusbar" or "mark" or "doc" || (st.Kind == "dialog" && st.OpenedIn == null);

    public static string Build(RecordingSession s, SourceMapIndex? map, string scriptDir, List<string> warnings, ShotNamer? namer = null)
    {
        var sb = new StringBuilder($"# {Md.Cell(StepWording.SessionName(s))}\n\n");
        var meta = StepWording.MetaLine(s);
        if (meta.Length > 0) sb.Append(meta).Append(".\n\n");
        // the Retrace settings the test was recorded with (those not at their default): only steps.md says so; the Word and Excel exports leave the line out (StepsDoc.IsSettingsLine)
        if (SettingsHeader.Line(SettingsHeader.FromHeader(s.Header)) is { } settingsLine) sb.Append('*').Append(Md.Inline(settingsLine)).Append("*\n\n");

        sb.Append($"**Start with:** {StartWording.Describe(s)}.\n\n");
        sb.Append("Do the steps in order. A picture under a step shows what the screen looked like at that step.\n\n");
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
        if (n == 0) sb.Append("_This test has no steps._\n\n");

        sb.Append("## Result\n\n");
        if (EndCheck(s) is { } endCheck) sb.Append("**End check:** ").Append(endCheck).Append(".\n\n");
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

    /// <summary>The drawing at the end in plain words, from the recording's last `end_counts` event: "Model space has 2 objects, 1 layer, 1 layout". Null when the recording has none.</summary>
    public static string? EndCheck(RecordingSession s)
    {
        var e = s.Events.LastOrDefault(x => x.Type == "end_counts");
        if (e == null) return null;
        string? N(string key, string one, string many) =>
            e.Raw.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number && v.TryGetInt32(out var n) ? $"{n} {(n == 1 ? one : many)}" : null;
        var parts = new[] { N("modelspace", "object", "objects"), N("layers", "layer", "layers"), N("layouts", "layout", "layouts") }.Where(x => x != null).ToList();
        return parts.Count == 0 ? null : "Model space has " + string.Join(", ", parts);
    }

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
