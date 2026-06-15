using System.Text.RegularExpressions;
using PiiRemover.Core.Models;

namespace PiiRemover.Core.Engines;

/// <summary>
/// Exact whole-word match, case-insensitive.
/// Simpler to configure than a Regex for non-technical users.
///
/// Examples:
///   "Smith"   matches  Smith, SMITH, smith  but NOT  Smithsonian
///   "Cohen"   matches  Cohen, COHEN         but NOT  CohenBerg
/// </summary>
public sealed class WholeWordEngine : RegexBasedEngine
{
    public override PatternType SupportedType => PatternType.WholeWord;

    protected override string ToRegexPattern(string pattern)
    {
        var escaped = Regex.Escape(pattern.Trim());
        return $@"\b{escaped}\b";
    }
}
