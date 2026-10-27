// steps.md for a recording in a test folder: name the pictures the script shows after the step that shows them.
using System.Text;
using System.Text.Json;

namespace Retrace.Engine.Recordings;

/// <summary>Decides the new name of every picture the script shows (`step-02-click.png`), then applies it: renames the files, moves the pictures the
/// script does not show to `screenshots\unused\`, and rewrites the `file` of the matching `shot` events in recording.jsonl.
/// The script is built first (with the new names in its links); <see cref="Apply"/> then makes the files match. Rerunning changes nothing.</summary>
public sealed class ShotNamer
{
    private readonly string _dir;        // the test folder
    private readonly string _recording;
    private readonly RecordingSession _session;
    private readonly int _digits;
    private readonly Dictionary<string, string> _byAbs = new(StringComparer.Ordinal);   // picture (full path) -> new path relative to the folder, '/'
    private readonly HashSet<string> _taken = new(StringComparer.OrdinalIgnoreCase);
    private int _step, _answers;

    public ShotNamer(RecordingSession session, int stepCount)
    {
        _session = session;
        _recording = Path.GetFullPath(session.File);
        _dir = Path.GetDirectoryName(_recording)!;
        _digits = stepCount >= 100 ? 3 : 2;
    }

    static readonly System.Text.RegularExpressions.Regex StepName = Rx.Create(@"^step-\d+-", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    static readonly System.Text.RegularExpressions.Regex RawName = Rx.Create(@"^\d+-(.+)\.png$", System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    static readonly Dictionary<string, string> RawKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ribbon"] = "click", ["cmd-start"] = "command", ["cmd-end"] = "end", ["cmd-cancel"] = "cancelled", ["mark"] = "check", ["palette"] = "click",
    };

    /// <summary>The script is about to write step <paramref name="n"/>.</summary>
    public void BeginStep(int n) { _step = n; _answers = 0; }

    private static bool Same(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>Path (relative to the folder, '/') the script links for <paramref name="shot"/>, assigned on first use.</summary>
    public string Link(RecordingShot shot, string role)
    {
        var abs = Path.GetFullPath(shot.Abs);
        if (_byAbs.TryGetValue(abs, out var known)) return known;
        // a picture outside this folder (a hand-edited path) keeps its link
        if (!abs.StartsWith(_dir + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return _byAbs[abs] = shot.File;
        var file = Path.GetFileName(shot.File);
        // already named after its step (a second run): keep it, so links and names never move again
        if (StepName.IsMatch(file)) { _taken.Add(file); return _byAbs[abs] = shot.File; }
        // the recorder's name says what the picture shows ("012-cmd-start.png"); prompt pictures take the answer's wording (pick-1, enter-2)
        var raw = RawName.Match(file);
        var kind = shot.What switch
        {
            "dialog-after" => "dialog-after",
            "dialog" => "dialog-before",
            "palette" => "palette",
            _ => raw.Success && RawKinds.TryGetValue(raw.Groups[1].Value, out var k) ? k : role,
        };
        if (kind.StartsWith("answer:", StringComparison.Ordinal)) kind = kind[7..] + "-" + ++_answers;
        var stem = $"step-{_step.ToString("D" + _digits)}-{kind}";
        var ext = Path.GetExtension(shot.File);
        var name = stem + ext;
        for (int i = 2; !_taken.Add(name); i++) name = $"{stem}-{i}{ext}";
        return _byAbs[abs] = $"{TestFolders.ScreenshotsDir}/{name}";
    }

    /// <summary>Renames, moves unused pictures aside and rewrites recording.jsonl. False (nothing changed, reason in <paramref name="warnings"/>) when it cannot.</summary>
    public bool Apply(List<string> warnings)
    {
        try { return ApplyCore(warnings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"pictures were not renamed: {ex.Message}");
            return false;
        }
    }

    private bool ApplyCore(List<string> warnings)
    {
        var shotsDir = Path.Combine(_dir, TestFolders.ScreenshotsDir);
        var unused = Path.Combine(shotsDir, TestFolders.UnusedDir);
        var shots = AllShots().ToList();

        // 1. what moves where (full paths) and which `file` values change
        var moves = new List<(string From, string To)>();
        var rel = new Dictionary<string, string>(StringComparer.Ordinal);   // old `file` as recorded -> new `file`
        foreach (var shot in shots)
        {
            var abs = Path.GetFullPath(shot.Abs);
            if (!_byAbs.TryGetValue(abs, out var newRel)) continue;
            if (newRel != shot.File) rel[shot.File] = newRel;
            var to = Path.GetFullPath(Path.Combine(_dir, newRel.Replace('/', Path.DirectorySeparatorChar)));
            if (!Same(abs, to) && File.Exists(abs) && moves.All(m => m.From != abs)) moves.Add((abs, to));
        }
        // pictures directly in screenshots\ that the script does not show
        var aside = new List<(string From, string To)>();
        if (Directory.Exists(shotsDir))
        {
            var takenAside = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f0 in Directory.EnumerateFiles(shotsDir).OrderBy(f => f, StringComparer.Ordinal))
            {
                var f = Path.GetFullPath(f0);
                if (_byAbs.ContainsKey(f)) continue;
                var name = Path.GetFileName(f);
                var stem = Path.GetFileNameWithoutExtension(name); var ext = Path.GetExtension(name);
                var to = Path.Combine(unused, name);
                for (int i = 2; File.Exists(to) || !takenAside.Add(to); i++) to = Path.Combine(unused, $"{stem}-{i}{ext}");
                aside.Add((f, to));
                foreach (var shot in shots.Where(x => Path.GetFullPath(x.Abs) == f))   // events that point at it follow
                    rel[shot.File] = $"{TestFolders.ScreenshotsDir}/{TestFolders.UnusedDir}/{Path.GetFileName(to)}";
            }
        }
        if (moves.Count == 0 && aside.Count == 0 && rel.Count == 0) return true;

        // 2. the new recording.jsonl text, before anything is touched
        var oldText = new UTF8Encoding(false).GetString(File.ReadAllBytes(_recording));
        var newText = Rewrite(oldText, rel, warnings);
        if (newText == null) return false;

        // 3. move: used pictures to a temporary name first (a new name may be another picture's old name), the unused ones aside, then to the final names
        var temps = new List<(string Temp, string To, string From)>();
        string? tmpJson = null;
        try
        {
            Directory.CreateDirectory(shotsDir);
            foreach (var (from, to) in moves)
            {
                var tmp = Path.Combine(Path.GetDirectoryName(from)!, $".rt-{Guid.NewGuid():N}.tmp");
                File.Move(from, tmp);
                temps.Add((tmp, to, from));
            }
            if (aside.Count > 0) Directory.CreateDirectory(unused);
            foreach (var (from, to) in aside) File.Move(from, to);
            foreach (var (tmp, to, _) in temps)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Move(tmp, to);
            }
            temps.Clear();
            if (newText != oldText)
            {
                tmpJson = _recording + ".tmp";
                File.WriteAllBytes(tmpJson, new UTF8Encoding(false).GetBytes(newText));
                File.Move(tmpJson, _recording, true);
                tmpJson = null;
            }
        }
        catch
        {
            // put every picture back where it was
            foreach (var (tmp, _, from) in temps) try { if (File.Exists(tmp)) File.Move(tmp, from); } catch (Exception) { }
            foreach (var (from, to) in aside) try { if (File.Exists(to) && !File.Exists(from)) File.Move(to, from); } catch (Exception) { }
            foreach (var (from, to) in moves) try { if (File.Exists(to) && !File.Exists(from)) File.Move(to, from); } catch (Exception) { }
            if (tmpJson != null) try { File.Delete(tmpJson); } catch (Exception) { }
            throw;
        }
        return true;
    }

    /// <summary>Every `shot` event of the recording with its picture's path on this machine.</summary>
    private IEnumerable<RecordingShot> AllShots()
    {
        foreach (var e in _session.Events.Where(x => x.Type == "shot"))
        {
            var file = e.Str("file");
            if (string.IsNullOrWhiteSpace(file)) continue;
            var norm = file.Replace('\\', '/');
            yield return new RecordingShot(norm, e.Str("what") ?? "window", Path.Combine(_dir, norm.Replace('/', Path.DirectorySeparatorChar)));
        }
    }

    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Replaces the `file` value of the `shot` lines named in <paramref name="rel"/>; every other line, and every other byte of those lines,
    /// stays. Null (with a warning) when a line does not contain the value the way the recorder writes it.</summary>
    internal static string? Rewrite(string text, Dictionary<string, string> rel, List<string> warnings)
    {
        if (rel.Count == 0) return text;
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!line.Contains("\"shot\"", StringComparison.Ordinal)) continue;
            string? file;
            try
            {
                using var d = JsonDocument.Parse(line);
                if (d.RootElement.ValueKind != JsonValueKind.Object || !d.RootElement.TryGetProperty("type", out var t) || t.GetString() != "shot") continue;
                file = d.RootElement.TryGetProperty("file", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
            }
            catch (JsonException) { continue; }
            if (file == null || !rel.TryGetValue(file.Replace('\\', '/'), out var to)) continue;
            var oldLit = "\"file\":" + JsonSerializer.Serialize(file, Relaxed);
            var at = line.IndexOf(oldLit, StringComparison.Ordinal);
            if (at < 0) { warnings.Add($"recording.jsonl line {i + 1}: cannot rewrite the picture path, pictures were not renamed"); return null; }
            lines[i] = line[..at] + "\"file\":" + JsonSerializer.Serialize(to, Relaxed) + line[(at + oldLit.Length)..];
        }
        return string.Join('\n', lines);
    }
}
