// guide/howto/<session>.md: one how-to page per recorded session (--sessions), same marker/merge rules as the tab pages.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Retrace.Engine.Recordings;
using Retrace.Engine.Util;

namespace Retrace.Engine.Emit;

/// <summary>A how-to page: Name is unique among how-tos (a repeated session name gets " (2)"), Doc is relative to the output root.</summary>
public sealed record HowTo(RecordingSession Session, string Name, string Slug)
{
    public string Doc => $"{GuideWriter.Dir}/howto/{Slug}.md";
}

public sealed partial class GuideWriter
{
    private const string HowToStub = "_What does this procedure achieve, and when should someone follow it? " + OwnerNote + "_";

    private List<HowTo> _howTos = new();
    /// <summary>Source map id of a ribbon item -> how-tos with a step that clicks it, in how-to order.</summary>
    private readonly Dictionary<string, List<HowTo>> _usedIn = new(StringComparer.Ordinal);

    private IReadOnlyList<HowTo> UsedIn(string itemId) => _usedIn.TryGetValue(itemId, out var l) ? l : [];

    /// <summary>Names and slugs are fixed here, in an order that does not depend on the argument order, so a rerun gives the same pages.</summary>
    private void PlanHowTos(IReadOnlyList<RecordingSession> all)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<HowTo>();
        var ordered = all
            .Select(s => (s, name: string.IsNullOrWhiteSpace(s.HeaderStr("name")) ? s.Name : s.HeaderStr("name")!.Trim()))
            .OrderBy(x => Slug.Of(x.name), StringComparer.Ordinal).ThenBy(x => x.name, StringComparer.Ordinal)
            .ThenBy(x => x.s.HeaderStr("started") ?? "", StringComparer.Ordinal).ThenBy(x => x.s.Name, StringComparer.Ordinal)
            .ThenBy(x => x.s.File, StringComparer.Ordinal);
        foreach (var (s, name) in ordered)
        {
            var slug = Slug.Of(name);
            var u = slug;
            for (int i = 2; !used.Add(u); i++) u = $"{slug}-{i}";
            var shown = name;
            for (int i = 2; !names.Add(shown); i++) shown = $"{name} ({i})";
            list.Add(new HowTo(s, shown, u));
        }
        _howTos = list;

        foreach (var h in list)
            foreach (var st in h.Session.Steps.Where(x => x is { Kind: "ribbon", Join: not null }))
            {
                var id = st.Join!.Entry.Id;
                if (!_usedIn.TryGetValue(id, out var l)) _usedIn[id] = l = new List<HowTo>();
                if (!l.Contains(h)) l.Add(h);
            }
    }

    private void WriteHowTos()
    {
        foreach (var h in _howTos) WritePage(h.Doc, "", HowToBlocks(h));
    }

    private List<GuideBlock> HowToBlocks(HowTo h)
    {
        var s = h.Session;
        var doc = h.Doc;
        var head = new StringBuilder($"# {Md.Cell(h.Name)}\n\n");
        var meta = StepWording.MetaLine(s);
        if (meta.Length > 0) head.Append(meta).Append(".\n\n");
        head.Append($"Manual test page: [{Md.Cell(s.Name)}]({Md.Rel(doc, $"{DocsLayout.Recordings}/{s.Slug}.md")})\n");
        var blocks = new List<GuideBlock> { new("page", head.ToString(), HowToStub) };

        // Key = step:<kind>:<what>:<n>, n = how many earlier steps had the same kind and what. Inserting a step elsewhere
        // leaves the other keys alone (an ordinal over all steps would renumber every key after the insert).
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        int number = 0;
        var shots = new ShotCopier(opt.Output, $"{DocsLayout.GuideImages}/howto/{h.Slug}", w => M.Warnings.Add($"guide: {w}"));
        foreach (var st in s.Steps)
        {
            var text = StepText(st, doc, number + 1, shots, out var kind, out var what);
            if (text == null) continue;
            number++;
            var baseKey = $"step:{kind}:{what}";
            var n = seen[baseKey] = seen.GetValueOrDefault(baseKey) + 1;
            blocks.Add(new($"{baseKey}:{n}", text, ""));
        }
        return blocks;
    }

    private static string KeyPart(string? s, int max = 40)
    {
        var t = Slug.Of(s);
        return t.Length > max ? t[..max].TrimEnd('-') : t;
    }

    /// <summary>Markdown for one step (null: not a step), with the key parts for it. The wording is <see cref="StepWording"/>, shared with the test script.</summary>
    private string? StepText(RecordingStep st, string doc, int n, ShotCopier shots, out string kind, out string what)
    {
        kind = st.Kind;
        what = "";
        switch (st.Kind)
        {
            case "ribbon":
                what = st.ItemId != null ? GuideMerge.SafeKey(st.ItemId) : st.Join != null ? SourceMapIndex.LastSegment(st.Join.Entry.Id) : KeyPart(StepWording.RibbonPath(st));
                break;
            case "command": what = KeyPart(st.Command); break;
            case "lisp": what = KeyPart(st.Lisp); break;
            case "dialog": what = KeyPart(StepWording.DialogTitle(st)); break;
            case "mark": what = KeyPart(st.Note, 24); break;
            case "doc": what = KeyPart(st.Note, 30); break;
            default: return null;
        }
        var ctx = new WordingContext
        {
            RibbonHref = s => s.Join != null && _sectionOf.TryGetValue(s.Join.Entry.Id, out var target) ? $"{Md.Rel(doc, target.Doc)}#{target.Anchor}" : null,
            ShotHref = shot => shots.Copy(shot) is { } rel ? Md.Rel(doc, rel) : null,
            DialogPicture = s =>
            {
                if (s.Form != null && ix.FormsById.TryGetValue(s.Form.Entry.Id.StartsWith("form:", StringComparison.Ordinal) ? s.Form.Entry.Id[5..] : s.Form.Entry.Id, out var f)
                    && FormImage(f) is { } img)
                    return (f.Title, Md.Rel(doc, img));
                return null;
            },
        };
        return StepWording.Render(st, n, ctx);
    }

    // The wording lives in StepWording; these stay for callers and tests that know GuideWriter.
    public static string StateText(RecordingState state) => StepWording.StateText(state);
    public static string? PromptSubject(string? message) => StepWording.PromptSubject(message);
    public static string AnswerText(RecordingAnswer a) => StepWording.AnswerText(a);
}

