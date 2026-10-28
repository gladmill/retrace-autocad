namespace Retrace.Engine.Util;

/// <summary>AutoLISP text: where a symbol or a string ends. One reading for the LISP scanner and the macro reader.</summary>
internal static class LispText
{
    /// <summary>White space, a parenthesis, a quote or a ; ends a symbol.</summary>
    public static bool IsDelimiter(char c) => char.IsWhiteSpace(c) || c is '(' or ')' or '"' or '\'' or ';';

    /// <summary>Index of the closing quote of the string that opens at <paramref name="open"/> (the end of the text when it never closes).
    /// A backslash escapes the next character: \" and \\.</summary>
    public static int EndOfString(string s, int open)
    {
        for (int i = open + 1; i < s.Length; i++)
        {
            if (s[i] == '\\') i++;
            else if (s[i] == '"') return i;
        }
        return s.Length;
    }

    /// <summary>The text between the quote at <paramref name="open"/> and the one at <paramref name="close"/>, with \" and \\ unescaped.</summary>
    public static string Unquote(string s, int open, int close) =>
        open + 1 >= close ? "" : s[(open + 1)..close].Replace("\\\"", "\"").Replace("\\\\", "\\");
}
