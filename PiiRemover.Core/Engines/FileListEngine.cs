using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using PiiRemover.Core.Models;

namespace PiiRemover.Core.Engines;

/// <summary>
/// Matches any term from a newline/pipe-separated list stored in the pattern value.
///
/// Algorithm: word-tokenizer + HashSet lookup (O(1) per word), NOT regex alternation.
/// A regex with 100 000+ alternations degrades exponentially; this approach stays
/// constant regardless of term count — only text length matters.
///
/// Multi-word terms ("Tel Aviv") are matched by sliding a window of 1..maxWords
/// consecutive tokens and doing a normalised HashSet lookup on each span.
/// </summary>
public sealed class FileListEngine : IPatternEngine
{
    // Cache compiled TermIndex per pattern DB row id
    private static readonly ConcurrentDictionary<int, TermIndex> IndexCache = new();

    public PatternType SupportedType => PatternType.FileList;

    public IEnumerable<RedactMatch> FindMatches(string text, PiiPattern pattern, string replacement)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(pattern.Pattern))
            yield break;

        var index = IndexCache.GetOrAdd(pattern.Id, _ => new TermIndex(
            pattern.Pattern.Split(new[] { '\n', '\r', '|' }, StringSplitOptions.RemoveEmptyEntries)
                           .Select(t => t.Trim())
                           .Where(t => t.Length > 0)));

        foreach (var m in index.FindMatches(text))
        {
            yield return new RedactMatch
            {
                StartIndex  = m.Start,
                Length      = m.Length,
                Replacement = replacement,
                MatchedText = m.Matched
            };
        }
    }

    public static void InvalidateCache(int patternId) => IndexCache.TryRemove(patternId, out _);
    public static void InvalidateAll()               => IndexCache.Clear();

    // ── File parsing helpers (used by admin upload handler) ──────────────────

    /// <summary>
    /// Parse any supported file format into a deduplicated list of terms.
    ///
    /// Supported formats:
    ///  • Single-column TXT/CSV  — one term per line
    ///  • Fixed-width dual-column (NAMES_TO_REDACT.DAT style) — splits each line
    ///    at 2+ consecutive spaces, extracts both Hebrew and English tokens
    ///  • Pipe-separated — splits on |
    /// </summary>
    public static IReadOnlyList<string> ParseFile(string fileContent)
    {
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in fileContent.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#')) continue;

            if (Regex.IsMatch(line, @"\s{2,}"))
            {
                // Dual-column fixed-width
                var cols = Regex.Split(line, @"\s{2,}")
                                .Select(c => c.Trim())
                                .Where(c => c.Length > 1);
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

// ═══════════════════════════════════════════════════════════════════════════
// TermIndex — the fast lookup structure
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Immutable lookup structure built once per pattern, cached in memory.
///
/// Single-word terms  → HashSet lookup,  O(1)
/// Multi-word terms   → sliding window of N consecutive tokens, O(text × maxWords)
///   where maxWords is capped at 6 so performance is still O(text)
/// </summary>
internal sealed class TermIndex
{
    // Normalised term → original (for MatchedText)
    private readonly Dictionary<string, string> _terms;
    private readonly int _maxWordCount;

    private static readonly Regex NonWordRun = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);

    public TermIndex(IEnumerable<string> rawTerms)
    {
        // Normalise in parallel for large lists (480k+ terms), then load into Dictionary
        var pairs = rawTerms
            .AsParallel()
            .Select(raw => (norm: Normalize(raw), raw))
            .Where(x => x.norm.Length > 0)
            .ToList();

        _terms = new Dictionary<string, string>(pairs.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (norm, raw) in pairs)
            _terms.TryAdd(norm, raw);

        // Pre-compute max word count so the inner loop has a tight upper bound
        _maxWordCount = _terms.Count == 0
            ? 1
            : Math.Min(6, _terms.Keys.Max(k => k.Split(' ').Length));
    }

    /// <summary>
    /// Walk <paramref name="text"/> token-by-token and yield every match.
    /// Greedy: at each position tries longest window first to avoid
    /// matching "Cohen" inside "Cohen Levi" if "Cohen Levi" is also a term.
    /// </summary>
    public IEnumerable<(int Start, int Length, string Matched)> FindMatches(string text)
    {
        if (_terms.Count == 0) yield break;

        var tokens = Tokenize(text);
        int i = 0;

        while (i < tokens.Count)
        {
            bool found = false;

            int maxWc = Math.Min(_maxWordCount, tokens.Count - i);
            for (int wc = maxWc; wc >= 1; wc--)
            {
                int spanStart = tokens[i].Start;
                int spanEnd   = tokens[i + wc - 1].Start + tokens[i + wc - 1].Length;
                var span      = text.AsSpan(spanStart, spanEnd - spanStart);
                var norm      = Normalize(span.ToString());

                if (_terms.TryGetValue(norm, out var original))
                {
                    yield return (spanStart, spanEnd - spanStart, original);
                    i    += wc;
                    found = true;
                    break;
                }
            }

            if (!found) i++;
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// Collapse any run of non-letter/digit chars to a single space, lowercase.
    private static string Normalize(string s)
    {
        var trimmed = s.Trim();
        if (trimmed.Length == 0) return string.Empty;
        return NonWordRun.Replace(trimmed, " ").ToLowerInvariant().Trim();
    }

    /// Extract word tokens (letter/digit runs, hyphens included) with their
    /// start positions. Runs of O(text length).
    private static List<(int Start, int Length)> Tokenize(string text)
    {
        var result = new List<(int, int)>(text.Length / 6 + 16);
        int i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && !IsWordChar(text[i])) i++;
            if (i >= text.Length) break;
            int s = i;
            while (i < text.Length && IsWordChar(text[i])) i++;
            result.Add((s, i - s));
        }
        return result;
    }

    /// Letters, digits, and hyphens (for compound names like Abu-Ali, Bat-Sheva)
    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '-';
}
