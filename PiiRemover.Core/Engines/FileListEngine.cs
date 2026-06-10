using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using PiiRemover.Core.Models;

namespace PiiRemover.Core.Engines;

/// <summary>
/// Matches any term from a newline-separated list stored in the pattern value.
/// Designed for large name/keyword lists uploaded from external files.
///
/// Matching is case-insensitive and whole-word aware: "Cohen" will NOT match
/// the substring inside "Cohenberg", but WILL match "Cohen," or "COHEN".
///
/// For performance the term list is parsed and cached per pattern ID.
/// With FieldsCache the entire field catalog is already held in memory —
/// FileListEngine adds a second-level Regex-per-term cache so the hot path
/// is a single pre-compiled AhoCorasick-like alternation regex, not a loop
/// over thousands of IndexOf calls.
/// </summary>
public sealed class FileListEngine : IPatternEngine
{
    // Cache key = pattern.Id (unique per DB row) so updating the file list
    // invalidates automatically when FieldsCache is cleared.
    private static readonly ConcurrentDictionary<int, Regex> RegexCache = new();

    public PatternType SupportedType => PatternType.FileList;

    public IEnumerable<RedactMatch> FindMatches(string text, PiiPattern pattern, string replacement)
    {
        if (string.IsNullOrWhiteSpace(pattern.Pattern)) yield break;

        if (!RegexCache.TryGetValue(pattern.Id, out var regex))
        {
            var built = BuildRegex(pattern.Pattern);
            if (built is null) yield break;
            regex = RegexCache.GetOrAdd(pattern.Id, built);
        }
        if (regex is null) yield break;

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

    /// <summary>
    /// Invalidate the compiled regex for a specific pattern (call after update).
    /// FieldsCache.Invalidate() already evicts the data layer; this evicts the
    /// compiled regex so the next call rebuilds from the fresh pattern string.
    /// </summary>
    public static void InvalidateCache(int patternId) =>
        RegexCache.TryRemove(patternId, out _);

    /// <summary>Invalidate all cached regexes (e.g. on full catalog reload).</summary>
    public static void InvalidateAll() => RegexCache.Clear();

    // ── Internals ─────────────────────────────────────────────────────────────

    private static Regex? BuildRegex(string patternValue)
    {
        // Split by newline (primary delimiter); also accept pipe for compatibility
        var terms = patternValue
            .Split(new[] { '\n', '\r', '|' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(t => t.Length) // longer terms first so "ABU AMOUNA" wins over "ABU"
            .ToList();

        if (terms.Count == 0) return null;

        // Build a single alternation regex: \b(term1|term2|…)\b
        // This is compiled once and is far faster than looping over IndexOf per term.
        var escaped   = terms.Select(Regex.Escape);
        var alternation = string.Join('|', escaped);

        return new Regex(
            $@"\b(?:{alternation})\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(5));
    }

    // ── File parsing helpers (used by admin upload handler) ──────────────────

    /// <summary>
    /// Parse any supported file format into a list of terms to match.
    ///
    /// Supported formats:
    ///  • Single-column TXT/CSV  — one term per line
    ///  • Fixed-width dual-column (e.g. NAMES_TO_REDACT.DAT) — splits each line
    ///    at 2+ consecutive spaces and extracts all non-empty tokens from both
    ///    columns, giving both Hebrew and English name variants.
    ///  • Pipe-separated  — splits on | as well
    /// </summary>
    public static IReadOnlyList<string> ParseFile(string fileContent)
    {
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in fileContent.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#')) continue;

            // Dual-column fixed-width detection: line contains 2+ consecutive spaces
            if (Regex.IsMatch(line, @"\s{2,}"))
            {
                // Split on runs of 2+ spaces → get individual column values
                var cols = Regex.Split(line, @"\s{2,}")
                                .Select(c => c.Trim())
                                .Where(c => c.Length > 1); // skip single-char artifacts
                foreach (var col in cols) terms.Add(col);
            }
            else if (line.Contains('|'))
            {
                foreach (var part in line.Split('|'))
                {
                    var t = part.Trim();
                    if (t.Length > 1) terms.Add(t);
                }
            }
            else
            {
                if (line.Length > 1) terms.Add(line);
            }
        }

        return terms.OrderBy(t => t).ToList();
    }

    /// <summary>Serialize a term list back to the newline-separated DB format.</summary>
    public static string Serialize(IEnumerable<string> terms) =>
        string.Join('\n', terms.Where(t => !string.IsNullOrWhiteSpace(t)));
}
