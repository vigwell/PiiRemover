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
        var sw = Stopwatch.StartNew();
        var allMatches = new List<RedactMatch>();

        foreach (var field in fields.Where(f => f.IsActive))
        {
            foreach (var pattern in field.Patterns.OrderByDescending(p => p.Priority))
            {
                if (!_engines.TryGetValue(pattern.PatternType, out var engine))
                    continue;

                var hits = engine.FindMatches(text, pattern, field.ReplaceWith);
                foreach (var hit in hits)
                {
                    hit.FieldName = field.FieldName;
                    allMatches.Add(hit);
                }
            }
        }

        // Deduplicate overlapping matches, keeping highest-priority (largest span wins)
        var deduped = DeduplicateOverlaps(allMatches);

        // Apply right-to-left so indices remain valid
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
        var sorted = matches.OrderBy(m => m.StartIndex).ThenByDescending(m => m.Length).ToList();
        var result = new List<RedactMatch>();
        int lastEnd = -1;

        foreach (var m in sorted)
        {
            if (m.StartIndex >= lastEnd)
            {
                result.Add(m);
                lastEnd = m.StartIndex + m.Length;
            }
        }
        return result;
    }
}
