// Recordings ingest, part 4: write <docs>/recordings/{index.md, <test>.md, images/<test>/} and <docs>/data/recordings.json.
using System.Text;
using System.Text.Json;
using Retrace.Engine.Emit;
using Retrace.Engine.Util;

namespace Retrace.Engine.Recordings;

public sealed class RecordingWriter(RecordingAnalysis a, string outRoot)
{
    public const string Root = DocsLayout.Recordings;
    readonly OutputSink _sink = new(outRoot);
    /// <summary>Problems while copying screenshots (missing files).</summary>
    public List<string> Warnings { get; } = new();
    const string Arrow = " ▸ ";

    public int Write()
    {
        _sink.Write($"{Root}/index.md", Index());
        foreach (var s in a.Sessions) _sink.Write(PageOf(s), Session(s));
        _sink.Write(DocsLayout.RecordingsFile, JsonSerializer.Serialize(Machine(), SourceMapWriter.Json));
        return _sink.Count;
    }

    /// <summary>The page of one test, relative to the docs folder: `recordings/&lt;test&gt;.md`.</summary>
    static string PageOf(RecordingSession s) => $"{Root}/{s.Slug}.md";

    // ---- links -------------------------------------------------------------------------------

    /// <summary>Link from a page inside the recordings output (fromRel is relative to the out root) to a page of the engine output
    /// the sourcemap points at (doc is relative to the sourcemap's folder, may carry an #anchor).</summary>
    string LinkToMapDoc(string fromRel, string doc)
    {
        var map = a.Map!;
        var hash = doc.IndexOf('#');
        var file = hash >= 0 ? doc[..hash] : doc;
        var anchor = hash >= 0 ? doc[hash..] : "";
        var fromDir = Path.GetDirectoryName(Path.Combine(outRoot, fromRel.Replace('/', Path.DirectorySeparatorChar)))!;
        var target = Path.Combine(map.MapDir, file.Replace('/', Path.DirectorySeparatorChar));
        return Path.GetRelativePath(fromDir, target).Replace('\\', '/').Replace(" ", "%20") + anchor;
    }

    string EntryLink(string fromRel, SourceMapEntry e, string text) =>
        e.Doc != null && a.Map != null ? $"[{Md.Cell(text)}]({LinkToMapDoc(fromRel, e.Doc)})" : Md.Cell(text);

    // ---- index.md ----------------------------------------------------------------------------

