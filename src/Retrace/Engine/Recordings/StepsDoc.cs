// Reads a written "steps.md" back (with Markdig) for the Word / Excel export.
using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Retrace.Engine.Recordings;

/// <summary>One numbered step of a test script, as the Excel sheet shows it.</summary>
public sealed record StepRow(int Number, string Action, string Details, string Check, IReadOnlyList<string> Pictures);

/// <summary>A parsed "steps.md": the Markdig tree plus the pieces the Excel export needs.</summary>
public sealed class StepsDoc
{
    public MarkdownDocument Ast { get; }
    public string Dir { get; }
    public string Name { get; private set; } = "";
    public string Meta { get; private set; } = "";
    public string StartWith { get; private set; } = "";
    public List<StepRow> Steps { get; } = [];

    /// <summary>Checkbox text becomes a ballot box before parsing, so every inline splitter sees one character.</summary>
    public static string Prepare(string md) => md.Replace("[ ]", "☐").Replace("[x]", "☒").Replace("[X]", "☒");

    public static MarkdownPipeline Pipeline { get; } = new MarkdownPipelineBuilder().UsePipeTables().Build();

    public StepsDoc(string markdown, string dir)
    {
        Dir = dir;
        Ast = Markdown.Parse(Prepare(markdown), Pipeline);
        Extract();
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
                    else if (t.StartsWith("Recorded", StringComparison.Ordinal) && Meta.Length == 0) Meta = t;
                }
                continue;
            }
            if (b is ListBlock list && list.IsOrdered)
            {
                foreach (var item in list.OfType<ListItemBlock>()) Steps.Add(ReadStep(item, Steps.Count + 1));
            }
        }
    }

    StepRow ReadStep(ListItemBlock item, int number)
    {
        string action = "";
        var details = new List<string>();
        var pictures = new List<string>();
        foreach (var b in item)
        {
            if (b is ParagraphBlock p)
            {
                AddPictures(pictures, p.Inline);
                var t = PlainText(p.Inline);
                if (t.Length == 0 || (action.Length > 0 && IsPictureCaption(t))) continue;
                if (action.Length == 0) action = t; else details.Add(t);
            }
            else if (b is ListBlock l) CollectList(l, 0, details, pictures);
        }
        var check = action.StartsWith("Check:", StringComparison.OrdinalIgnoreCase) ? action["Check:".Length..].Trim() : "";
        return new StepRow(number, action, string.Join("\n", details), check, pictures);
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
