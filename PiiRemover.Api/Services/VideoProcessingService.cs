using System.Diagnostics;
using PiiRemover.Data.Models;

namespace PiiRemover.Api.Services;

/// <summary>
/// Invokes FFmpeg to merge video + audio and optionally burn a transcript text overlay.
/// Mirrors the working Rads4Vet VideoProcessor.ContainerApp approach:
///   - Text embedded directly as text='...' (not textfile=) to avoid Windows path escaping issues
///   - pad= adds a black bar; one drawtext per wrapped line positioned inside the bar
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
        var ffmpeg   = await _vs.GetFfmpegPathAsync();
        var preset   = await _vs.GetPresetAsync();
        var crf      = await _vs.GetCrfAsync();
        var fontSize = await _vs.GetFontSizeAsync();

        // ── Video filter ─────────────────────────────────────────────────────
        string vfArg = string.Empty;

        if (!string.IsNullOrWhiteSpace(job.TranscriptText))
        {
            var (lines, topPadding) = WrapText(job.TranscriptText, 1280, fontSize);
            int lineHeight = fontSize + 4;

            // One drawtext filter per line, text embedded inline (no temp file)
            // Windows: font='Calibri' (system font, always present)
            var drawParts = lines.Select((line, i) =>
                $"drawtext=font='Calibri'" +
                $":text='{EscapeText(line)}'" +
                $":x=(w-text_w)/2" +
                $":y={2 + i * lineHeight}" +
                $":fontsize={fontSize}" +
                $":fontcolor=white");

            // pad adds the black bar; drawtext sits inside it
            var allFilters = $"fps=10,scale=1280:-2:flags=fast_bilinear" +
                             $",pad=width=iw:height=ih+{topPadding}:x=0:y={topPadding}:color=black" +
                             $",{string.Join(",", drawParts)}";

            vfArg = $"-vf \"{allFilters}\"";
            _logger.LogInformation("FFmpeg [{JobId}] drawtext: {Lines} line(s), topPadding={Pad}",
                job.Id, lines.Count, topPadding);
        }

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
            throw new InvalidOperationException(
                $"FFmpeg exited with code {proc.ExitCode}.\n{stderr}");
        }

        _logger.LogInformation("FFmpeg [{JobId}] completed successfully", job.Id);
    }

    // ── Text escaping for FFmpeg drawtext inline text= value ─────────────────
    // Must escape: backslash, single-quote, colon (filter syntax delimiters)
    private static string EscapeText(string text) =>
        text.Replace("\\", "\\\\")
            .Replace("'",  "\\'")
            .Replace(":",  "\\:");

    // ── Word-wrap text to fit within 1280px video width ──────────────────────
    private static (IReadOnlyList<string> Lines, int TopPadding) WrapText(
        string text, int videoWidth, int fontSize)
    {
        const double avgCharWidthRatio = 0.55;
        int usableWidth  = videoWidth - 24; // 12px margin each side
        int maxChars     = (int)(usableWidth / (fontSize * avgCharWidthRatio));

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (var word in words)
        {
            if (current.Length == 0)
            {
                current.Append(word);
            }
            else if (current.Length + 1 + word.Length <= maxChars)
            {
                current.Append(' ').Append(word);
            }
            else
            {
                lines.Add(current.ToString());
                current.Clear().Append(word);
            }
        }
        if (current.Length > 0) lines.Add(current.ToString());
        if (lines.Count == 0) lines.Add(string.Empty);

        int topPadding = lines.Count * fontSize + (lines.Count - 1) * 4 + 4;
        return (lines, topPadding);
    }
}
