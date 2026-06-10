using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using PiiRemover.Core.Models;

namespace PiiRemover.Core.Engines;

/// <summary>
/// If the sub-pattern is found ANYWHERE on a line, the entire line is replaced
/// (including its line terminator, so setting ReplaceWith = "" fully deletes the line).
///
/// Algorithm:
///   1. Run the sub-pattern regex to find every match position.
///   2. For each hit, walk the raw text backwards to find the start of that line,
///      and forward to find the end (including the line terminator).
///   3. Return the whole-line span as the redact match.
///   Lines that contain multiple hits are only returned once (deduped by line-start).
///
/// Pattern value: a .NET regex sub-expression (case-insensitive).
///   Plain text works fine for simple keywords ("Patient:").
///   Full regex syntax is supported for advanced cases ("\d{9}", "N/A|Unknown").
///
/// ReplaceWith on the field:
///   ""            → line is deleted entirely
///   "████"        → whole line replaced with one redaction block
///   "[REDACTED]"  → custom label
/// </summary>
public sealed class WholeLineEngine : IPatternEngine
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new();

    public PatternType SupportedType => PatternType.WholeLine;

    public IEnumerable<RedactMatch> FindMatches(string text, PiiPattern pattern, string replacement)
    {
        Regex subRegex;
        try
        {
            subRegex = Cache.GetOrAdd(pattern.Pattern, p =>
                new Regex(p,
                    RegexOptions.Compiled | RegexOptions.IgnoreCase,
                    TimeSpan.FromSeconds(2)));
        }
        catch (ArgumentException)
        {
            yield break; // invalid regex — silently skip
        }

        // Track which line-starts we've already emitted so we don't double-report
        // a line when the sub-pattern matches it more than once.
        var emitted = new HashSet<int>();

        foreach (Match m in subRegex.Matches(text))
        {
            // ── 1. Find start of line ────────────────────────────────────────
            // Walk backwards until we hit a \n, a \r, or the beginning of text.
            int lineStart = m.Index;
            while (lineStart > 0
                   && text[lineStart - 1] != '\n'
                   && text[lineStart - 1] != '\r')
            {
                lineStart--;
            }

            if (!emitted.Add(lineStart))
                continue; // already emitted this line

            // ── 2. Find end of line content (before any terminator) ──────────
            int lineEnd = lineStart;
            while (lineEnd < text.Length
                   && text[lineEnd] != '\r'
                   && text[lineEnd] != '\n')
            {
                lineEnd++;
            }

            // ── 3. Consume the line terminator (\n, \r, or \r\n) ────────────
            if (lineEnd < text.Length)
            {
                if (text[lineEnd] == '\r') lineEnd++;           // consume \r
                if (lineEnd < text.Length && text[lineEnd] == '\n') lineEnd++; // consume \n
            }

            yield return new RedactMatch
            {
                StartIndex  = lineStart,
                Length      = lineEnd - lineStart,
                Replacement = replacement,
                MatchedText = text.Substring(lineStart, lineEnd - lineStart)
            };
        }
    }
}
