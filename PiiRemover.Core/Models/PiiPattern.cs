namespace PiiRemover.Core.Models;

public enum PatternType
{
    Regex,
    ConstList,
    LlmPrompt
}

public class PiiPattern
{
    public int Id { get; set; }
    public int FieldId { get; set; }
    public PatternType PatternType { get; set; }
    public string Pattern { get; set; } = string.Empty;
    public int Priority { get; set; }
}
