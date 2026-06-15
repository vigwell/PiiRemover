using System.Diagnostics;
using System.Text;
using PiiRemover.Core.Models;

namespace PiiRemover.Core.Engines;

public class RedactionOrchestrator
{
    private readonly Dictionary<PatternType, IPatternEngine> _engines;

    public RedactionOrchestrator(IEnumerable<IPatternEngine> engines)
    {
        _engines = engines.ToDictionary(e => e.SupportedType);
    }

    public RedactResult Redact(string text, IEnumerable<PiiField> fields)
    {
        var sw         = Stopwatch.StartNew();
        var activeFields = fields.Where(f => f.IsActive).ToList();

        // ── Step 1: collect PRESERVE (whitelist) regions ──────────────────────
        // These are spans that must never be touched, regardless of other rules.
        var preserveRegions = new List<(int Start, int End)>();
        foreach (var field in activeFields.Where(f => f.IsPreserve))
        {
            foreach (var pattern in field.Patterns)
            {
                if (!_engines.TryGetValue(pattern.PatternType, out var engine)) continue;
                foreach (var hit in engine.FindMatches(text, pattern, string.Empty))
                    preserveRegions.Add((hit.StartIndex, hit.StartIndex + hit.Length));
            }
        }

        // ── Step 2: collect REDACT candidates from normal fields ──────────────
        var allMatches = new List<RedactMatch>();
        foreach (var field in activeFields.Where(f => !f.IsPreserve))
        {
            foreach (var pattern in field.Patterns.OrderByDescending(p => p.Priority))
            {
                if (!_engines.TryGetValue(pattern.PatternType, out var engine)) continue;

                // Apply scope: slice the text to the region defined by ScopeStart / ScopeEnd,
                // then offset match positions back to the original document coordinates.
                var (scopedText, scopeOffset) = ApplyScope(text, pattern);

                foreach (var hit in engine.FindMatches(scopedText, pattern, field.ReplaceWith))
                {
                    hit.StartIndex += scopeOffset;
                    hit.FieldName   = field.FieldName;
                    allMatches.Add(hit);
                }
            }
        }

        // ── Step 3: remove candidates that overlap any preserve region ─────────
        if (preserveRegions.Count > 0)
        {
            allMatches = allMatches
                .Where(m => !preserveRegions.Any(p =>
                    m.StartIndex < p.End && (m.StartIndex + m.Length) > p.Start))
                .ToList();
        }

        // ── Step 4: deduplicate overlapping matches ───────────────────────────
        var deduped = DeduplicateOverlaps(allMatches);

        // ── Step 5: apply right-to-left so indices remain valid ───────────────
        // When the replacement template is a single character (e.g. "█") we fill
        // each position individually, preserving structural whitespace (\n, \r, \t)
        // at their exact positions so document line breaks and indentation survive.
        //   "David Cohen\n"  →  "███████████\n"   (not "████████████")
        //   "0501234567\r\n" →  "██████████\r\n"
        var sb = new StringBuilder(text);
        foreach (var match in deduped.OrderByDescending(m => m.StartIndex))
        {
            if (match.StartIndex + match.Length > sb.Length) continue;
            var replacement = match.Replacement.Length == 1
                ? BuildStructureAwareReplacement(match.MatchedText, match.Replacement[0])
                : match.Replacement;
            // Keep the stored Replacement in sync so the Tester UI shows the right value
            match.Replacement = replacement;
            sb.Remove(match.StartIndex, match.Length);
            sb.Insert(match.StartIndex, replacement);
        }

        sw.Stop();
        return new RedactResult
        {
            RedactedText = sb.ToString(),
            Matches = deduped,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    /// <summary>
    /// Applies ScopeStart / ScopeEnd markers to slice the document text to the region
    /// where this pattern should fire.
    ///
    /// ScopeStart — newline-separated plain-text markers (case-insensitive).
    ///   Of all markers that appear in the text, pick the one whose match starts EARLIEST;
    ///   the scope begins at the END of that match (i.e. after the marker itself).
    ///
    /// ScopeEnd — newline-separated plain-text markers (case-insensitive).
    ///   Of all markers that appear in the text, pick the one whose match starts EARLIEST;
    ///   the scope ends at the START of that match (i.e. the marker itself is not redacted).
    ///
    /// Returns (slicedText, offsetIntoOriginal).
    /// </summary>
    private static (string ScopedText, int Offset) ApplyScope(string text, PiiPattern pattern)
    {
        int start = 0;
        int end   = text.Length;

        if (!string.IsNullOrWhiteSpace(pattern.ScopeStart))
        {
            var markers = pattern.ScopeStart
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            // Find the earliest-starting marker; scope begins at the end of it
            int bestPos = -1, bestEnd = -1;
            foreach (var m in markers)
            {
                var idx = text.IndexOf(m, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0 && (bestPos < 0 || idx < bestPos))
                {
                    bestPos = idx;
                    bestEnd = idx + m.Length;
                }
            }
            if (bestEnd >= 0) start = bestEnd;
        }

        // ScopeEnd: try text markers first; fall back to ScopeEndPosition if none match.
        bool markerFound = false;
        if (!string.IsNullOrWhiteSpace(pattern.ScopeEnd))
        {
            var markers = pattern.ScopeEnd
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            // Find the earliest-starting marker within [start..text.Length]; scope ends before it
            int bestPos = -1;
            foreach (var m in markers)
            {
                var idx = text.IndexOf(m, start, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0 && (bestPos < 0 || idx < bestPos))
                    bestPos = idx;
            }
            if (bestPos >= 0)
            {
                end = bestPos;
                markerFound = true;
            }
        }

        // Position fallback: if no marker matched AND ScopeEndPosition > 0, cap the scope there.
        if (!markerFound && pattern.ScopeEndPosition > 0)
            end = Math.Min(end, start + pattern.ScopeEndPosition);

        if (start == 0 && end == text.Length)
            return (text, 0); // no scope applied

        start = Math.Max(0, Math.Min(start, text.Length));
        end   = Math.Max(start, Math.Min(end, text.Length));
        return (text.Substring(start, end - start), start);
    }

    /// <summary>
    /// Builds a replacement string for a single-char replacement token.
    /// Structural whitespace characters (\r, \n, \t) are copied from the original
    /// matched text verbatim; every other character is replaced by <paramref name="replacementChar"/>.
    /// This guarantees that line endings and indentation at the boundary of a match
    /// survive redaction unchanged, keeping the document structure intact.
    /// </summary>
    private static string BuildStructureAwareReplacement(string matchedText, char replacementChar)
    {
        if (string.IsNullOrEmpty(matchedText))
            return string.Empty;

        var chars = new char[matchedText.Length];
        for (int i = 0; i < matchedText.Length; i++)
        {
            var c = matchedText[i];
            // Preserve line endings and tabs; replace everything else
            chars[i] = c is '\n' or '\r' or '\t' ? c : replacementChar;
        }
        return new string(chars);
    }

    private static List<RedactMatch> DeduplicateOverlaps(List<RedactMatch> matches)
    {
        // Sort by start position; within the same start prefer the longest match.
        // When a later (shorter) match overlaps an already-accepted match it is dropped —
        // the longer match always wins, even if the shorter one started slightly later.
        var sorted = matches.OrderBy(m => m.StartIndex).ThenByDescending(m => m.Length).ToList();
        var result = new List<RedactMatch>();
        int lastEnd = -1;

        foreach (var m in sorted)
        {
            // Skip any match that overlaps (even partially) with an already-accepted match
            if (m.StartIndex < lastEnd) continue;

            result.Add(m);
            lastEnd = m.StartIndex + m.Length;
        }
        return result;
    }
}