    string Index()
    {
        const string doc = $"{Root}/index.md";
        var sb = new StringBuilder("# Recorded tests\n\n");
        sb.Append($"{a.Sessions.Count} recorded session(s), {a.Sessions.Sum(s => s.Steps.Count(x => x.Kind == "ribbon"))} ribbon clicks, ")
          .Append($"{a.Commands.Sum(c => c.Total)} commands. ");
        sb.Append(a.Map != null ? $"Joined to the source map in `{a.Map.MapDir}`.\n\n" : "No sourcemap given (no data/sourcemap.json): no source links, no ribbon coverage.\n\n");

        sb.Append("## Sessions\n\n");
        sb.Append(Md.Table(new[] { "Session", "Recorded with", "Duration", "Steps", "Ribbon clicks", "Marks", "Unmatched clicks" },
            a.Sessions.Select(s => new[]
            {
                $"[{Md.Cell(s.Name)}]({s.Slug}.md)",
                string.Join(" ", new[] { s.HeaderStr("product"), s.HeaderStr("autocad") }.Where(x => !string.IsNullOrEmpty(x))),
                FormatDuration(s.DurationMs),
                s.Steps.Count.ToString(),
                s.Steps.Count(x => x.Kind == "ribbon").ToString(),
                s.Steps.Count(x => x.Kind == "mark").ToString(),
                a.Map == null ? "-" : s.Steps.Count(x => x.Kind == "ribbon" && x.Join == null).ToString(),
            })));

        sb.Append("\n## Most used ribbon items\n\nHow often each button was clicked, and what it ran. 'Then' is the item clicked right after it most often.\n\n");
        sb.Append(Md.Table(new[] { "#", "Ribbon item", "Clicks", "Sessions", "Runs", "Then", "Source" },
            a.Ribbon.Take(40).Select((r, i) => new[]
            {
                (i + 1).ToString(),
                r.Entry != null ? EntryLink(doc, r.Entry, r.Label) : Md.Cell(r.Label) + " (not in sourcemap)",
                r.Count.ToString(), r.Sessions.Count.ToString(),
                string.Join(", ", r.Commands.OrderByDescending(c => c.Value).Take(3).Select(c => Md.Code(c.Key))),
                r.Next.Count == 0 ? "" : Md.Cell(r.Next.OrderByDescending(n => n.Value).First().Key) + $" ({r.Next.OrderByDescending(n => n.Value).First().Value})",
                Md.Code(r.Entry?.Source?.ToString()),
            })));

        sb.Append("\n## Hot sequences\n\nActions that follow each other (ribbon clicks and typed commands), 2 to N long, seen at least twice. Sub-sequences that always occur inside a longer one are left out.\n\n");
        if (a.Sequences.Count == 0) sb.Append("_No repeated sequences yet: record more sessions._\n");
        else sb.Append(Md.Table(new[] { "Sequence", "Times", "In sessions" },
            a.Sequences.Select(g => new[] { string.Join(" → ", g.Tokens.Select(t => Md.Cell(t))), g.Count.ToString(), g.Sessions.ToString() })));

        sb.Append("\n## Commands\n\n");
        sb.Append(Md.Table(new[] { "Command", "Runs", "From ribbon", "Typed/other", "Cancelled", "Failed", "Sessions" },
            a.Commands.Take(40).Select(c => new[] { Md.Code(c.Name), c.Total.ToString(), c.FromRibbon.ToString(), c.Typed.ToString(), c.Cancelled.ToString(), c.Failed.ToString(), c.Sessions.Count.ToString() })));

        if (a.Map != null)
        {
            sb.Append("\n## Ribbon coverage\n\nButtons clicked at least once, per tab, against all buttons in the sourcemap.\n\n");
            sb.Append(Md.Table(new[] { "Tab", "Exercised", "Total", "%" },
                a.Coverage.Select(c => new[]
                {
                    EntryLink(doc, c.Tab, c.Tab.Title), c.Exercised.Count.ToString(), c.Total.ToString(),
                    c.Total == 0 ? "-" : (100.0 * c.Exercised.Count / c.Total).ToString("0") + "%",
                })));
            var all = a.Coverage.Sum(c => c.Total);
            var done = a.Coverage.Sum(c => c.Exercised.Count);
            sb.Append($"\nTotal: {done} of {all} buttons ({(all == 0 ? 0 : 100.0 * done / all):0}%).\n\n");
            sb.Append("### Never exercised\n\n");
            foreach (var c in a.Coverage.Where(c => c.Missing.Count > 0))
            {
                sb.Append($"<details><summary>{Md.Cell(c.Tab.Title)}: {c.Missing.Count} not clicked</summary>\n\n");
                foreach (var g in c.Missing.GroupBy(m => m.Capture.GetValueOrDefault("panel") ?? ""))
                    sb.Append($"- **{(g.Key.Length == 0 ? "(no panel)" : Md.Cell(g.Key))}**: {string.Join(", ", g.Select(m => EntryLink(doc, m, m.Title)))}\n");
                sb.Append("\n</details>\n\n");
            }
        }

        if (a.UnmatchedRibbon.Count > 0)
        {
            sb.Append("## Clicks not found in the sourcemap\n\nA ribbon item the recorder saw but the scanned sources do not contain (other CUIX, stock AutoCAD ribbon, a plugin that was not scanned).\n\n");
            sb.Append(Md.Table(new[] { "Item", "Id", "Macro", "Clicks" },
                a.UnmatchedRibbon.GroupBy(s => (s.ItemId, s.Macro, Label: RecordingAnalysis.RibbonLabel(s)))
                    .OrderByDescending(g => g.Count())
                    .Select(g => new[] { Md.Cell(g.Key.Label), Md.Code(g.Key.ItemId), Md.Code(g.Key.Macro), g.Count().ToString() })));
        }
        return sb.ToString();
    }

    static string FormatDuration(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s" : $"{t.Seconds}s";
    }

    // ---- one page per session ----------------------------------------------------------------

