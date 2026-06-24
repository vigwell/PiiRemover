using System.Diagnostics;
using System.Globalization;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using PiiRemover.Core.Engines;

namespace PiiRemover.Api.Services;

/// <summary>
/// Runs an offline speech recognition pass on the uploaded audio file to find the exact
/// timestamps of PII words, returning time ranges to silence in the final FFmpeg pass.
///
/// Flow:
///   1. FFmpeg converts the uploaded audio (WebM) to a temp WAV (16-bit PCM, 16kHz, mono)
///   2. System.Speech.Recognition processes the WAV synchronously, phrase by phrase
///   3. For each recognised word, RecognitionResult.GetAudioForWordRange() provides the
///      exact AudioPosition (TimeSpan) and Duration — both measured from the file start
///   4. Each word is tested against the client's PII field rules via RedactionOrchestrator
///   5. Matched words → (Start, End) time range → returned to VideoWorkerService
///   6. Temp WAV deleted in finally block regardless of outcome
/// </summary>
public class AudioPiiRedactionService
{
    private readonly VideoSettings _videoSettings;
    private readonly RedactionOrchestrator _orchestrator;
    private readonly FieldsCache _fieldsCache;
    private readonly ILogger<AudioPiiRedactionService> _logger;

    public AudioPiiRedactionService(
        VideoSettings videoSettings,
        RedactionOrchestrator orchestrator,
        FieldsCache fieldsCache,
        ILogger<AudioPiiRedactionService> logger)
    {
        _videoSettings = videoSettings;
        _orchestrator  = orchestrator;
        _fieldsCache   = fieldsCache;
        _logger        = logger;
    }

    public async Task<IReadOnlyList<(TimeSpan Start, TimeSpan End)>> GetRedactionRangesAsync(
        string audioPath, int clientId, CancellationToken ct)
    {
        var wavPath = Path.Combine(
            Path.GetDirectoryName(audioPath)!,
            Path.GetFileNameWithoutExtension(audioPath) + ".piitmp.wav");

        try
        {
            // Step 1 — convert to WAV (System.Speech requires PCM)
            await ConvertToWavAsync(audioPath, wavPath, ct);

            // Step 2 — run offline recognition
            var phrases = RunOfflineRecognition(wavPath);
            if (phrases.Count == 0)
            {
                _logger.LogInformation("AudioPiiRedaction: no phrases recognised in {Path}", audioPath);
                return [];
            }

            // Step 3 — find PII words
            var fields = await _fieldsCache.GetFieldsAsync(clientId);
            var ranges = new List<(TimeSpan, TimeSpan)>();

            foreach (var result in phrases)
            {
                foreach (var word in result.Words)
                {
                    var cleaned = word.Text.Trim(' ', ',', '.', '!', '?');
                    if (string.IsNullOrWhiteSpace(cleaned)) continue;

                    var redacted = _orchestrator.Redact(cleaned, fields);
                    if (redacted.Matches.Count > 0)
                    {
                        try
                        {
                            var seg = result.GetAudioForWordRange(word, word);
                            ranges.Add((seg.AudioPosition, seg.AudioPosition + seg.Duration));
                            _logger.LogDebug("AudioPiiRedaction: silencing '{Word}' @ {Start:F3}s–{End:F3}s",
                                word.Text, seg.AudioPosition.TotalSeconds,
                                (seg.AudioPosition + seg.Duration).TotalSeconds);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "AudioPiiRedaction: could not get timing for word '{Word}'", word.Text);
                        }
                    }
                }
            }

            _logger.LogInformation("AudioPiiRedaction: {Count} PII time range(s) to silence", ranges.Count);
            return ranges;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AudioPiiRedaction: failed — skipping audio redaction for {Path}", audioPath);
            return [];
        }
        finally
        {
            if (File.Exists(wavPath))
                try { File.Delete(wavPath); } catch { }
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task ConvertToWavAsync(string inputPath, string wavPath, CancellationToken ct)
    {
        var ffmpeg = await _videoSettings.GetFfmpegPathAsync();

        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName               = ffmpeg,
                Arguments              = $"-y -i \"{inputPath}\" -ar 16000 -ac 1 -f wav \"{wavPath}\"",
                RedirectStandardError  = true,
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            }
        };

        proc.Start();
        proc.BeginErrorReadLine();
        proc.BeginOutputReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(2));
        await proc.WaitForExitAsync(cts.Token);

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"FFmpeg WAV conversion failed with exit code {proc.ExitCode}");
    }

    private static List<RecognitionResult> RunOfflineRecognition(string wavPath)
    {
        var results = new List<RecognitionResult>();

        using var engine = new SpeechRecognitionEngine(new CultureInfo("en-US"));
        engine.LoadGrammar(new DictationGrammar());

        using var stream = File.OpenRead(wavPath);
        var fmt = new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono);
        engine.SetInputToAudioStream(stream, fmt);

        RecognitionResult? result;
        while ((result = engine.Recognize()) is not null)
            results.Add(result);

        return results;
    }
}
