using Markdig;

using Retrace.AutoCad;

namespace Retrace.Ui;

/// <summary>Markdown to HTML with Markdig: pipe tables etc. (advanced extensions), raw HTML allowed so the engine's `&lt;a id=...&gt;` anchors work.</summary>
public static class MarkdownPage
{
    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    const string Css = """
        :root { color-scheme: light dark; }
        body { font-family: "Segoe UI", system-ui, sans-serif; font-size: 15px; line-height: 1.55; margin: 0; padding: 16px 22px 48px; background: Canvas; color: CanvasText; }
        #content { max-width: 900px; margin: 0 auto; }
        h1, h2, h3, h4 { line-height: 1.25; margin: 1.4em 0 .5em; }
        h1 { font-size: 1.7em; border-bottom: 1px solid #8884; padding-bottom: .2em; }
        h2 { font-size: 1.35em; border-bottom: 1px solid #8883; padding-bottom: .15em; }
        a { color: #2b6cb0; }
        img, svg { max-width: 100%; height: auto; }
        table { border-collapse: collapse; margin: .8em 0; }
        th, td { border: 1px solid #8886; padding: 4px 9px; text-align: left; vertical-align: top; }
        th { background: #8882; }
        code { font-family: Consolas, "Cascadia Mono", monospace; font-size: .92em; background: #8832; padding: 1px 4px; border-radius: 3px; }
        pre { background: #8822; padding: 10px 12px; overflow: auto; border-radius: 4px; }
        pre code { background: none; padding: 0; }
        blockquote { margin: .8em 0; padding: .1em 1em; border-left: 4px solid #8886; color: GrayText; }
        hr { border: 0; border-top: 1px solid #8884; margin: 1.5em 0; }
        """;

    /// <summary>Only the body markup (used for live updates of #content).</summary>
    public static string Body(string markdown) => Markdown.ToHtml(markdown ?? "", Pipeline);

    public static string Page(string markdown, string title = "") =>
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>"
        + System.Net.WebUtility.HtmlEncode(title) + "</title><style>" + Css + "</style></head><body><div id=\"content\">"
        + Body(markdown) + "</div></body></html>";

    public static string Message(string title, string text) =>
        Page("# " + title + "\n\n" + text, title);
}