    string Session(RecordingSession s)
    {
        var doc = PageOf(s);
        var sb = new StringBuilder($"# Manual test: {s.Name}\n\n");
        sb.Append("Reconstructed from a recorded session. Do the steps in order; **Expected** lines are checkpoints the tester wrote while recording.\n\n");
        sb.Append(Md.Table(new[] { "Field", "Value" }, new List<string?[]>
        {
            new[] { "Recording", Md.Code(Path.GetFileName(s.File)) },
            new[] { "AutoCAD", string.Join(" ", new[] { s.HeaderStr("product"), s.HeaderStr("autocad") }.Where(x => !string.IsNullOrEmpty(x))) },
            new[] { "Workspace / locale", $"{s.HeaderStr("workspace")} / {s.HeaderStr("locale")}" },
            new[] { "Started", s.HeaderStr("started") },
            new[] { "Duration", FormatDuration(s.DurationMs) + $" ({s.Events.Count} events)" },
        }.Concat(s.InitialState == null ? Array.Empty<string?[]>() : new[] { new[] { "Start state", Md.Cell(s.InitialState.Summary()) } }).ToList()));
        if (s.BadLines > 0) sb.Append($"\n> {s.BadLines} line(s) could not be read (torn write after a crash?) and were skipped.\n");
        sb.Append("\n[Back to the recordings](index.md)\n\n## Steps\n\n");

        int n = 0;
        // screenshots are copied beside the pages (recordings/images/<test>/), so the docs folder can be moved
        var copier = new ShotCopier(outRoot, $"{Root}/images/{s.Slug}", w => Warnings.Add($"recordings: {w}"));
        void Pics(List<RecordingShot> shots, string alt)
        {
            foreach (var shot in shots)
                if (copier.Copy(shot) is { } rel) sb.Append($"   ![{StepWording.Alt(alt)}]({Md.Rel(doc, rel)})\n");
        }
        foreach (var st in s.Steps)
        {
            switch (st.Kind)
            {
                case "ribbon":
                {
                    n++;
                    var path = string.Join(Arrow, new[] { st.Tab, st.Panel }.Where(x => !string.IsNullOrWhiteSpace(x)).Append(st.Text ?? st.ItemId ?? "?"));
                    var line = $"{n}. Click **{Md.Cell(path)}**";
                    if (st.Join != null) line += $" ({EntryLink(doc, st.Join.Entry, "ribbon page")}{(st.Join.MatchedBy == "macro" ? ", matched by command" : "")})";
                    else if (a.Map != null) line += " (not in the sourcemap)";
                    sb.Append(line).Append('\n');
                    var meta = new List<string>();
                    if (st.Commands.Count > 0) meta.Add("runs " + string.Join(", ", st.Commands.Select(c => Md.Code(c))));
                    else if (!string.IsNullOrEmpty(st.Macro)) meta.Add("macro " + Md.Code(st.Macro));
                    if (st.Join?.Entry.Source != null) meta.Add("defined at " + Md.Code(st.Join.Entry.Source.ToString()));
                    if (st.ItemId != null) meta.Add("id " + Md.Code(st.ItemId));
                    if (meta.Count > 0) sb.Append($"   - {string.Join("; ", meta)}\n");
                    Pics(st.Shots, StepAlt(st)); Answers(sb, st, Pics);
                    break;
                }
                case "command":
                    n++;
                    sb.Append($"{n}. {(st.Outcome == "unknown" ? "Type unknown command" : "Type command")} {Md.Code(st.Command)} on the command line\n");
                    Pics(st.Shots, StepAlt(st)); Answers(sb, st, Pics);
                    break;
                case "lisp":
                    n++;
                    sb.Append($"{n}. Run LISP {Md.Code(st.Lisp)}\n");
                    Pics(st.Shots, StepAlt(st)); Answers(sb, st, Pics);
                    break;
                case "dialog":
                {
                    n++;
                    var title = string.IsNullOrWhiteSpace(st.DialogTitle) ? "(untitled dialog)" : st.DialogTitle;
                    var line = $"{n}. Dialog opens: **\"{Md.Cell(title)}\"** - fill it in by hand";
                    if (st.Form != null) line += $" ({EntryLink(doc, st.Form.Entry, "ui page")}{(st.Form.Entry.Source != null ? ", " + Md.Code(st.Form.Entry.Source.ToString()) : "")})";
                    sb.Append(line).Append('\n');
                    Pics(st.Shots, "Dialog " + title);
                    break;
                }
                case "palette":
                {
                    n++;
                    var name = string.IsNullOrWhiteSpace(st.DialogTitle) ? "(untitled)" : st.DialogTitle;
                    var what = st.Native != null ? ": " + st.Native.Describe(Md.Cell, x => $"**{x}**") + (st.OpensDrawing != null ? $" (opens {Md.Code(st.OpensDrawing)})" : "")
                        : string.IsNullOrWhiteSpace(st.Text) ? "" : $": click **{Md.Cell(st.Text)}**";
                    sb.Append($"{n}. Palette **\"{Md.Cell(name)}\"**{what}\n");
                    foreach (var v in st.Values)
                        if (StepWording.ChangeText(v, Md.Cell, Md.Code, bold: true) is { } t) sb.Append("   - ").Append(t).Append('\n');
                    Pics(st.Shots, "Palette " + name);
                    break;
                }
                case "mark":
                    n++;
                    sb.Append($"{n}. **Expected:** {(string.IsNullOrWhiteSpace(st.Note) ? "(checkpoint, no note)" : Md.Cell(st.Note))}\n");
                    Pics(st.Shots, "Checkpoint " + st.Note);
                    if (st.State != null) sb.Append($"   - {Md.Cell(st.State.Summary())} ({st.State.Count} object{(st.State.Count == 1 ? "" : "s")}, hash {Md.Code(st.State.Hash)})\n");
                    break;
                case "doc":
                    n++;
                    sb.Append($"{n}. {Md.Cell(st.Note)}\n");
                    break;
            }
        }
        if (n == 0) sb.Append("_No steps recorded._\n");
        return sb.ToString();
    }

