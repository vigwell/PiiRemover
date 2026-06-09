using PiiRemover.Core.Models;

namespace PiiRemover.Core.Engines;

public interface IPatternEngine
{
    PatternType SupportedType { get; }
    IEnumerable<RedactMatch> FindMatches(string text, PiiPattern pattern, string replacement);
}
