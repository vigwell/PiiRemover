using System.Text.RegularExpressions;
using PiiRemover.Core.Models;

namespace PiiRemover.Core.Engines;

/// <summary>
/// Matches any whitespace-delimited token that STARTS WITH the given prefix (case-insensitive).
///
/// Examples:
///   "Dr."    matches  Dr.Cohen, DR.SMITH, dr.johnson
///   "PAT-"   matches  PAT-001, PAT-4412
/// </summary>
public sealed class BeginsWithEngine : RegexBasedEngine
{
    public override PatternType SupportedType => PatternType.BeginsWith;

    protected override string ToRegexPattern(string pattern)
    {
        // \b = word boundary (works for Unicode letters in .NET)
        // \S* = any non-whitespace chars following the prefix
        var escaped = Regex.Escape(pattern.Trim());
        return $@"\b{escaped}\S*";
    }
}
