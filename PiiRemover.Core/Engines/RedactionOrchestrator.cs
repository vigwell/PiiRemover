using System.Diagnostics;
using System.Text;
using PiiRemover.Core.Models;

namespace PiiRemover.Core.Engines;

public class RedactionOrchestrator
{
    private readonly Dictionary<PatternType, IPatternEngine> _engines;
    private readonly IAiService? _ai;

    public RedactionOrchestrator(IEnumerable<IPatternEngine> engines, IAiService? ai = null)
    {
        _engines = engines.ToDictionary(e => e.SupportedType);
        _ai      = ai;
    }

    public RedactResult Redact(string text, IEnumerable<PiiField> fields)
    {
        var sw         = Stopwatch.StartNew();
        var activeFields = fields.Where(f => f.IsActive).ToList();

        // ── Prefetch: batch all LlmPrompt descriptions into one AI call ───────
        // Pre-populates the OllamaService scope so each subsequent FindMatches()
        // call returns instantly instead of making its own HTTP round-trip.
        if (_ai != null)
        {
            var llmDescs = activeFields
                .Where(f => !f.IsPreserve)
                .SelectMany(f => f.Patterns)
                .Where(p => p.PatternType == PatternType.LlmPrompt
                         && !string.IsNullOrWhiteSpace(p.Pattern))
                .Select(p => p.Pattern!)
                .Distinct()
                .ToList();
            if (llmDescs.Count > 0)
                _ai.PrefetchAsync(text, llmDescs).GetAwaiter().GetResult();
        }
        try
        {

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
        var sb = new StringBuilder(text);
        foreach (var match in deduped.OrderByDescending(m => m.StartIndex))
        {
            if (match.StartIndex + match.Length > sb.Length) continue;
            var replacement = match.Replacement.Length == 1
                ? BuildStructureAwareReplacement(match.MatchedText, match.Replacement[0])
                : match.Replacement;
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
        finally
        {
            // Release per-request AI results — PII must not persist in memory after this call.
            _ai?.ClearScope();
        }
    }

    private static (string ScopedText, int Offset) ApplyScope(string text, PiiPattern pattern)
    {
        int start = 0;
        int end   = text.Length;

        if (!string.IsNullOrWhiteSpace(pattern.ScopeStart))
        {
            var markers = pattern.ScopeStart
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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

        bool markerFound = false;
        if (!string.IsNullOrWhiteSpace(pattern.ScopeEnd))
        {
            var markers = pattern.ScopeEnd
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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

        if (!markerFound && pattern.ScopeEndPosition > 0)
            end = Math.Min(end, start + pattern.ScopeEndPosition);

        if (start == 0 && end == text.Length)
            return (text, 0);

        start = Math.Max(0, Math.Min(start, text.Length));
        end   = Math.Max(start, Math.Min(end, text.Length));
        return (text.Substring(start, end - start), start);
    }

    private static string BuildStructureAwareReplacement(string matchedText, char replacementChar)
    {
        if (string.IsNullOrEmpty(matchedText))
            return string.Empty;

        var chars = new char[matchedText.Length];
        for (int i = 0; i < matchedText.Length; i++)
        {
            var c = matchedText[i];
            chars[i] = c is '\n' or '\r' or '\t' ? c : replacementChar;
        }
        return new string(chars);
    }

    private static List<RedactMatch> DeduplicateOverlaps(List<RedactMatch> matches)
    {
        var sorted = matches.OrderBy(m => m.StartIndex).ThenByDescending(m => m.Length).ToList();
        var result = new List<RedactMatch>();
        int lastEnd = -1;

        foreach (var m in sorted)
        {
            if (m.StartIndex < lastEnd) continue;
            result.Add(m);
            lastEnd = m.StartIndex + m.Length;
        }
        return result;
    }
}
