using System.Globalization;
using Retrace.Engine.Recordings;
using System.Text;
using System.Text.Json;

namespace Retrace.Core;

/// <param name="Input">recorder's flag: true = a click/key press led to it, false = mouse-move noise, null = not recorded</param>
/// <param name="T">time of the result (ms since the session started)</param>
public sealed record Answer(string Kind, string Status, JsonElement? Value, string? Keyword, string? Message, bool? Input = null, long? Seq = null, long T = 0);

public sealed class Step
{
    public string Kind = "";            // ribbon | command | lisp | dialog | palette | mark | tab
    public string Title = "";           // human readable one-liner
    public string? ItemId, Macro, Tab, Panel, Path;
    public string? Command;             // command started by this step (global name)
    public string? Lisp;
    public string? DialogTitle;         // dialog: its title; palette: the palette (its tab in the palette set; Panel = the set)
    public string? Button;              // palette: the button clicked, null when only values changed
    public int ButtonIndex = 1;         // palette: which of the palette's buttons with that text (1 = the first)
    public string? Note;
    public DrawingDigest? State;        // mark: the drawing state recorded at the checkpoint (null in older sessions)
    public long T;
    public bool HadDialog;
    /// <summary>dialog / palette: the fields the person changed while it was open; null = the session has no `dialog_values` event for it.</summary>
    public List<FieldChange>? Values;
    /// <summary>dialog: how many fields the recorder could read.</summary>
    public int ValuesRead;
    /// <summary>dialog: "ok" / "cancel" as the recording closed it; null = not recorded.</summary>
    public string? ClosedWith;
    /// <summary>dialog: "win32" or "uia" as the recorder read it; null = not recorded.</summary>
    public string? ValuesSource;
    public List<Answer> Answers = new();
}

/// <summary>Reads a session file and groups its events into replayable steps. No AutoCAD dependency, so it is easy to test.</summary>
public static class Plan
{
    /// <summary>The Retrace ribbon tab: switching to it (to stop or mark) is not part of a test.</summary>
    public static bool IsOwnTab(string? tabId) => OwnCommands.IsOwnTab(tabId);

    /// <summary>A button of the Retrace tab (its ids are the plugin command names).</summary>
    public static bool IsOwnRibbon(string? id, string? tabId) => IsOwnTab(tabId) || OwnCommands.IsOwnCommand(id);

    static readonly System.Text.RegularExpressions.Regex TypeName = Rx.Create(@"^[A-Za-z_]\w*(\.[A-Za-z_]\w*)+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>A recorded macro, or null when there is none. Older recorders wrote the CommandParameter's ToString for buttons that run
    /// code (the Samples tab), i.e. a .NET type name like "Autodesk.Windows.RibbonButton": that is not a macro.</summary>
    public static string? CleanMacro(string? macro) => string.IsNullOrWhiteSpace(macro) || TypeName.IsMatch(macro.Trim()) ? null : macro;

    public static List<JsonElement> ReadEvents(string path)
    {
        var list = new List<JsonElement>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { list.Add(JsonDocument.Parse(line).RootElement.Clone()); } catch (JsonException) { /* torn last line after a crash */ }
        }
        return list;
    }

