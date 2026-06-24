using System.Diagnostics;
using System.Text;
using PiiRemover.Data.Models;

namespace PiiRemover.Api.Services;

/// <summary>
/// Invokes FFmpeg to merge video + audio and optionally burn a transcript text overlay.
/// FFmpeg command mirrors Rads4Vet VideoProcessor.ContainerApp with local-filesystem paths.
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
        var textYPos = await _vs.GetTextYPosAsync();

        string? tmpTranscript = null;
        string vfArg = string.Empty;
        string afArg = string.Empty;

        if (!string.IsNullOrWhiteSpace(job.TranscriptText))
        {
            // Write transcript to a temp text file — drawtext textfile= handles multi-line
            tmpTranscript = Path.Combine(storagePath, "temp", $"{job.Id}_transcript.txt");
            await File.WriteAllTextAsync(tmpTranscript, job.TranscriptText, Encoding.UTF8, ct);

            // FFmpeg drawtext path: forward slashes, colon escaped
            var fmtPath = tmpTranscript.Replace('\\', '/').Replace(":", "\\:");
            vfArg = $"-vf \"drawtext=textfile='{fmtPath}':fontcolor=white:fontsize={fontSize}" +
                    $":box=1:boxcolor=black@0.5:x=10:y=h-th-{textYPos}\"";
        }

        // Build audio mute filter for PII time ranges: volume=0:enable='between(t,s1,e1)+between(t,s2,e2)'
        if (audioRanges is { Count: > 0 })
        {
            var between = string.Join("+", audioRanges.Select(r =>
                $"between(t,{r.Start.TotalSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}" +
                $",{r.End.TotalSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)})"));
            afArg = $"-af \"volume=0:enable='{between}'\"";
        }

        string args;
        if (job.AudioPath is not null)
        {
            args = $"-y -i \"{job.VideoPath}\" -i \"{job.AudioPath}\" " +
                   $"-map 0:v -map 1:a -c:v libx264 -preset {preset} -crf {crf} " +
                   $"-c:a aac -shortest -movflags +faststart {vfArg} {afArg} \"{job.OutputPath}\"";
        }
        else
        {
            args = $"-y -i \"{job.VideoPath}\" " +
                   $"-c:v libx264 -preset {preset} -crf {crf} -movflags +faststart {vfArg} {afArg} \"{job.OutputPath}\"";
        }

        _logger.LogInformation("FFmpeg [{JobId}]: {Args}", job.Id, args);

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

        var stderr = new StringBuilder();
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
        finally
        {
            if (tmpTranscript is not null)
                try { File.Delete(tmpTranscript); } catch { }
        }

        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"FFmpeg exited with code {proc.ExitCode}. Output:\n{stderr}");

        _logger.LogInformation("FFmpeg [{JobId}] completed successfully", job.Id);
    }
}
