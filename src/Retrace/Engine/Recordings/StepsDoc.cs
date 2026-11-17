// Reads a written "steps.md" back (with Markdig) for the Word / Excel export.
using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Retrace.Engine.Recordings;

/// <summary>One numbered step of a test script, as the Excel sheet shows it. <paramref name="Where"/>: the location line under the step's action ("Ribbon › Home tab › Draw panel"), "" when there is none.</summary>
public sealed record StepRow(int Number, string Action, string Details, string Check, IReadOnlyList<string> Pictures, string Where = "");

/// <summary>One row of the Excel sheet: one thing the tester does. <paramref name="Label"/>: "3" for a step, "3.2" for its second sub-step (the Word document numbers the steps only). <paramref name="Details"/>: the
/// change list of a dialog or palette, one line each. <paramref name="Expected"/>: what the tester should see (a check). <paramref name="Pictures"/>: every picture of the row in the order steps.md shows them;
/// <paramref name="Filled"/>: the row is a dialog or palette filled in, so its last picture (the filled-in one) is the one the sheet shows.</summary>
public sealed record SheetRow(string Label, string Action, string Where, string Details, string Expected, IReadOnlyList<string> Pictures, bool Filled)
{
    /// <summary>The picture the sheet embeds: one per row.</summary>
    public string? Picture => Pictures.Count == 0 ? null : Filled ? Pictures[^1] : Pictures[0];
}

/// <summary>A parsed "steps.md": the Markdig tree plus the pieces the Excel export needs.</summary>
public sealed class StepsDoc
{
    public MarkdownDocument Ast { get; }
    public string Dir { get; }
    public string Name { get; private set; } = "";
    public string Meta { get; private set; } = "";
    public string StartWith { get; private set; } = "";
    /// <summary>The "End check:" line of the Result section ("Model space has 2 objects, 1 layer, 1 layout"), "" when there is none.</summary>
    public string EndCheck { get; private set; } = "";
    public List<StepRow> Steps { get; } = [];
    /// <summary>The Excel rows: a step, then one row per sub-step of a command (each answer, each dialog filled in), see <see cref="SheetRow"/>.</summary>
    public List<SheetRow> Rows { get; } = [];

    /// <summary>Checkbox text becomes a ballot box before parsing, so every inline splitter sees one character.</summary>
    public static string Prepare(string md) => md.Replace("[ ]", "☐").Replace("[x]", "☒").Replace("[X]", "☒");

    public static MarkdownPipeline Pipeline { get; } = new MarkdownPipelineBuilder().UsePipeTables().Build();

    public StepsDoc(string markdown, string dir)
    {
        Dir = dir;
        Ast = Markdown.Parse(Prepare(markdown), Pipeline);
        Extract();
        // the end check is what the tester should see after the last row
        if (EndCheck.Length > 0 && Rows.Count > 0)
        {
            var last = Rows[^1];
            Rows[^1] = last with { Expected = last.Expected.Length > 0 ? last.Expected + "\n" + EndCheck : EndCheck };
        }
    }

    public static StepsDoc Load(string stepsFile) => new(File.ReadAllText(stepsFile), Path.GetDirectoryName(Path.GetFullPath(stepsFile)) ?? "");

    void Extract()
    {
        bool inSteps = false;
        foreach (var b in Ast)
        {
            if (b is HeadingBlock h)
            {
                var t = PlainText(h.Inline);
                if (h.Level == 1 && Name.Length == 0) Name = t;
                inSteps = h.Level == 2 && t.Equals("Steps", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inSteps)
            {
                if (b is ParagraphBlock p)
                {
                    var t = PlainText(p.Inline);
                    if (t.StartsWith("Start with:", StringComparison.OrdinalIgnoreCase)) StartWith = t["Start with:".Length..].Trim();
                    else if (t.StartsWith("End check:", StringComparison.OrdinalIgnoreCase)) EndCheck = t["End check:".Length..].Trim().TrimEnd('.');
                    else if (t.StartsWith("Recorded", StringComparison.Ordinal) && Meta.Length == 0) Meta = t;
                }
                continue;
            }
            if (b is ListBlock list && list.IsOrdered)
            {
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    Steps.Add(ReadStep(item, Steps.Count + 1));
                    ReadRows(item, Steps.Count);
                }
            }
        }
    }

