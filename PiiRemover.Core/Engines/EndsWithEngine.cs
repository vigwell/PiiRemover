using System.Text.RegularExpressions;
using PiiRemover.Core.Models;

namespace PiiRemover.Core.Engines;

/// <summary>
/// Matches any whitespace-delimited token that ENDS WITH the given suffix (case-insensitive).
///
/// Examples:
///   "@hospital.org"   matches  admin@hospital.org, noreply@hospital.org
///   ".il"             matches  example.co.il, site.il
/// </summary>
public sealed class EndsWithEngine : RegexBasedEngine
{
    public override PatternType SupportedType => PatternType.EndsWith;

    protected override string ToRegexPattern(string pattern)
    {
        // \S* = any non-whitespace chars before the suffix
        // \b  = trailing word boundary
        var escaped = Regex.Escape(pattern.Trim());
        return $@"\S*{escaped}\b";
    }
}
