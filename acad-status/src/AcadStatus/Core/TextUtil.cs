using System;
using System.Text;

namespace AcadStatus.Core;

/// <summary>The one text normalizer: every text that reaches a file, the pane or a window is made one line first, never cut through a
/// surrogate pair (an emoji), and never holds a lone surrogate (which <c>Utf8JsonWriter</c> refuses to write: the file would stop being
/// written and every reader would see the writer as busy).</summary>
public static class TextUtil
{
    public const char Replacement = '�';

    /// <summary>The text with every lone surrogate replaced by U+FFFD; the same string when there is none.</summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        int bad = IndexOfLoneSurrogate(text, 0);
        if (bad < 0) return text;
        var sb = new StringBuilder(text.Length);
        int start = 0;
        while (bad >= 0)
        {
            sb.Append(text, start, bad - start).Append(Replacement);
            start = bad + 1;
            bad = IndexOfLoneSurrogate(text, start);
        }
        return sb.Append(text, start, text.Length - start).ToString();
    }

    static int IndexOfLoneSurrogate(string s, int from)
    {
        for (int i = from; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]))
            {
                if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { i++; continue; }
                return i;
            }
            if (char.IsLowSurrogate(s[i])) return i;
        }
        return -1;
    }

    /// <summary>The first <paramref name="max"/> characters, or one fewer when the cut would split a surrogate pair. Not trimmed.</summary>
    public static string SafeTruncate(string text, int max)
    {
        if (max <= 0) return "";
        if (text.Length <= max) return text;
        int n = max;
        if (char.IsHighSurrogate(text[n - 1]) && char.IsLowSurrogate(text[n])) n--;
        return text[..n];
    }

    /// <summary>Trimmed, with every run of white space and control characters (line breaks, tabs) made one space, and lone surrogates replaced.</summary>
    public static string OneLine(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = Sanitize(text);
        var sb = new StringBuilder(s.Length);
        bool space = false;
        foreach (var c in s.Trim())
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)) { space = true; continue; }
            if (space) sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>One line of at most <paramref name="max"/> characters; what does not fit is cut off.</summary>
    public static string Clean(string? text, int max)
    {
        if (max <= 0) return "";
        return SafeTruncate(OneLine(text), max).TrimEnd();
    }

    /// <summary>One line of at most <paramref name="max"/> characters; a longer text ends in "…".</summary>
    public static string Cap(string? text, int max)
    {
        if (max <= 0) return "";
        var s = OneLine(text);
        if (s.Length <= max) return s;
        return max == 1 ? "…" : SafeTruncate(s, max - 1).TrimEnd() + "…";
    }
}

/// <summary>An RGB colour, without System.Drawing (Core stays free of it).</summary>
public readonly record struct Rgb(byte R, byte G, byte B);

/// <summary>The one palette of the indicator: the dot on the status bar and the dots in the details window use the same colours.</summary>
public static class TonePalette
{
    public static Rgb Of(Tone tone) => tone switch
    {
        Tone.Green => new Rgb(46, 170, 70),
        Tone.Blue => new Rgb(40, 110, 220),
        Tone.Amber => new Rgb(235, 160, 10),
        Tone.Red => new Rgb(215, 45, 45),
        _ => new Rgb(140, 140, 140),
    };
}
