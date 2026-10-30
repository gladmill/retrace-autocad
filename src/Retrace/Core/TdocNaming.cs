using Retrace.Engine.Recordings;

namespace Retrace.Core;

// NO AutoCAD references in this file (linked into the engine test project). Where a Tdoc run puts its files:
//   <docs>\training\<stamp>_<safe title>\tdoc.json, pictures\t<test>-s<step>-<n>.png
//   <docs>\training\<stamp>_<safe title>.docx
public static class TdocNaming
{
    public const string TrainingDir = "training";
    public const string PicturesDir = "pictures";
    public const string ManifestFile = "tdoc.json";
    const int MaxTitle = 60;

    static readonly string[] Reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    /// <summary>The title as a file name part: characters Windows forbids become '-', at most 60 characters, no trailing dot or space, a device
    /// name (CON, NUL...) gets a '_' in front, "training" when nothing is left.</summary>
    public static string SafeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Trim('.', ' ').Length == 0) return "training";
        var s = TestFolders.CleanName(title);
        if (s.Length > MaxTitle) s = s[..MaxTitle].TrimEnd('.', ' ');
        if (s.Length == 0) return "training";
        var stem = s.Contains('.') ? s[..s.IndexOf('.')] : s;
        return Reserved.Contains(stem.Trim(), StringComparer.OrdinalIgnoreCase) ? "_" + s : s;
    }

    /// <summary>`2026-10-30_14-05-09_Block-training`: the folder, and the Word file without its extension.</summary>
    public static string FolderName(DateTime started, string? title) => $"{TestFolders.Stamp(started)}_{SafeTitle(title)}";

    /// <summary>The Word file that belongs to a training folder: beside it, same name, `.docx`.</summary>
    public static string DocxPath(string folder) => folder.TrimEnd('\\', '/') + ".docx";

    /// <summary>`t2-s5-1.png`: test 2, step 5, first picture of that step (all 1-based).</summary>
    public static string PictureName(int test, int step, int n) => $"t{test}-s{step}-{n}.png";

    /// <summary>The picture's path in tdoc.json: relative to the manifest, '/' separators.</summary>
    public static string PictureRel(int test, int step, int n) => PicturesDir + "/" + PictureName(test, step, n);
}
