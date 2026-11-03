using System.Text;

namespace Retrace.Core;

/// <summary>The text a person types at a prompt, kept between two prompt results. AutoCAD raises no managed event for the corners and
/// keywords of a built-in command's selection (ERASE W, -ATTEDIT window): only the finished selection arrives, with handles. The recorder
/// therefore keeps what was typed since the last prompt result and stores it with the selection (`typed`), so a replay can type it again
/// (see <see cref="SelectionReplay"/>). Mouse clicks are not typed text: they go to their own field (`clicks`, see <see cref="TypedSelection"/>). Pure logic; the recorder feeds it the WM_CHAR characters.</summary>
public static class TypedInput
{
    public const int MaxLength = 2000;

    /// <summary>The character a key types, for a US keyboard layout (other layouts type other characters for the punctuation keys; the
    /// digits, letters, Enter, Backspace and Space are the same). Null for keys that type nothing.</summary>
    public static char? CharOf(uint vk, bool shift, bool capsLock)
    {
        switch (vk)
        {
            case 0x0D: return '\r';
            case 0x08: return '\b';
            case 0x20: return ' ';
            case >= 0x30 and <= 0x39: return shift ? ")!@#$%^&*("[(int)vk - 0x30] : (char)vk;
            case >= 0x41 and <= 0x5A: return shift ^ capsLock ? (char)vk : char.ToLowerInvariant((char)vk);
            case >= 0x60 and <= 0x69: return (char)('0' + (vk - 0x60));
            case 0x6A: return '*';
            case 0x6B: return '+';
            case 0x6D: return '-';
            case 0x6E: return '.';
            case 0x6F: return '/';
            case 0xBA: return shift ? ':' : ';';
            case 0xBB: return shift ? '+' : '=';
            case 0xBC: return shift ? '<' : ',';
            case 0xBD: return shift ? '_' : '-';
            case 0xBE: return shift ? '>' : '.';
            case 0xBF: return shift ? '?' : '/';
            case 0xC0: return shift ? '~' : '`';
            case 0xDB: return shift ? '{' : '[';
            case 0xDC: return shift ? '|' : '\\';
            case 0xDD: return shift ? '}' : ']';
            case 0xDE: return shift ? '"' : '\'';
        }
        return null;
    }

    /// <summary>Applies one typed character: Enter and Space become a line break (Space is Enter at selection and point prompts), Backspace
    /// removes the last character of the line, other control characters are ignored. Esc is ignored too: it cancels the prompt, and the recorder's own test tools press it to close ribbon key tips, which must not wipe the line.</summary>
    public static void Append(StringBuilder sb, char c)
    {
        if (c is '\r' or ' ') { if (sb.Length < MaxLength) sb.Append('\n'); }
        else if (c == '\b') { if (sb.Length > 0 && sb[^1] != '\n') sb.Length--; }
        else if (c >= ' ' && sb.Length < MaxLength) sb.Append(c);
    }

    /// <summary>The typed text as lines to type again (each followed by Enter); the final line break is not a line of its own.
    /// Null when nothing was typed, or when text follows the last line break (typed, never entered: half a buffer is never replayed).</summary>
    public static List<string>? Lines(string? typed)
    {
        if (string.IsNullOrEmpty(typed) || !typed.EndsWith('\n')) return null;
        var parts = typed.Split('\n').ToList();
        parts.RemoveAt(parts.Count - 1);
        return parts.Count == 0 ? null : parts;
    }

    /// <summary>true for a line that gives a point: "0,0", "-1,5.5", "@3,4", "@5&lt;30", "#1,2".</summary>
    public static bool IsPointLine(string line)
    {
        var t = line.Trim().TrimStart('@', '#', '*');
        return t.Length > 0 && (t.Contains(',') || t.Contains('<')) && t.All(ch => char.IsAsciiDigit(ch) || ch is '+' or '-' or '.' or ',' or '<' or 'e' or 'E');
    }

    /// <summary>Whether the typed text alone makes the whole selection: it holds something other than Enters, and every point the selection
    /// used (<paramref name="pointCount"/>) has a point line in the text. Keys alone: a mouse selection's keys are not complete (replaying them would leave out
    /// its clicks, a bare Enter), so <see cref="TypedSelection"/> checks the keys with the point count of a typed selection and the text with the clicks written in as point lines for a clicked one.</summary>
    public static bool MakesSelection(string? typed, int pointCount)
    {
        var lines = Lines(typed);
        return lines != null && lines.Any(l => l.Length > 0) && lines.Count(IsPointLine) == pointCount;
    }
}
