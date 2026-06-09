using PiiRemover.Core.Models;

namespace PiiRemover.Core.Engines;

public class ConstListEngine : IPatternEngine
{
    public PatternType SupportedType => PatternType.ConstList;

    public IEnumerable<RedactMatch> FindMatches(string text, PiiPattern pattern, string replacement)
    {
        var terms = pattern.Pattern.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var term in terms)
        {
            int idx = 0;
            while ((idx = text.IndexOf(term, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                yield return new RedactMatch
                {
                    StartIndex = idx,
                    Length = term.Length,
                    FieldName = string.Empty,
                    Replacement = replacement,
                    MatchedText = text.Substring(idx, term.Length)
                };
                idx += term.Length;
            }
        }
    }
}
