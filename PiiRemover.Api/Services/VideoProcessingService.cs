using System.Diagnostics;
using PiiRemover.Data.Models;

namespace PiiRemover.Api.Services;

/// <summary>
/// Invokes FFmpeg to merge video + audio (no text baked in).
/// When transcript text is present, writes a WebVTT captions file (.vtt) alongside the MP4
/// so the browser can play video + captions together without altering the video stream.
/// </summary>
public class VideoProcessingService
{
    private readonly VideoSettings _vs;
    private readonly ILogger<VideoProcessingService> _logger;

    public VideoProcessingService(VideoSettings vs, ILogger<VideoProcessingService> logger)
    {
        _vs     = vs;
        _logger = logger;
    }

    public async Task ProcessAsync(VideoJob job, string storagePath, CancellationToken ct,
        IReadOnlyList<(TimeSpan Start, TimeSpan End)>? audioRanges = null)
    {
        var ffmpeg  = await _vs.GetFfmpegPathAsync();
        var preset  = await _vs.GetPresetAsync();
        var crf     = await _vs.GetCrfAsync();

        // ── Generate WebVTT captions file (only when createCaptions=true) ───
        if (job.CreateCaptions && !string.IsNullOrWhiteSpace(job.TranscriptText))
        {
            var vttPath    = Path.ChangeExtension(job.OutputPath, ".vtt");
            var vttContent = BuildVtt(job.TranscriptText, job.TranscriptSegments);
            await File.WriteAllTextAsync(vttPath, vttContent, System.Text.Encoding.UTF8, ct);
            _logger.LogInformation("FFmpeg [{JobId}] captions written ({Source}) → {Path}",
                job.Id, job.TranscriptSegments is null ? "estimated" : "real timestamps", vttPath);
        }

        // ── Video filter (no overlay — captions are in the sidecar .vtt) ─────
        const string vfFilter = "fps=10,scale=1280:-2:flags=fast_bilinear";
        string vfArg = $"-vf \"{vfFilter}\"";

        // ── Audio mute filter for PII time ranges ────────────────────────────
        string afArg = string.Empty;
        if (audioRanges is { Count: > 0 })
        {
            var between = string.Join("+", audioRanges.Select(r =>
                $"between(t,{r.Start.TotalSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}" +
                $",{r.End.TotalSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)})"));
            afArg = $"-af \"volume=0:enable='{between}'\"";
        }

        // ── FFmpeg command ───────────────────────────────────────────────────
        const string probe = "-probesize 5M -analyzeduration 10M";
        string args = job.AudioPath is not null
            ? $"-y {probe} -threads 0 -i \"{job.VideoPath}\" -i \"{job.AudioPath}\" " +
              $"-map 0:v -map 1:a {vfArg} {afArg} " +
              $"-c:v libx264 -preset {preset} -tune zerolatency -crf {crf} " +
              $"-c:a aac -threads 0 -movflags +faststart -shortest \"{job.OutputPath}\""
            : $"-y {probe} -threads 0 -i \"{job.VideoPath}\" " +
              $"{vfArg} {afArg} " +
              $"-c:v libx264 -preset {preset} -tune zerolatency -crf {crf} " +
              $"-threads 0 -movflags +faststart \"{job.OutputPath}\"";

        _logger.LogInformation("FFmpeg [{JobId}]: {Args}", job.Id, args);

        // ── Execute ──────────────────────────────────────────────────────────
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName               = ffmpeg,
                Arguments              = args,
                RedirectStandardError  = true,
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            }
        };

        var stderr = new System.Text.StringBuilder();
        proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        proc.Start();
        proc.BeginErrorReadLine();
        proc.BeginOutputReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(10));

        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"FFmpeg timed out after 10 minutes for job {job.Id}");
        }

        if (proc.ExitCode != 0)
        {
            _logger.LogError("FFmpeg [{JobId}] failed (exit {Code}):\n{Stderr}", job.Id, proc.ExitCode, stderr);
            throw new InvalidOperationException($"FFmpeg exited with code {proc.ExitCode}.\n{stderr}");
        }

        _logger.LogInformation("FFmpeg [{JobId}] completed successfully", job.Id);
    }

    // ── WebVTT generation ─────────────────────────────────────────────────────

    private const int MaxWordsPerCue = 10;

    // Entry point: use real timestamps when available, fall back to word-rate estimate.
    private static string BuildVtt(string transcript, string? segmentsJson)
    {
        if (!string.IsNullOrWhiteSpace(segmentsJson))
        {
            try
            {
                var segments = System.Text.Json.JsonSerializer.Deserialize<SttSegment[]>(segmentsJson,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (segments is { Length: > 0 })
                    return BuildVttFromSegments(segments);
            }
            catch { /* fall through to estimate */ }
        }
        return BuildVttEstimated(transcript);
    }

    // Real timestamps: each STT segment → split into N-word cues, duration proportional within segment.
    private static string BuildVttFromSegments(SttSegment[] segments)
    {
        var sb = new System.Text.StringBuilder("WEBVTT\n\n");

        foreach (var seg in segments)
        {
            if (string.IsNullOrWhiteSpace(seg.Text)) continue;
            var words    = seg.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            double durMs = seg.EndMs - seg.StartMs;
            double msPerWord = words.Length > 0 ? durMs / words.Length : durMs;

            for (int i = 0; i < words.Length; i += MaxWordsPerCue)
            {
                int    count      = Math.Min(MaxWordsPerCue, words.Length - i);
                double cueStartMs = seg.StartMs + i * msPerWord;
                double cueEndMs   = cueStartMs  + count * msPerWord;

                sb.Append(VttTime(cueStartMs / 1000.0)).Append(" --> ").AppendLine(VttTime(cueEndMs / 1000.0));
                sb.AppendLine(string.Join(" ", words, i, count));
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    // Fallback: word-rate estimate at 150 wpm when no live timestamps are available.
    private static string BuildVttEstimated(string transcript)
    {
        const double wordsPerSec = 2.5;

        var words = transcript.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var sb    = new System.Text.StringBuilder("WEBVTT\n\n");

        for (int i = 0; i < words.Length; i += MaxWordsPerCue)
        {
            int    count    = Math.Min(MaxWordsPerCue, words.Length - i);
            double startSec = i / wordsPerSec;
            double endSec   = (i + count) / wordsPerSec;

            sb.Append(VttTime(startSec)).Append(" --> ").AppendLine(VttTime(endSec));
            sb.AppendLine(string.Join(" ", words, i, count));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string VttTime(double totalSeconds)
    {
        int h  = (int)(totalSeconds / 3600);
        int m  = (int)(totalSeconds % 3600 / 60);
        int s  = (int)(totalSeconds % 60);
        int ms = (int)((totalSeconds - Math.Floor(totalSeconds)) * 1000);
        return $"{h:D2}:{m:D2}:{s:D2}.{ms:D3}";
    }

    private sealed record SttSegment(string Text, long StartMs, long EndMs);
}
