// The checkpoints of one recording: their automatic names, reading them back for the review window, and renaming them in recording.jsonl.
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Retrace.Engine.Emit;

namespace Retrace.Engine.Recordings;

/// <summary>One checkpoint as the review window shows it.</summary>
/// <param name="N">the `mark` event's `n`: what a rename refers to</param>
/// <param name="Index">1-based among the checkpoints (Check 1, Check 2 ... in steps.md and in replay results)</param>
/// <param name="Step">its step number in steps.md</param>
/// <param name="Name">the current name (the `note`)</param>
/// <param name="AutoName">the name Retrace gave it; an empty name in the window means this one</param>
/// <param name="After">what was done just before, in words ("OK in Draw grid", "Draw › Grid on the ribbon"), or null at the start</param>
/// <param name="State">what the drawing held ("12 POINT", "the drawing is empty"), or null when the recorder could not read it</param>
/// <param name="Picture">full path of its screenshot, or null</param>
/// <param name="Near">what the checkpoint shortcut found at the cursor (mark `near`), or null</param>
public sealed record CheckpointInfo(long N, int Index, int Step, string Name, string AutoName, string? After, string? State, string? Picture, string? Near = null)
{
    public bool Renamed => !string.Equals(Name, AutoName, StringComparison.Ordinal);
}

public static class Checkpoints
{
    /// <summary>Longest name kept; longer ones are cut.</summary>
    public const int MaxName = 120;

    /// <summary>The automatic name: `checkpoint&lt;index&gt;`, then where the last thing was done and with which control, as lowercase words
    /// joined by `_`: after a dialog `checkpoint1_draw_grid_button_ok` (dialog title, the button that closed it), after a ribbon click
    /// `checkpoint2_draw_button_grid` (panel, button), after a palette `checkpoint3_box_button_draw_box` (palette, button), after a typed command `checkpoint4_cmd_drawgrid`,
    /// after opening a drawing `checkpoint5_open_demo01_dwg`; `checkpoint1` before anything was done.</summary>
    public static string AutoName(int index, CheckpointContext? last)
    {
        var parts = new List<string> { "checkpoint" + index };
        if (last != null)
            switch (last.Kind)
            {
                case "dialog":
                    parts.Add(Token(last.Container));
                    if (!string.IsNullOrEmpty(last.Control)) parts.Add("button_" + Token(last.Control));
                    break;
                case "ribbon":
                    parts.Add(Token(last.Container));
                    parts.Add("button_" + Token(last.Control));
                    break;
                case "palette":
                    parts.Add(Token(last.Container));
                    parts.Add(string.IsNullOrEmpty(last.Control) ? "palette" : "button_" + Token(last.Control));
                    break;
                case "control":   // the checkpoint shortcut, with the cursor on a control: where, and the control
                    parts.Add(Token(last.Container));
                    if (!string.IsNullOrEmpty(last.Control)) parts.Add(Token(last.Control));
                    break;
                case "command":
                    parts.Add("cmd_" + Token(last.Control));
                    break;
                case "open":
                    parts.Add("open_" + Token(last.Control));
                    break;
            }
        var name = string.Join("_", parts.Where(p => p.Length > 0 && !p.EndsWith('_')));
        return name.Length > MaxName ? name[..MaxName].TrimEnd('_') : name;
    }

    /// <summary>An automatic name without its `checkpoint&lt;n&gt;` part ("checkpoint2_cmd_line" -> "cmd_line", "checkpoint1" -> ""), for keys that
    /// must not depend on a checkpoint's position. Any other name is returned as it is.</summary>
    public static string WithoutNumber(string? name)
    {
        var s = name ?? "";
        const string P = "checkpoint";
        if (!s.StartsWith(P, StringComparison.Ordinal)) return s;
        int i = P.Length;
        while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
        if (i == P.Length) return s;
        if (i == s.Length) return "";
        return s[i] == '_' ? s[(i + 1)..] : s;
    }

