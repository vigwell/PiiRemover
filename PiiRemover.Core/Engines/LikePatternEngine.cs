using System.Text;
using System.Text.RegularExpressions;
using PiiRemover.Core.Models;

namespace PiiRemover.Core.Engines;

/// <summary>
/// SQL-style wildcard matching.
///   *  = any sequence of characters (including spaces)
///   ?  = any single character
///
/// Examples:
///   *cohen*          matches "mr.cohen", "COHEN123", "cohenberg"
///   Dr.?             matches "Dr.A", "DR.B"  (exactly one char after the dot)
///   *@gmail.com      matches "user@gmail.com", "foo+bar@gmail.com"
/// </summary>
public sealed class LikePatternEngine : RegexBasedEngine
{
    public override PatternType SupportedType => PatternType.Like;

    protected override string ToRegexPattern(string pattern)
    {
        var sb = new StringBuilder();

        bool startsWild = pattern.StartsWith('*') || pattern.StartsWith('?');
        bool endsWild   = pattern.EndsWith('*')   || pattern.EndsWith('?');

        // Add left word boundary only when pattern doesn't begin with a wildcard
        if (!startsWild) sb.Append(@"\b");

        foreach (char c in pattern)
        {
            if      (c == '*') sb.Append(".*");
            else if (c == '?') sb.Append('.');
            else               sb.Append(Regex.Escape(c.ToString()));
        }

        // Add right word boundary only when pattern doesn't end with a wildcard
        if (!endsWild) sb.Append(@"\b");

        return sb.ToString();
    }
}
