using PiiRemover.Core.Models;

namespace PiiRemover.Core.Engines;

// Stub — wire to local Ollama/llama.cpp via HttpClient when ready
public class LlmPromptEngine : IPatternEngine
{
    public PatternType SupportedType => PatternType.LlmPrompt;

    public IEnumerable<RedactMatch> FindMatches(string text, PiiPattern pattern, string replacement)
        => Enumerable.Empty<RedactMatch>();
}
