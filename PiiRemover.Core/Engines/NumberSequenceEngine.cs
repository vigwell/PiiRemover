using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using PiiRemover.Core.Models;

namespace PiiRemover.Core.Engines;

/// <summary>
/// Matches standalone sequences of digits of a specified length (no adjacent digits).
/// Useful for ID numbers, bank accounts, phone numbers, dates, etc.
///
/// Pattern format:
///   "9"      → exactly 9 consecutive digits  (Israeli ID number)
///   "7,10"   → between 7 and 10 digits
///   "4,4"    → exactly 4 digits  (PIN)
///
/// Uses negative lookaround to ensure the sequence is NOT embedded in a longer number.
/// E.g., pattern "9" will NOT match the 9-digit substring inside a 13-digit number.
/// </summary>
public sealed class NumberSequenceEngine : IPatternEngine
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new();

    public PatternType SupportedType => PatternType.NumberSequence;

    public IEnumerable<RedactMatch> FindMatches(string text, PiiPattern pattern, string replacement)
    {
        Regex regex;
        try
        {
            regex = Cache.GetOrAdd(pattern.Pattern, BuildRegex);
        }
        catch (Exception)
        {
            yield break;
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

    private static Regex BuildRegex(string rawPattern)
    {
        var parts = rawPattern.Split(',', 2, StringSplitOptions.TrimEntries);
        int min, max;

        if (parts.Length == 1)
        {
            if (!int.TryParse(parts[0], out min) || min < 1)
                throw new ArgumentException($"NumberSequence: expected a positive integer, got '{rawPattern}'");
            max = min;
        }
        else
        {
            if (!int.TryParse(parts[0], out min) || !int.TryParse(parts[1], out max) || min < 1 || max < min)
                throw new ArgumentException($"NumberSequence: expected 'min,max', got '{rawPattern}'");
        }

        // (?<!\d) = not preceded by another digit
        // (?!\d)  = not followed by another digit
        string quantifier = min == max ? $"{{{min}}}" : $"{{{min},{max}}}";
        return new Regex(
            $@"(?<!\d)\d{quantifier}(?!\d)",
            RegexOptions.Compiled,
            TimeSpan.FromSeconds(2));
    }
}
