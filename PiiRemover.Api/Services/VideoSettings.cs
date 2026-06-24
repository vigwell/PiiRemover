using PiiRemover.Data.Repositories;

namespace PiiRemover.Api.Services;

/// <summary>
/// Video Processing feature settings — key constants + lazy-init helpers.
/// Default values are taken from Rads4Vet production configuration.
/// Settings are stored in the Settings table and seeded on first read.
/// </summary>
public class VideoSettings
{
    // ── Setting key constants ─────────────────────────────────────────────────

    public const string KeyEnabled          = "video:enabled";
    public const string KeyStoragePath      = "video:storagePath";
    public const string KeyFfmpegPath       = "video:ffmpeg:executablePath";
    public const string KeyFfmpegPreset     = "video:ffmpeg:preset";
    public const string KeyFfmpegCrf        = "video:ffmpeg:crf";
    public const string KeyFfmpegFontSize   = "video:ffmpeg:fontSize";
    public const string KeyFfmpegTopPadding = "video:ffmpeg:topPadding";
    public const string KeyFfmpegTextYPos   = "video:ffmpeg:textYPosition";
    public const string KeyMaxFileSizeMb    = "video:maxFileSizeMb";
    public const string KeyBatchSize        = "video:batchSize";
    public const string KeyWorkerPollSecs   = "video:workerPollSeconds";
    public const string KeyWsTokenExpiry    = "video:wsTokenExpiryMinutes";
    public const string KeyWsIdleTimeout    = "video:wsConnectionTimeoutMinutes";
    public const string KeyDeleteInput           = "video:deleteInputAfterProcess";
    public const string KeyPiiRedactionEnabled      = "video:piiRedactionEnabled";
    public const string KeyPiiAudioRedactionEnabled = "video:piiAudioRedactionEnabled";
    public const string KeyCleanupOlderThanHours    = "video:cleanupInputOlderThanHours";

    // ── Default values (from Rads4Vet production settings) ────────────────────

    public const string DefaultFfmpegPath      = @"tools\ffmpeg\ffmpeg.exe";
    public const string DefaultPreset          = "ultrafast";
    public const int    DefaultCrf             = 32;
    public const int    DefaultFontSize        = 20;
    public const int    DefaultTopPadding      = 20;
    public const int    DefaultTextYPosition   = 8;
    public const int    DefaultMaxFileSizeMb   = 500;
    public const int    DefaultBatchSize       = 1;
    public const int    DefaultWorkerPollSecs  = 2;
    public const int    DefaultWsTokenExpiry   = 10;
    public const int    DefaultWsIdleTimeout           = 30;
    public const int    DefaultCleanupOlderThanHours  = 24;

    // ── UI descriptions (displayed on admin Settings page) ────────────────────

    public static readonly Dictionary<string, (string Default, string Description)> Metadata = new()
    {
        [KeyEnabled]          = ("false",                    "Enable the Video Processing feature"),
        [KeyStoragePath]      = ("",                         "Folder where uploaded and output video files are stored. Leave blank to use VideoStorage in the app root."),
        [KeyFfmpegPath]       = (DefaultFfmpegPath,          "Path to ffmpeg.exe. Relative paths are resolved from the app root. Default uses the bundled FFmpeg."),
        [KeyFfmpegPreset]     = (DefaultPreset,              "x264 encoding speed preset (ultrafast→veryslow). Faster = larger file."),
        [KeyFfmpegCrf]        = (DefaultCrf.ToString(),      "x264 quality factor (0–51). Lower = better quality, larger file. 32 is a good balance for screen recordings."),
        [KeyFfmpegFontSize]   = (DefaultFontSize.ToString(), "Font size for transcript text overlay burned into the video."),
        [KeyFfmpegTopPadding] = (DefaultTopPadding.ToString(),"Padding (px) above the transcript text overlay."),
        [KeyFfmpegTextYPos]   = (DefaultTextYPosition.ToString(), "Y offset (px) from the bottom edge for the transcript overlay."),
        [KeyMaxFileSizeMb]    = (DefaultMaxFileSizeMb.ToString(), "Maximum allowed upload size per file in MB."),
        [KeyBatchSize]        = (DefaultBatchSize.ToString(), "Maximum concurrent FFmpeg processes. Keep 1 on servers with limited RAM."),
        [KeyWorkerPollSecs]   = (DefaultWorkerPollSecs.ToString(), "How often (seconds) the worker checks for queued jobs."),
        [KeyWsTokenExpiry]    = (DefaultWsTokenExpiry.ToString(), "Minutes before a WebSocket handshake token expires."),
        [KeyWsIdleTimeout]    = (DefaultWsIdleTimeout.ToString(), "Minutes of idle time before a WebSocket connection is cleaned up."),
        [KeyDeleteInput]           = ("true",  "Delete uploaded raw files after successful processing to save disk space."),
        [KeyPiiRedactionEnabled]      = ("false", "Apply PII redaction to the transcript text before burning it as a video overlay. Uses the client's active PII field rules."),
        [KeyPiiAudioRedactionEnabled] = ("false", "🔇 Silence PII words in the audio track. Runs an offline speech recognition pass on the uploaded audio file, identifies when PII words were spoken (per-word timestamps), then instructs FFmpeg to mute those exact moments in the final MP4. Enable PII Redaction on Transcript as well for full protection."),
        [KeyCleanupOlderThanHours]    = ("24",    "Delete orphaned input files for completed/failed jobs older than this many hours (runs hourly). Set 0 to disable."),
    };