    static string StepAlt(RecordingStep st) => st.Kind switch
    {
        "ribbon" => "Click " + StepWording.RibbonPath(st),
        "command" => "Type " + st.Command,
        _ => "Run " + st.Lisp,
    };

    static void Answers(StringBuilder sb, RecordingStep st, Action<List<RecordingShot>, string> pics)
    {
        if (st.Outcome == "cancelled") sb.Append("   - (the command was cancelled)\n");
        if (st.Outcome == "failed") sb.Append("   - (the command failed)\n");
        foreach (var ans in st.Answers)
        {
            var msg = string.IsNullOrWhiteSpace(ans.Message) ? ans.Kind : ans.Message!.Trim();
            sb.Append($"   - prompt {Md.Code(msg)} → {Md.Cell(ans.Describe())}\n");
            pics(ans.Shots, "Prompt " + msg);
        }
        pics(st.EndShots, StepAlt(st) + " (end)");
    }

    // ---- recordings.json -----------------------------------------------------------------------

    static object StateJson(RecordingState st) => new { summary = st.Summary(), count = st.Count, byType = st.ByType, byLayer = st.ByLayer, layers = st.Layers, hash = st.Hash, v = st.Version };

    object Machine() => new
    {
        schema = "retrace/recordings@1",
        map = a.Map?.MapDir,
        sessions = a.Sessions.Select(s => new
        {
            name = s.Name, page = PageOf(s), file = s.File, durationMs = s.DurationMs, events = s.Events.Count,
            steps = s.Steps.Count, ribbonClicks = s.Steps.Count(x => x.Kind == "ribbon"),
            autocad = s.HeaderStr("autocad"), product = s.HeaderStr("product"), workspace = s.HeaderStr("workspace"),
            startState = s.InitialState == null ? null : StateJson(s.InitialState),
            checkpoints = s.Steps.Where(x => x.Kind == "mark").Select((x, i) => new { index = i + 1, note = x.Note, state = x.State == null ? null : StateJson(x.State) }),
        }),
        ribbon = a.Ribbon.Select(r => new
        {
            label = r.Label, itemId = r.ItemId, count = r.Count, sessions = r.Sessions.Count,
            commands = r.Commands.OrderByDescending(c => c.Value).ToDictionary(c => c.Key, c => c.Value),
            next = r.Next.OrderByDescending(c => c.Value).Take(5).ToDictionary(c => c.Key, c => c.Value),
            sourcemapId = r.Entry?.Id, matchedBy = r.MatchedBy, source = r.Entry?.Source,
        }),
        commands = a.Commands.Select(c => new { name = c.Name, runs = c.Total, fromRibbon = c.FromRibbon, typed = c.Typed, cancelled = c.Cancelled, failed = c.Failed, sessions = c.Sessions.Count }),
        sequences = a.Sequences.Select(g => new { tokens = g.Tokens, count = g.Count, sessions = g.Sessions }),
        coverage = a.Coverage.Select(c => new
        {
            tab = c.Tab.Title, tabId = c.Tab.Id, total = c.Total, exercised = c.Exercised.Count,
            missing = c.Missing.Select(m => new { id = m.Id, title = m.Title, panel = m.Capture.GetValueOrDefault("panel"), command = m.Command }),
        }),
        unmatchedRibbon = a.UnmatchedRibbon.Select(s => new { id = s.ItemId, label = RecordingAnalysis.RibbonLabel(s), macro = s.Macro }).Distinct(),
    };
}
