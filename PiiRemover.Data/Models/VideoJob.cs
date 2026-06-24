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
    public string? TranscriptSegments { get; set; }  // JSON [{text,startMs,endMs}] from live STT; null for file uploads
    public bool CreateCaptions { get; set; }    // generate .vtt sidecar from TranscriptText
    public bool RedactPii { get; set; }         // redact PII in the captions file
    public bool RedactAudioPii { get; set; }    // mute PII words in the audio track
    public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("o");
    public string? StartedAt { get; set; }
    public string? CompletedAt { get; set; }
    public long? DurationMs { get; set; }
    public string? ErrorMsg { get; set; }
}
