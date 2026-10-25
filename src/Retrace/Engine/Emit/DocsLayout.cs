namespace Retrace.Engine.Emit;

/// <summary>Where each kind of page and file goes under the docs folder. Paths are '/'-separated and relative to the docs folder.
/// <code>
/// README.md
/// guide\      owner-edited (merge markers): index.md, ribbon\..., howto\..., images\
/// reference\  regenerated every run: ribbon\, commands\, dialogs\, images\ribbon, images\dialogs, projects.md
/// test-plans\ manual test scripts from the source
/// recordings\ from the tests folder: index.md, one page per test, images\&lt;test&gt;\
/// data\       sourcemap.json, recordings.json, model.json
/// </code></summary>
public static class DocsLayout
{
    public const string Readme = "README.md";
    public const string Guide = "guide";
    public const string Reference = "reference";
    public const string TestPlans = "test-plans";
    public const string Recordings = "recordings";
    public const string Data = "data";

    public const string GuideImages = Guide + "/images";
    public const string RefRibbon = Reference + "/ribbon";
    public const string RefCommands = Reference + "/commands";
    public const string RefDialogs = Reference + "/dialogs";
    public const string RefImages = Reference + "/images";
    public const string RefProjects = Reference + "/projects.md";

    public const string SourceMapFile = Data + "/sourcemap.json";
    public const string RecordingsFile = Data + "/recordings.json";
    public const string ModelFile = Data + "/model.json";

    /// <summary>The guide page that mirrors a reference page: `reference/ribbon/x.md` -> `guide/ribbon/x.md`.</summary>
    public static string GuidePageOf(string referencePage) => Guide + referencePage[Reference.Length..];
}