    StepRow ReadStep(ListItemBlock item, int number)
    {
        string action = "";
        var details = new List<string>();
        var pictures = new List<string>();
        string where = "";
        foreach (var b in item)
        {
            if (b is ParagraphBlock p)
            {
                AddPictures(pictures, p.Inline);
                var t = PlainText(p.Inline);
                if (t.Length == 0 || (action.Length > 0 && IsPictureCaption(t))) continue;
                if (action.Length == 0) action = t;
                else if (where.Length == 0 && details.Count == 0 && IsWhere(p)) where = t;
                else details.Add(t);
            }
            else if (b is ListBlock l) CollectList(l, 0, details, pictures);
        }
        var check = action.StartsWith("Check:", StringComparison.OrdinalIgnoreCase) ? action["Check:".Length..].Trim() : "";
        return new StepRow(number, action, string.Join("\n", details), check, pictures, where);
    }

    /// <summary>True when the sub-items of this step are steps of their own (the sheet's 1.1, 1.2 rows): not a dialog or palette filled in, not a checkpoint.</summary>
    public static bool IsStepWithSubSteps(ListItemBlock item)
    {
        var first = item.OfType<ParagraphBlock>().Select(p => PlainText(p.Inline)).FirstOrDefault(t => t.Length > 0) ?? "";
        return !IsFillLine(first) && !first.StartsWith("Check:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when a list item has a line of text (a sub-step that is only a picture has none).</summary>
    public static bool HasText(ListItemBlock item) => item.OfType<ParagraphBlock>().Any(p => PlainText(p.Inline).Length > 0 && !IsPictureCaption(PlainText(p.Inline)));

    /// <summary>A dialog or palette filled in: its sub-items are one change list, not steps of their own. The 0.6.13 wording ("A dialog opens: ...", "In the ... palette ...") counts too, so an old steps.md still gives one row per action.</summary>
    static bool IsFillLine(string t) =>
        t.StartsWith("Fill in the ", StringComparison.Ordinal) || t.StartsWith("Cancel the ", StringComparison.Ordinal)
        || t.StartsWith("A dialog opens:", StringComparison.Ordinal) || t.StartsWith("A nested dialog opens:", StringComparison.Ordinal)
        || (t.StartsWith("In the ", StringComparison.Ordinal) && t.Contains(" palette", StringComparison.Ordinal));

    /// <summary>The rows of one step. A dialog or palette filled in ("Fill in the X dialog") is one row whose Details are its change list; a ribbon click, a typed command and the like is one row,
    /// then one row per sub-item (each answer; a dialog it opened with its change list as Details). A checkpoint's text is the Expected of its row.</summary>
    void ReadRows(ListItemBlock item, int number)
    {
        string action = "", where = "";
        var own = new List<string>();
        var opens = new List<string>();   // "The Pick palette opens." under an end picture: what the tester should see
        var subs = new List<ListBlock>();
        foreach (var b in item)
        {
            if (b is ParagraphBlock p)
            {
                AddPictures(own, p.Inline);
                var t = PlainText(p.Inline);
                if (t.Length == 0 || (action.Length > 0 && IsPictureCaption(t))) continue;
                if (action.Length == 0) action = t;
                else if (where.Length == 0 && IsWhere(p)) where = t;
                else if (Emit.StepWording.IsOpensCaption(t)) opens.Add(t);
            }
            else if (b is ListBlock l) subs.Add(l);
        }
        var bullets = subs.SelectMany(l => l.OfType<ListItemBlock>()).ToList();
        var check = action.StartsWith("Check:", StringComparison.OrdinalIgnoreCase);
        if (check)
        {
            // what the checkpoint's `near` text says goes to Details, so the tester sees it
            var notes = new List<string>();
            foreach (var bu in bullets) CollectBullet(bu, 0, notes, own);
            Rows.Add(new SheetRow(number.ToString(), "Check the drawing", where, string.Join("\n", notes), action["Check:".Length..].Trim(), own, false));
            return;
        }
        if (IsFillLine(action))
        {
            var lines = new List<string>();
            foreach (var bu in bullets) CollectBullet(bu, 0, lines, own);
            Rows.Add(new SheetRow(number.ToString(), action, where, string.Join("\n", lines), "", own, true));
            return;
        }
        Rows.Add(new SheetRow(number.ToString(), action, where, "", string.Join("\n", opens), own, false));
        int k = 0;
        foreach (var bu in bullets)
        {
            var pics = new List<string>();
            string text = "";
            var lines = new List<string>();
            foreach (var b in bu)
            {
                if (b is ParagraphBlock p)
                {
                    AddPictures(pics, p.Inline);
                    var t = PlainText(p.Inline);
                    if (t.Length == 0 || IsPictureCaption(t)) continue;
                    if (text.Length == 0) text = t; else lines.Add(t);
                }
                else if (b is ListBlock l) foreach (var inner in l.OfType<ListItemBlock>()) CollectBullet(inner, 0, lines, pics);
            }
            if (text.Length == 0) continue;
            Rows.Add(new SheetRow($"{number}.{++k}", text, "", string.Join("\n", lines), "", pics, IsFillLine(text)));
        }
    }

    /// <summary>The text of a sub-item and of the ones under it, one line each (deeper ones indented), and its pictures.</summary>
    void CollectBullet(ListItemBlock bu, int depth, List<string> lines, List<string> pictures)
    {
        foreach (var b in bu)
        {
            if (b is ParagraphBlock p)
            {
                AddPictures(pictures, p.Inline);
                var t = PlainText(p.Inline);
                if (t.Length == 0 || IsPictureCaption(t)) continue;
                lines.Add(new string(' ', depth * 2) + t);
            }
            else if (b is ListBlock l) foreach (var inner in l.OfType<ListItemBlock>()) CollectBullet(inner, depth + 1, lines, pictures);
        }
    }

    /// <summary>The location line of a step in steps.md: a paragraph that is one italic span (`*Ribbon › Home tab › Draw panel*`) right under the step's action line.</summary>
    public static bool IsWhere(ParagraphBlock p) =>
        p.Inline?.FirstChild is EmphasisInline { DelimiterCount: 1 } em && em.NextSibling == null;

    /// <summary>The italic line "Settings (not default): ..." / "Settings: all at their defaults" steps.md carries under its title; the Word and Excel exports leave it out.</summary>
    public static bool IsSettingsLine(ParagraphBlock p)
    {
        if (!IsWhere(p)) return false;
        var t = PlainText(p.Inline);
        return t.StartsWith("Settings (not default):", StringComparison.Ordinal) || t == "Settings: all at their defaults";
    }

    void CollectList(ListBlock list, int depth, List<string> lines, List<string> pictures)
    {
        foreach (var item in list.OfType<ListItemBlock>())
        {
            bool first = true;
            foreach (var b in item)
            {
                if (b is ParagraphBlock p)
                {
                    AddPictures(pictures, p.Inline);
                    var t = PlainText(p.Inline);
                    if (t.Length == 0 || IsPictureCaption(t)) continue;
                    lines.Add(new string(' ', depth * 2) + t);
                    first = false;
                }
                else if (b is ListBlock l) CollectList(l, first ? depth : depth + 1, lines, pictures);
            }
        }
    }

    static void AddPictures(List<string> into, ContainerInline? c)
    {
        foreach (var url in Images(c)) if (!into.Contains(url)) into.Add(url);
    }

    /// <summary>The captions over a dialog's before/after pictures ("Before:", "After (what it should look like ...):"): they say nothing
    /// without the pictures, so the spreadsheet's Details leave them out.</summary>
    public static bool IsPictureCaption(string t) =>
        t == "Before:" || (t.StartsWith("After (", StringComparison.Ordinal) && t.EndsWith("):", StringComparison.Ordinal));

    /// <summary>Image targets (as written in the markdown, still %20-escaped) in an inline tree.</summary>
    public static IEnumerable<string> Images(ContainerInline? c)
    {
        if (c == null) yield break;
        foreach (var inl in c.Descendants<LinkInline>())
            if (inl.IsImage && !string.IsNullOrEmpty(inl.Url)) yield return inl.Url!;
    }

    /// <summary>The text of an inline tree without markup; pictures are left out.</summary>
    public static string PlainText(ContainerInline? c)
    {
        var sb = new StringBuilder();
        Append(sb, c);
        return sb.ToString().Trim();
    }

    static void Append(StringBuilder sb, ContainerInline? c)
    {
        if (c == null) return;
        for (var n = c.FirstChild; n != null; n = n.NextSibling)
            switch (n)
            {
                case LiteralInline l: sb.Append(l.Content.ToString()); break;
                case CodeInline code: sb.Append(code.Content); break;
                case LineBreakInline: sb.Append(' '); break;
                case LinkInline { IsImage: true }: break;
                case ContainerInline ci: Append(sb, ci); break;   // emphasis, links
            }
    }

    /// <summary>Resolves a link target of the markdown (%20-escaped, forward slashes) to a path on disk.</summary>
    public string ResolvePath(string url) => Path.GetFullPath(Path.Combine(Dir, Uri.UnescapeDataString(url).Replace('/', Path.DirectorySeparatorChar)));
}