    private readonly ISettingsRepository _settings;

    public VideoSettings(ISettingsRepository settings) => _settings = settings;

    /// <summary>Gets a value, seeding the default if the key is missing.</summary>
    public async Task<string> GetAsync(string key)
    {
        var val = await _settings.GetAsync(key);
        if (val is null && Metadata.TryGetValue(key, out var meta))
        {
            await _settings.SetAsync(key, meta.Default, meta.Description);
            return meta.Default;
        }
        return val ?? string.Empty;
    }

    public async Task<bool> IsEnabledAsync()
    {
        var val = await GetAsync(KeyEnabled);
        return val.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string> GetStoragePathAsync()
    {
        var raw = await GetAsync(KeyStoragePath);
        if (string.IsNullOrWhiteSpace(raw))
            raw = Path.Combine(AppContext.BaseDirectory, "VideoStorage");
        return raw;
    }

    public async Task<string> GetFfmpegPathAsync()
    {
        var raw = await GetAsync(KeyFfmpegPath);
        if (string.IsNullOrWhiteSpace(raw))
            raw = DefaultFfmpegPath;
        return Path.IsPathRooted(raw) ? raw : Path.Combine(AppContext.BaseDirectory, raw);
    }

    public async Task<string> GetPresetAsync()    => await GetAsync(KeyFfmpegPreset);
    public async Task<int>    GetCrfAsync()       => int.TryParse(await GetAsync(KeyFfmpegCrf),       out var v) ? v : DefaultCrf;
    public async Task<int>    GetFontSizeAsync()  => int.TryParse(await GetAsync(KeyFfmpegFontSize),  out var v) ? v : DefaultFontSize;
    public async Task<int>    GetTextYPosAsync()  => int.TryParse(await GetAsync(KeyFfmpegTextYPos),  out var v) ? v : DefaultTextYPosition;
    public async Task<int>    GetMaxFileMbAsync() => int.TryParse(await GetAsync(KeyMaxFileSizeMb),   out var v) ? v : DefaultMaxFileSizeMb;
    public async Task<int>    GetBatchSizeAsync() => int.TryParse(await GetAsync(KeyBatchSize),       out var v) ? v : DefaultBatchSize;
    public async Task<int>    GetPollSecsAsync()  => int.TryParse(await GetAsync(KeyWorkerPollSecs),  out var v) ? v : DefaultWorkerPollSecs;
    public async Task<int>    GetWsTokenExpiryAsync() => int.TryParse(await GetAsync(KeyWsTokenExpiry), out var v) ? v : DefaultWsTokenExpiry;
    public async Task<int>    GetWsIdleTimeoutAsync() => int.TryParse(await GetAsync(KeyWsIdleTimeout), out var v) ? v : DefaultWsIdleTimeout;
    public async Task<bool>   GetDeleteInputAsync()           => !(await GetAsync(KeyDeleteInput)).Equals("false", StringComparison.OrdinalIgnoreCase);
    public async Task<bool>   GetPiiRedactionEnabledAsync()      => (await GetAsync(KeyPiiRedactionEnabled)).Equals("true", StringComparison.OrdinalIgnoreCase);
    public async Task<bool>   GetPiiAudioRedactionEnabledAsync() => (await GetAsync(KeyPiiAudioRedactionEnabled)).Equals("true", StringComparison.OrdinalIgnoreCase);
    public async Task<int>    GetCleanupOlderThanHoursAsync()    => int.TryParse(await GetAsync(KeyCleanupOlderThanHours), out var v) ? v : DefaultCleanupOlderThanHours;
}
