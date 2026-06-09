namespace PiiRemover.Core.Models;

public class RedactMatch
{
    public int StartIndex { get; set; }
    public int Length { get; set; }
    public string FieldName { get; set; } = string.Empty;
    public string Replacement { get; set; } = string.Empty;
    public string MatchedText { get; set; } = string.Empty;
}
