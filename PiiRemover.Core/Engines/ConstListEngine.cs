using System.Collections.Concurrent;
using PiiRemover.Core.Models;

namespace PiiRemover.Core.Engines;

/// <summary>
/// Matches any of the pipe-separated terms from the pattern, case-insensitively.
///
/// Uses the same TermIndex as FileListEngine — O(text) regardless of term count.
/// The previous IndexOf-per-term approach was O(terms × text): 1 000 names on a
/// 10-page document meant tens of millions of char comparisons per request.
///
/// Cache is keyed by pattern Id — call InvalidateCache/InvalidateAll after saves.
/// </summary>
public class ConstListEngine : IPatternEngine
{
    private static readonly ConcurrentDictionary<int, TermIndex> IndexCache = new();

    public PatternType SupportedType => PatternType.ConstList;

    public IEnumerable<RedactMatch> FindMatches(string text, PiiPattern pattern, string replacement)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(pattern.Pattern))
            yield break;

        var index = IndexCache.GetOrAdd(pattern.Id, _ => new TermIndex(
            pattern.Pattern
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(t => t.Length > 0)));

        foreach (var m in index.FindMatches(text))
            yield return new RedactMatch
            {
                StartIndex  = m.Start,
                Length      = m.Length,
                FieldName   = string.Empty,
                Replacement = replacement,
                MatchedText = m.Matched
            };
    }

    public static void InvalidateCache(int patternId) => IndexCache.TryRemove(patternId, out _);
    public static void InvalidateAll()               => IndexCache.Clear();
}
