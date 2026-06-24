namespace PiiRemover.Data.Models;

public class VideoJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int? ClientId { get; set; }
    public string Status { get; set; } = "queued";     // queued|processing|completed|failed
    public string? VideoPath { get; set; }
    public string? AudioPath { get; set; }
    public string? OutputPath { get; set; }
    public string? VideoName { get; set; }
    public string? AudioName { get; set; }
    public string? TranscriptText { get; set; }
    public bool RedactPii { get; set; }
    public bool RedactAudioPii { get; set; }
    public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("o");
    public string? StartedAt { get; set; }
    public string? CompletedAt { get; set; }
    public long? DurationMs { get; set; }
    public string? ErrorMsg { get; set; }
}
