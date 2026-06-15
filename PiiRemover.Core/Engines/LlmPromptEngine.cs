using PiiRemover.Core.Models;

namespace PiiRemover.Core.Engines;

/// <summary>
/// Pattern engine that delegates entity extraction to a locally-running AI model via OllamaService.
/// Pattern.Pattern = natural-language description of what to extract, e.g. "patient full name".
/// Returns empty gracefully when AI is disabled or unreachable.
/// </summary>
public class LlmPromptEngine : IPatternEngine
{
    private readonly IAiService _ai;

    public LlmPromptEngine(IAiService ai) => _ai = ai;

    public PatternType SupportedType => PatternType.LlmPrompt;

    public IEnumerable<RedactMatch> FindMatches(string text, PiiPattern pattern, string replacement)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(pattern.Pattern))
            yield break;

        // Check enabled flag synchronously (fast DB read, cached by SQLite)
        var enabled = _ai.IsEnabledAsync().GetAwaiter().GetResult();
        if (!enabled) yield break;

        List<string> entities;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            entities = _ai.ExtractEntitiesAsync(text, pattern.Pattern, cts.Token)
                          .GetAwaiter().GetResult();
        }
        catch
        {
            // Any failure (timeout, model error, etc.) — silently skip, other engines still run
            yield break;
        }

        foreach (var entity in entities)
        {
            if (entity.Length == 0) continue;

            // Find all occurrences of this entity in the source text
            var searchFrom = 0;
            while (searchFrom < text.Length)
            {
                var idx = text.IndexOf(entity, searchFrom, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) break;

                yield return new RedactMatch
                {
                    StartIndex  = idx,
                    Length      = entity.Length,
                    FieldName   = string.Empty,   // filled by RedactionOrchestrator
                    Replacement = replacement,
                    MatchedText = text.Substring(idx, entity.Length)
                };

                searchFrom = idx + entity.Length;
            }
        }
    }
}
