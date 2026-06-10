using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using PiiRemover.Core.Models;

namespace PiiRemover.Core.Engines;

/// <summary>
/// Redacts the VALUE that follows a known label in the document.
/// The label itself is preserved; only the text after it is replaced.
///
/// Pattern format:
///   "Patient Name:"        → redacts everything after the label to end of line
///   "Patient Name:|2"      → redacts the next 2 words after the label
///   "Date of Birth:|1"     → redacts the next 1 word
///   "Referring Doctor:|3"  → redacts the next 3 words
///
/// Matching is case-insensitive and works across any language.
/// </summary>
public sealed class AfterLabelEngine : IPatternEngine
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new();

    public PatternType SupportedType => PatternType.AfterLabel;

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
            // Group 1 is the value to redact (everything after the label + whitespace)
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

    // ── Regex builder ────────────────────────────────────────────────────────

    private static Regex BuildRegex(string rawPattern)
    {
        var (label, wordCount) = Parse(rawPattern);
        var escaped            = Regex.Escape(label);
        var valueGroup         = wordCount <= 0
            ? @"([^\r\n]+)"                                         // to end of line (greedy, any line-ending style)
            : BuildWordGroup(wordCount);                             // N words

        // (?im) — case-insensitive + multiline (^ and $ match line start/end)
        return new Regex(
            $@"(?im){escaped}[ \t]*{valueGroup}",
            RegexOptions.Compiled,
            TimeSpan.FromSeconds(2));
    }

    private static string BuildWordGroup(int n)
    {
        // Matches exactly n words separated by horizontal whitespace.
        // Last word is optional beyond the first, to handle documents where the
        // line may end before n words are found.
        var sb = new StringBuilder(@"(\S+");
        for (int i = 1; i < n; i++)
            sb.Append(@"(?:[ \t]+\S+)?");
        sb.Append(')');
        return sb.ToString();
    }

    private static (string label, int words) Parse(string rawPattern)
    {
        var parts = rawPattern.Split('|', 2);
        int words = 0;
        if (parts.Length > 1) int.TryParse(parts[1].Trim(), out words);
        return (parts[0].Trim(), words);
    }
}
