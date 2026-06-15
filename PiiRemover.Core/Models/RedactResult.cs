namespace PiiRemover.Core.Models;

public class RedactResult
{
    public string RedactedText { get; set; } = string.Empty;
    public List<RedactMatch> Matches { get; set; } = new();
    public long DurationMs { get; set; }
}
