using System.Text.RegularExpressions;

namespace Retrace.Engine.Util;

/// <summary>The one match timeout every regex of the engine and the core uses. The engine runs inside AutoCAD, so the process-wide
/// REGEX_DEFAULT_MATCH_TIMEOUT cannot be set: every regex gets this timeout explicitly (through <see cref="Rx"/>).</summary>
public static class RegexTimeout
{
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(5);
}

/// <summary>Regex calls with <see cref="RegexTimeout.Default"/>: the same shapes as the static Regex methods and the constructor.
/// A match that runs out of time throws RegexMatchTimeoutException (a scanner step turns it into a warning, see Pipeline).</summary>
public static class Rx
{
    static readonly TimeSpan T = RegexTimeout.Default;

    public static Regex Create(string pattern, RegexOptions options = RegexOptions.None) => new(pattern, options, T);
    /// <summary>For a pattern that needs a shorter (or longer) timeout than the default.</summary>
    public static Regex Create(string pattern, RegexOptions options, TimeSpan timeout) => new(pattern, options, timeout);

    public static Match Match(string input, string pattern, RegexOptions options = RegexOptions.None) => Regex.Match(input, pattern, options, T);
    public static MatchCollection Matches(string input, string pattern, RegexOptions options = RegexOptions.None) => Regex.Matches(input, pattern, options, T);
    public static bool IsMatch(string input, string pattern, RegexOptions options = RegexOptions.None) => Regex.IsMatch(input, pattern, options, T);
    public static string Replace(string input, string pattern, string replacement, RegexOptions options = RegexOptions.None) => Regex.Replace(input, pattern, replacement, options, T);
    public static string Replace(string input, string pattern, MatchEvaluator evaluator, RegexOptions options = RegexOptions.None) => Regex.Replace(input, pattern, evaluator, options, T);
    public static string[] Split(string input, string pattern, RegexOptions options = RegexOptions.None) => Regex.Split(input, pattern, options, T);
}
