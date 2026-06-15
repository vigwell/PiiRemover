using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using PiiRemover.Core.Models;

namespace PiiRemover.Core.Engines;

public class RegexPatternEngine : IPatternEngine
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new();

    public PatternType SupportedType => PatternType.Regex;

    public IEnumerable<RedactMatch> FindMatches(string text, PiiPattern pattern, string replacement)
    {
        var regex = Cache.GetOrAdd(pattern.Pattern, p =>
            new Regex(p, RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)));

        foreach (Match m in regex.Matches(text))
        {
            yield return new RedactMatch
            {
                StartIndex = m.Index,
                Length = m.Length,
                FieldName = string.Empty,
                Replacement = replacement,
                MatchedText = m.Value
            };
        }
    }
}