    /// <summary>"Draw grid" -> "draw_grid", "Layer Properties..." -> "layer_properties": lowercase letters and digits, other runs become one `_`,
    /// at most 30 characters.</summary>
    public static string Token(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sb = new StringBuilder();
        foreach (var c in text.Replace("&", "").ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
        }
        var t = sb.ToString().Trim('_');
        return t.Length > 30 ? t[..30].TrimEnd('_') : t;
    }

    /// <summary>Where the last action before a checkpoint happened, for its name and for the review window.</summary>
    /// <param name="Kind">dialog | ribbon | palette | command | open (a drawing opened; Control = its file name) | control (the control at the cursor when the shortcut was pressed)</param>
    /// <param name="Container">dialog title, ribbon panel (else tab), palette name</param>
    /// <param name="Control">dialog: "ok" / "cancel" (how it was closed) or null; ribbon or palette: the button's text (palette: null when only values changed); command: its name</param>
    /// <param name="Description">"control" only: what was at the cursor, in a sentence (stored in the mark's `near`)</param>
    public sealed record CheckpointContext(string Kind, string? Container, string? Control, string? Description = null)
    {
        /// <summary>In words, for the review window: "OK in Draw grid", "Draw › Grid on the ribbon", "DRAWGRID typed".</summary>
        public string Describe() => Kind switch
        {
            "dialog" => (Control is { Length: > 0 } c ? (c == "ok" ? "OK" : c == "cancel" ? "Cancel" : c) + " in " : "the dialog ") + (Container ?? "(untitled dialog)"),
            "ribbon" => string.Join(" › ", new[] { Container, Control }.Where(x => !string.IsNullOrWhiteSpace(x))) + " on the ribbon",
            "palette" => (Control is { Length: > 0 } b ? b + " in the " : "the ") + (Container ?? "(untitled)") + " palette",
            "control" => Description ?? (Control is { Length: > 0 } k ? $"{k} in {Container}" : Container ?? "?"),
            "open" => $"{Control ?? "a drawing"} opened",
            _ => (Control ?? "?").ToUpperInvariant() + " typed",
        };
    }

    /// <summary>The context a step leaves behind: the last dialog it opened (closed with OK / Cancel), else the ribbon button or command; null for other steps.</summary>
    public static CheckpointContext? ContextOf(RecordingStep st)
    {
        var dlg = st.Kind == "dialog" ? st : st.InnerDialogs.LastOrDefault();
        if (dlg != null) return new CheckpointContext("dialog", dlg.DialogTitle, dlg.ClosedWith);
        return st.Kind switch
        {
            "ribbon" => new CheckpointContext("ribbon", FirstText(st.Panel, st.Tab), FirstText(st.Text, st.ItemId)),
            "command" or "lisp" => new CheckpointContext("command", null, st.Kind == "lisp" ? "lisp" : st.Command),
            // a native palette: where and what (checkpoint3_projects_demo01_dwg); after a double-click that opened a drawing, that drawing
            "palette" when st.OpensDrawing != null => new CheckpointContext("open", null, Path.GetFileName(st.OpensDrawing.Replace('\\', '/'))),
            "palette" when st.Native != null => new CheckpointContext("control", st.DialogTitle, st.Native.Target, st.Native.Describe() + $" in the {st.DialogTitle} palette"),
            "palette" => new CheckpointContext("palette", st.DialogTitle, st.Text),
            "doc" when st.Text != null => new CheckpointContext("open", null, st.Text),   // an "Open drawing" step; Text = the file name
            _ => null,
        };
    }

