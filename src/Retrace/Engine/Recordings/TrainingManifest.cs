// tdoc.json: what a Tdoc run produced (tests, steps, pictures, checkpoint results). TrainingDocx turns it into a Word file.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Retrace.Engine.Recordings;

/// <summary>`tdoc.json` beside the training document and its pictures. JSON names are camelCase; unknown or missing fields are tolerated on read.</summary>
public sealed class TrainingManifest
{
    public string Title { get; set; } = "";
    /// <summary>ISO 8601, UTC.</summary>
    public string CreatedUtc { get; set; } = "";
    public string RetraceVersion { get; set; } = "";
    public List<TrainingTest> Tests { get; set; } = [];

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The manifest in <paramref name="path"/>, or null when the file is missing or is not a manifest. Null lists and strings become empty ones.</summary>
    public static TrainingManifest? Read(string path)
    {
        try
        {
            var m = JsonSerializer.Deserialize<TrainingManifest>(File.ReadAllText(path), Options);
            m?.Normalize();
            return m;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }

    /// <summary>Writes the manifest through a temp file and moves it over <paramref name="path"/> (the folder is created).</summary>
    public void Write(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, "." + Path.GetFileName(path) + ".tmp");
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options), new System.Text.UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    void Normalize()
    {
        Title ??= ""; CreatedUtc ??= ""; RetraceVersion ??= "";
        Tests = (Tests ?? []).Where(t => t != null).ToList();
        foreach (var t in Tests)
        {
            t.Name ??= ""; t.Folder ??= "";
            t.Steps = (t.Steps ?? []).Where(s => s != null).ToList();
            foreach (var s in t.Steps)
            {
                s.Kind ??= ""; s.Title ??= "";
                s.Instructions = (s.Instructions ?? []).Where(i => i != null).ToList();
                s.Pictures = (s.Pictures ?? []).Where(p => p != null).ToList();
                foreach (var p in s.Pictures)
                {
                    p.File ??= "";
                    p.Callouts = (p.Callouts ?? []).Where(c => c != null).ToList();
                    foreach (var c in p.Callouts) c.Text ??= "";
                }
                if (s.Checkpoint != null)
                {
                    s.Checkpoint.Note ??= "";
                    s.Checkpoint.Differences = (s.Checkpoint.Differences ?? []).Where(d => d != null).ToList();
                }
            }
        }
    }
}

/// <summary>One replayed test: a chapter of the document.</summary>
public sealed class TrainingTest
{
    public string Name { get; set; } = "";
    /// <summary>The test folder it was replayed from.</summary>
    public string Folder { get; set; } = "";
    /// <summary>The replay result ("PASS", "FAIL", "STOPPED"...), or null when not known.</summary>
    public string? Verdict { get; set; }
    public string? Message { get; set; }
    public List<TrainingStep> Steps { get; set; } = [];
}

public sealed class TrainingStep
{
    /// <summary>1-based within the test.</summary>
    public int Number { get; set; }
    /// <summary>ribbon | command | lisp | dialog | palette | mark | open | tab | other</summary>
    public string Kind { get; set; } = "other";
    /// <summary>The recorded step title.</summary>
    public string Title { get; set; } = "";
    /// <summary>Sentences for the reader, in order ("Click Create Block on the Insert tab.").</summary>
    public List<string> Instructions { get; set; } = [];
    public List<TrainingPicture> Pictures { get; set; } = [];
    /// <summary>Set for kind "mark".</summary>
    public TrainingCheckpoint? Checkpoint { get; set; }
}

public sealed class TrainingPicture
{
    /// <summary>Relative to the manifest's folder, `/` separators.</summary>
    public string File { get; set; } = "";
    public string? Caption { get; set; }
    public List<TrainingCallout> Callouts { get; set; } = [];
}

/// <summary>A legend line under a picture: number 1, text "Name: KitBlock".</summary>
public sealed class TrainingCallout
{
    public int Number { get; set; }
    public string Text { get; set; } = "";
}

public sealed class TrainingCheckpoint
{
    public int Index { get; set; }
    public string Note { get; set; } = "";
    /// <summary>true = PASS, false = FAIL, null = not verified.</summary>
    public bool? Pass { get; set; }
    public List<string> Differences { get; set; } = [];
}
