using System.Data;
using Dapper;
using PiiRemover.Core.Models;

namespace PiiRemover.Data;

/// <summary>
/// Call DapperConfig.Register() once at startup before any DB access.
/// Teaches Dapper to convert the TEXT values stored in PiiPatterns.PatternType
/// ("REGEX", "CONST_LIST", "LLM_PROMPT") to/from the C# PatternType enum.
/// </summary>
public static class DapperConfig
{
    private static bool _registered;

    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        SqlMapper.AddTypeHandler(new PatternTypeHandler());
    }
}

file class PatternTypeHandler : SqlMapper.TypeHandler<PatternType>
{
    public override PatternType Parse(object value)
    {
        return (value?.ToString() ?? "").Replace("_", "").ToUpperInvariant() switch
        {
            "REGEX"     => PatternType.Regex,
            "CONSTLIST" => PatternType.ConstList,
            "LLMPROMPT" => PatternType.LlmPrompt,
            _           => PatternType.Regex
        };
    }

    public override void SetValue(IDbDataParameter parameter, PatternType value)
    {
        parameter.Value = value switch
        {
            PatternType.Regex     => "REGEX",
            PatternType.ConstList => "CONST_LIST",
            PatternType.LlmPrompt => "LLM_PROMPT",
            _                     => "REGEX"
        };
    }
}
