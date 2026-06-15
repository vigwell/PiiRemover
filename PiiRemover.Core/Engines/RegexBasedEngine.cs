using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using PiiRemover.Core.Models;

namespace PiiRemover.Core.Engines;

/// <summary>
/// Abstract base for engines that compile their domain-specific pattern syntax to a
/// single .NET Regex and then run standard match/yield logic.
/// Each subclass only needs to implement <see cref="ToRegexPattern"/>.
/// Compiled regexes are cached per pattern string (static, app-lifetime).
/// </summary>
public abstract class RegexBasedEngine : IPatternEngine
{
    // Shared static cache — keyed by "{PatternType}:{patternString}" so different
    // engine types never collide even if the raw pattern strings happen to be equal.
    private static readonly ConcurrentDictionary<string, Regex> RegexCache = new();

    public abstract PatternType SupportedType { get; }

    /// <summary>Convert the user-supplied pattern string into a valid .NET regex string.</summary>
    protected abstract string ToRegexPattern(string pattern);

    public IEnumerable<RedactMatch> FindMatches(string text, PiiPattern pattern, string replacement)
    {
        Regex regex;
        try
        {
            var cacheKey = $"{SupportedType}:{pattern.Pattern}";
            regex = RegexCache.GetOrAdd(cacheKey, _ =>
                new Regex(ToRegexPattern(pattern.Pattern),
                    RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline,
                    TimeSpan.FromSeconds(2)));
        }
        catch (ArgumentException)
        {
            yield break; // bad pattern — silently skip rather than crash
        }

        foreach (Match m in regex.Matches(text))
        {
            yield return new RedactMatch
            {
                StartIndex  = m.Index,
                Length      = m.Length,
                Replacement = replacement,
                MatchedText = m.Value
            };
        }
    }
}
