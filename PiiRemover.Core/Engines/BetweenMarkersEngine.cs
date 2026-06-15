using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using PiiRemover.Core.Models;

namespace PiiRemover.Core.Engines;

/// <summary>
/// Redacts text that appears between two delimiter strings on the same line.
/// The delimiters themselves are preserved.
///
/// Pattern format:   "left_marker | right_marker"
///   (the pipe with optional spaces separates the two markers)
///
/// Examples:
///   "ID: | DOB"       in  "ID: 123456789 DOB: 1990-01-01"
///                         → redacts  "123456789 "
///   "Name: | ,"       in  "Name: Victor Cohen, Age: 45"
///                         → redacts  "Victor Cohen"
///   "&lt;name&gt; | &lt;/name&gt;"   in XML-like content
/// </summary>
public sealed class BetweenMarkersEngine : IPatternEngine
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new();

    public PatternType SupportedType => PatternType.BetweenMarkers;

    public IEnumerable<RedactMatch> FindMatches(string text, PiiPattern pattern, string replacement)
    {
        Regex regex;
        try
        {
            regex = Cache.GetOrAdd(pattern.Pattern, BuildRegex);
        }
        catch (ArgumentException)
        {
            yield break;
        }

        foreach (Match m in regex.Matches(text))
        {
            var grp = m.Groups[1];
            if (!grp.Success || grp.Length == 0) continue;

            yield return new RedactMatch
            {
                StartIndex  = grp.Index,
                Length      = grp.Length,
                Replacement = replacement,
                MatchedText = grp.Value
            };
        }
    }

    private static Regex BuildRegex(string rawPattern)
    {
        // Split on " | " — at least one space on each side of the pipe
        var parts = rawPattern.Split(" | ", 2, StringSplitOptions.None);
        if (parts.Length < 2)
            throw new ArgumentException($"BetweenMarkers pattern must be \"left | right\", got: {rawPattern}");

        var left  = Regex.Escape(parts[0]);
        var right = Regex.Escape(parts[1]);

        // Capture group between the two markers (non-greedy, same line)
        // (?i) = case-insensitive
        return new Regex(
            $@"(?i){left}(.+?){right}",
            RegexOptions.Compiled,
            TimeSpan.FromSeconds(2));
    }
}
