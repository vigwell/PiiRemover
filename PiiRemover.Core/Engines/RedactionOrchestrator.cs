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
                foreach (var hit in engine.FindMatches(text, pattern, field.ReplaceWith))
                {
                    hit.FieldName = field.FieldName;
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
            sb.Remove(match.StartIndex, match.Length);
            sb.Insert(match.StartIndex, match.Replacement);
        }

        sw.Stop();
        return new RedactResult
        {
            RedactedText = sb.ToString(),
            Matches = deduped,
            DurationMs = sw.ElapsedMilliseconds
        };
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
