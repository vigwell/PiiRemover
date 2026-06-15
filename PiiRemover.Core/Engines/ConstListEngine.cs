using PiiRemover.Core.Models;

namespace PiiRemover.Core.Engines;

/// <summary>
/// Matches any of the pipe-separated terms from the pattern, case-insensitively.
///
/// Matching is WHOLE-WORD: the term must be surrounded by non-letter/non-digit
/// characters (spaces, punctuation, newlines, start/end of text).
/// Unicode-aware — correctly handles Hebrew, Arabic, and other non-ASCII scripts.
///
/// Examples:
///   Term "Vigen"  matches "Vigen Shah"     → redacted
///   Term "Vigen"  does NOT match "Vigens"  → skipped (extra letter follows)
///   Term "כהן"    matches "כהן לוי"       → redacted
///   Term "כהן"    does NOT match "כהנבך"  → skipped
/// </summary>
public class ConstListEngine : IPatternEngine
{
    public PatternType SupportedType => PatternType.ConstList;

    public IEnumerable<RedactMatch> FindMatches(string text, PiiPattern pattern, string replacement)
    {
        var terms = pattern.Pattern.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var term in terms)
        {
            if (term.Length == 0) continue;

            int idx = 0;
            while ((idx = text.IndexOf(term, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                if (IsWordBoundary(text, idx, term.Length))
                {
                    yield return new RedactMatch
                    {
                        StartIndex  = idx,
                        Length      = term.Length,
                        FieldName   = string.Empty,
                        Replacement = replacement,
                        MatchedText = text.Substring(idx, term.Length)
                    };
                }
                idx += term.Length;
            }
        }
    }

    /// <summary>
    /// Returns true when the substring at [index, index+length) is surrounded by
    /// non-word characters (or start / end of string).
    /// Uses char.IsLetterOrDigit which is Unicode-aware (works for Hebrew, Arabic, CJK, etc.)
    /// </summary>
    private static bool IsWordBoundary(string text, int index, int length)
    {
        bool startOk = index == 0
            || !char.IsLetterOrDigit(text, index - 1);

        bool endOk = (index + length) >= text.Length
            || !char.IsLetterOrDigit(text, index + length);

        return startOk && endOk;
    }
}