    public static string? Str(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    public static bool? Bool(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    /// <summary>Where a step has results flagged as real input, the unflagged ones are mouse-move noise (see Recorder).
    /// Steps without flags (scripted input, older recordings) are left alone.</summary>
    /// <summary>Older recordings also logged prompts nested inside another one (GetCorner runs a point prompt inside the corner
    /// prompt). A nested prompt started later than its outer one but ended first, so its seq is higher than the next answer's.</summary>
    public static void DropNested(IEnumerable<List<Answer>> answerLists)
    {
        foreach (var l in answerLists)
            for (int i = l.Count - 2; i >= 0; i--)
                if (l[i].Seq is { } a && l[i + 1].Seq is { } b && a > b) l.RemoveAt(i);
    }

    public static void DropInputNoise(IEnumerable<List<Answer>> answerLists)
    {
        foreach (var l in answerLists)
            if (l.Any(a => a.Input == true)) l.RemoveAll(a => a.Input == false);
    }

    /// <summary>The drawing state recorded right after the header (the first `state` event), or null (older sessions).</summary>
    public static DrawingDigest? InitialState(IEnumerable<JsonElement> events)
    {
        foreach (var e in events)
            if (Str(e, "type") == "state" && e.TryGetProperty("state", out var st)) return DrawingDigest.FromJson(st);
        return null;
    }

    public static long Num(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    public static List<Step> Build(IEnumerable<JsonElement> events)
    {
        var steps = new List<Step>();
        Step? cur = null;
        int depth = 0;
        bool inLisp = false;
        string? lastModalTitle = null;
        Step? modalStep = null;
        Step? palette = null;   // the last palette step: a click right after its values joins it
        Step? modalOwner = null; var hadDialogBefore = false;   // the command that was running at the current modal_enter and its HadDialog before it (restored if that dialog is a security prompt)
        var modalByN = new Dictionary<long, Step>();   // event n of modal_enter / modal_title -> its dialog step
        foreach (var e in events)
        {
            var type = Str(e, "type");
            var t = Num(e, "t");
            switch (type)
            {
                case "tab":
                    if (IsOwnTab(Str(e, "id"))) break;
                    steps.Add(new Step { Kind = "tab", T = t, Tab = Str(e, "title"), Title = $"Activate ribbon tab \"{Str(e, "title") ?? Str(e, "id")}\"" });
                    break;
                case "ribbon":
                {
                    if (IsOwnRibbon(Str(e, "id"), Str(e, "tabId"))) break;   // older recordings: a click on Retrace' own Stop / Checkpoint
                    var text = Str(e, "text") ?? Str(e, "id") ?? "?";
                    cur = new Step
                    {
                        Kind = "ribbon", T = t, ItemId = Str(e, "id"), Macro = CleanMacro(Str(e, "macro")), Tab = Str(e, "tab"), Panel = Str(e, "panel"), Path = Str(e, "path"),
                        Title = "Click " + string.Join(" > ", new[] { Str(e, "tab"), Str(e, "panel"), text }.Where(s => !string.IsNullOrEmpty(s))),
                    };
                    steps.Add(cur);
                    break;
                }
                case "cmd_start":
                {
                    var cmd = Str(e, "command") ?? "?";
                    if (depth++ > 0) break; // nested / transparent: part of the running step
                    // a palette button that runs a command (SendStringToExecute): the command and its answers belong to the palette step
                    if (Str(e, "origin") == "palette" && palette is { Command: null, Button: not null } && steps.Count > 0 && ReferenceEquals(steps[^1], palette))
                    {
                        palette.Command = cmd;
                        cur = palette;
                        break;
                    }
                    // older recorders tagged commands of macro-less (built-in) buttons "typed/other": a command within 1 s is the click's
                    if (cur is { Kind: "ribbon", Command: null } && (Str(e, "origin") == "ribbon" || (cur.Macro == null && t - cur.T <= 1000))) { cur.Command = cmd; break; }
                    cur = new Step { Kind = "command", T = t, Command = cmd, Title = $"Type command {cmd}" };
                    steps.Add(cur);
                    break;
                }
                case "cmd_end" or "cmd_cancel" or "cmd_fail":
                    if (depth > 0) depth--;
                    break;
                case "lisp_end" or "lisp_cancel":
                    inLisp = false;
                    break;
                case "lisp_start":
                    inLisp = true;
                    cur = new Step { Kind = "lisp", T = t, Lisp = Str(e, "firstLine"), Title = $"Run LISP {Str(e, "firstLine")}" };
                    steps.Add(cur);
                    break;
                case "prompt":
                {
                    // Outside any command or LISP: a prompt of our own commands (older sessions recorded the checkpoint note).
                    if (cur == null || (depth == 0 && !inLisp)) break;
                    // Free input at a prompt that also takes keywords (CIRCLE's "radius or [Diameter]" given 10) comes back as a
                    // Keyword result; AutoCAD then re-asks the same message as a different kind. The typed text already answers it.
                    var prev = cur.Answers.Count > 0 ? cur.Answers[^1] : null;
                    if (prev is { Status: "Keyword" } && prev.Message == Str(e, "message") && prev.Kind != Str(e, "kind")) break;
                    cur.Answers.Add(new Answer(Str(e, "kind") ?? "?", Str(e, "status") ?? "?", e.TryGetProperty("value", out var v) ? v.Clone() : null, Str(e, "keyword"), Str(e, "message"), Bool(e, "input"), e.TryGetProperty("seq", out var sq) && sq.ValueKind == JsonValueKind.Number ? sq.GetInt64() : null, t));
                    break;
                }
                case "modal_enter":
                case "modal_title":
                    if (type == "modal_enter")
                    {
                        modalStep = new Step { Kind = "dialog", T = t, DialogTitle = Str(e, "title"), Title = "" };
                        steps.Add(modalStep);
                        modalOwner = cur; hadDialogBefore = cur?.HadDialog ?? false;
                        if (cur != null) cur.HadDialog = true;
                    }
                    else if (modalStep != null && !string.IsNullOrEmpty(Str(e, "title"))) modalStep.DialogTitle = Str(e, "title");
                    // AutoCAD's security prompt (loading an untrusted dll or LISP file) is a person's decision, not a step of the test: drop it.
                    // A replay that meets it stops with what to do (Replayer); a file that is trusted by now never shows it again.
                    if (modalStep != null && SecurityPrompt.IsRecorded(modalStep.DialogTitle, Str(e, "windowClass")))
                    {
                        steps.Remove(modalStep);
                        foreach (var k in modalByN.Where(kv => kv.Value == modalStep).Select(kv => kv.Key).ToList()) modalByN.Remove(k);
                        if (modalOwner != null) modalOwner.HadDialog = hadDialogBefore;
                        modalStep = null; lastModalTitle = null;
                        break;
                    }
                    lastModalTitle = modalStep?.DialogTitle;
                    if (modalStep != null) modalStep.Title = $"Dialog opens: \"{lastModalTitle}\" - fill it in by hand";
                    if (modalStep != null && e.TryGetProperty("n", out var nv) && nv.ValueKind == JsonValueKind.Number && nv.TryGetInt64(out var nn)) modalByN[nn] = modalStep;
                    break;
                case "dialog_values":
                    if (e.TryGetProperty("for", out var fv) && fv.ValueKind == JsonValueKind.Number && fv.TryGetInt64(out var fn) && modalByN.TryGetValue(fn, out var ds))
                    {
                        ds.ClosedWith = Str(e, "closedWith");
                        ds.ValuesSource = Str(e, "source");
                        ds.ValuesRead = e.TryGetProperty("read", out var rv) && rv.ValueKind == JsonValueKind.Number && rv.TryGetInt32(out var ri) ? ri : 0;
                        ds.Values = new List<FieldChange>();
                        if (e.TryGetProperty("fields", out var fa) && fa.ValueKind == JsonValueKind.Array)
                            foreach (var f in fa.EnumerateArray())
                                ds.Values.Add(new FieldChange(Str(f, "key") ?? "", Str(f, "label") ?? "", Str(f, "kind") ?? "", Str(f, "before") ?? "", Str(f, "after") ?? ""));
                    }
                    break;
                case "palette_values":
                case "palette_click":
                {
                    string? name = Str(e, "palette"), set = Str(e, "set");
                    bool join = type == "palette_click" && palette != null && steps.Count > 0 && ReferenceEquals(steps[^1], palette)
                                && palette.Button == null && palette.DialogTitle == name && palette.Panel == set;
                    if (!join) { palette = new Step { Kind = "palette", T = t, DialogTitle = name, Panel = set }; steps.Add(palette); }
                    if (type == "palette_click")
                    {
                        palette!.Button = Str(e, "button");
                        palette.ButtonIndex = e.TryGetProperty("index", out var bi) && bi.ValueKind == JsonValueKind.Number && bi.TryGetInt32(out var bn) ? bn : 1;
                    }
                    else
                    {
                        palette!.ValuesSource = Str(e, "source");
                        palette.ValuesRead = e.TryGetProperty("read", out var prv) && prv.ValueKind == JsonValueKind.Number && prv.TryGetInt32(out var pri) ? pri : 0;
                        palette.Values ??= new List<FieldChange>();
                        if (e.TryGetProperty("fields", out var pfa) && pfa.ValueKind == JsonValueKind.Array)
                            foreach (var f in pfa.EnumerateArray())
                                palette.Values.Add(new FieldChange(Str(f, "key") ?? "", Str(f, "label") ?? "", Str(f, "kind") ?? "", Str(f, "before") ?? "", Str(f, "after") ?? ""));
                    }
                    var changes = palette.Values?.Count ?? 0;
                    palette.Title = $"Palette \"{name}\": " + (changes > 0 ? $"{changes} value(s)" : "") + (changes > 0 && palette.Button != null ? ", then " : "")
                                    + (palette.Button != null ? $"click {palette.Button}" : changes == 0 ? "no changes" : "");
                    break;
                }
                case "mark":
                {
                    var note = Str(e, "note") ?? "";
                    var state = e.TryGetProperty("state", out var st) ? DrawingDigest.FromJson(st) : null;
                    steps.Add(new Step { Kind = "mark", T = t, Note = note, State = state, Title = note.Length == 0 ? "Checkpoint" : "Check: " + note });
                    break;
                }
            }
        }
        DropInputNoise(steps.Select(s => s.Answers)); // first: in mouse recordings the inner prompt may hold the click flag
        DropNested(steps.Select(s => s.Answers));
        return steps;
    }

    // ---- turning recorded answers into command-line input ------------------------------------

    static string Inv(double d) => d.ToString("0.############", CultureInfo.InvariantCulture);

    /// <summary>One answer as the text to type, or null if it cannot be typed. Each returned line is followed by Enter.</summary>
    public static List<string>? TypedLines(Answer a)
    {
        if (a.Status == "None") return new List<string> { "" };          // Enter
        if (a.Status == "Cancel") return new List<string> { "\u0003" };  // Esc/^C
        if (a.Status == "Keyword") return new List<string> { a.Keyword ?? "" };
        if (a.Status != "OK" || a.Value is not { } v) return null;
        switch (a.Kind)
        {
            case "point" or "corner":
                if (v.ValueKind != JsonValueKind.Array) return null;
                var c = v.EnumerateArray().Select(x => x.GetDouble()).ToArray();
                if (c.Length < 2) return null;
                return new List<string> { c.Length > 2 && Math.Abs(c[2]) > 1e-12 ? $"{Inv(c[0])},{Inv(c[1])},{Inv(c[2])}" : $"{Inv(c[0])},{Inv(c[1])}" };
            case "distance" or "double":
                return new List<string> { Inv(v.GetDouble()) };
            case "angle":
                return new List<string> { Inv(v.GetDouble() * 180.0 / Math.PI) }; // recorded in radians; typed in degrees (assumes AUNITS 0)
            case "integer":
                return new List<string> { v.GetInt64().ToString(CultureInfo.InvariantCulture) };
            case "string":
                return new List<string> { v.GetString() ?? "" };
            case "entity" or "nestedEntity":
                return Str(v, "handle") is { } h ? new List<string> { $"(handent \"{h}\")" } : null;
            case "selection":
            {
                if (!v.TryGetProperty("handles", out var hs) || hs.ValueKind != JsonValueKind.Array) return null;
                var lines = hs.EnumerateArray().Select(x => $"(handent \"{x.GetString()}\")").ToList();
                lines.Add(""); // Enter ends selection
                return lines;
            }
        }
        return null;
    }

    /// <summary>The command-line text that replays a step: command or converted macro, then the answers. Lines are joined with \n (= Enter).</summary>
    public static string? InputText(Step s, bool includeStart = true)
    {
        var sb = new StringBuilder();
        if (includeStart)
        {
            if (s.Kind == "ribbon")
            {
                var m = MacroToInput(s.Macro);
                if (m == null && s.Command != null) m = "_" + s.Command;
                if (m == null) return null;
                sb.Append(m);
            }
            else if (s.Kind == "command") sb.Append('_').Append(s.Command);
            else if (s.Kind == "lisp") sb.Append(s.Lisp);
            else return null;
            sb.Append('\n');
        }
        foreach (var a in s.Answers)
        {
            var lines = TypedLines(a);
            if (lines == null) return sb.ToString(); // stop at the first untypable answer; the rest is up to the tester
            // ^C cancels on its own; an Enter after it would repeat the command (seen replaying LINE ... Esc, R25.1)
            if (a.Status == "Cancel") return sb.Append('\u0003').ToString();
            foreach (var l in lines) sb.Append(l).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Input for a step whose command opens a dialog (recorded at <paramref name="dialogT"/>): First = the start plus the answers
    /// given before the dialog opened, After = the answers given after it (null if none). After is typed only once the dialog was
    /// confirmed; sent up front it would wait in AutoCAD's input queue and run as a command if the dialog is cancelled.</summary>
    public static (string? First, string? After) InputAroundDialog(Step s, long dialogT)
    {
        Step With(IEnumerable<Answer> answers) => new()
        {
            Kind = s.Kind, Title = s.Title, ItemId = s.ItemId, Macro = s.Macro, Tab = s.Tab, Panel = s.Panel, Path = s.Path,
            Command = s.Command, Lisp = s.Lisp, T = s.T, HadDialog = s.HadDialog, Answers = answers.ToList(),
        };
        var after = With(s.Answers.Where(a => a.T >= dialogT));
        var rest = after.Answers.Count == 0 ? null : InputText(after, includeStart: false);
        return (InputText(With(s.Answers.Where(a => a.T < dialogT))), string.IsNullOrEmpty(rest) ? null : rest);
    }

    /// <summary>Menu macro -> typed input: ^C -> Esc, ; -> Enter, \ (pause) dropped (the recorded answers fill it). Null for empty macros.</summary>
    public static string? MacroToInput(string? macro)
    {
        if (string.IsNullOrWhiteSpace(macro)) return null;
        var m = macro.Replace("^C", "\u0003", StringComparison.OrdinalIgnoreCase).Replace(";", "\n").Replace("\\", "").Replace("^M", "\n", StringComparison.OrdinalIgnoreCase);
        return m.TrimEnd('\n');
    }
}