    /// <summary>A name as stored: one line, single spaces, trimmed, at most <see cref="MaxName"/> characters.</summary>
    public static string Clean(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var sb = new StringBuilder(name.Length);
        bool space = false;
        foreach (var c in name)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(c);
        }
        var s = sb.ToString();
        return s.Length > MaxName ? s[..MaxName].TrimEnd() : s;
    }

    static string? FirstText(params string?[] xs) => xs.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();

    /// <summary>The checkpoints of a recording, in order. Recordings made before checkpoints had automatic names get one here
    /// (only for the window: nothing is written).</summary>
    public static List<CheckpointInfo> Read(string recording) => Read(recording, out _);

    /// <inheritdoc cref="Read(string)"/><param name="testName">the test's name as the steps show it (the header `name`)</param>
    public static List<CheckpointInfo> Read(string recording, out string testName)
    {
        var s = RecordingReader.Load(Path.GetFullPath(recording));
        testName = StepWording.SessionName(s);
        RecordingAnalysis.Run([s], null);
        var autoByN = s.Events.Where(e => e.Type == "mark" && e.N != null).ToDictionary(e => e.N!.Value, e => e.Str("autoName"));
        var list = new List<CheckpointInfo>();
        CheckpointContext? last = null;
        int step = 0;
        foreach (var st in s.Steps)
        {
            if (!ScriptWriter.IsNumbered(st)) continue;
            step++;
            if (st.Kind != "mark") { last = ContextOf(st) ?? last; continue; }
            if (st.N is not { } n) continue;   // a recording without event numbers cannot be renamed
            var index = list.Count + 1;
            var auto = autoByN.TryGetValue(n, out var a) && !string.IsNullOrWhiteSpace(a) ? Clean(a) : AutoName(index, last);
            var name = string.IsNullOrWhiteSpace(st.Note) ? auto : Clean(st.Note);
            var pic = st.Shots.Select(x => x.Abs).FirstOrDefault(File.Exists);
            var state = st.State == null ? null : StepWording.StateText(st.State);
            list.Add(new CheckpointInfo(n, index, step, name, auto, last?.Describe(), state, pic, st.Description));
        }
        return list;
    }

    static readonly JsonSerializerOptions Relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Sets the `note` of the `mark` events named in <paramref name="names"/> (event n -> new name; empty = back to its automatic name)
    /// and writes recording.jsonl again through a temporary file. Every other line stays byte for byte. Returns how many changed.
    /// Throws <see cref="InvalidDataException"/> when an n is not a checkpoint of this recording; IO errors pass through.</summary>
    public static int Rename(string recording, IReadOnlyDictionary<long, string> names)
    {
        var bytes = File.ReadAllBytes(recording);
        var lines = new UTF8Encoding(false).GetString(bytes).Split('\n');
        var left = new HashSet<long>(names.Keys);
        int changed = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!line.Contains("\"mark\"", StringComparison.Ordinal)) continue;
            JsonObject? o;
            try { o = JsonNode.Parse(line.TrimEnd('\r')) as JsonObject; }
            catch (JsonException) { continue; }
            if (o == null || o["type"]?.GetValueKind() != JsonValueKind.String || (string?)o["type"] != "mark") continue;
            if (o["n"] is not JsonValue nv || !nv.TryGetValue<long>(out var n) || !names.TryGetValue(n, out var wanted)) continue;
            left.Remove(n);
            var auto = o["autoName"]?.GetValueKind() == JsonValueKind.String ? Clean((string?)o["autoName"]) : "";
            var name = Clean(wanted);
            if (name.Length == 0) name = auto;   // cleared in the window: back to the automatic name (older recordings: no note)
            var old = o["note"]?.GetValueKind() == JsonValueKind.String ? (string?)o["note"] : null;
            if (old == name) continue;
            o["note"] = name;
            lines[i] = o.ToJsonString(Relaxed) + (line.EndsWith('\r') ? "\r" : "");
            changed++;
        }
        if (left.Count > 0) throw new InvalidDataException($"not a checkpoint of this recording: event {string.Join(", ", left.Order())}");
        if (changed == 0) return 0;
        var tmp = recording + ".tmp";
        File.WriteAllBytes(tmp, new UTF8Encoding(false).GetBytes(string.Join('\n', lines)));
        File.Move(tmp, recording, true);
        return changed;
    }
}
