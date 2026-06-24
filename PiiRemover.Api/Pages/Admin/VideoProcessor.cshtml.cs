using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PiiRemover.Api.Services;
using PiiRemover.Data.Repositories;

namespace PiiRemover.Api.Pages.Admin;

[Authorize]
public class VideoProcessorModel : PageModel
{
    private readonly VideoSettings _videoSettings;
    private readonly ISettingsRepository _settingsRepo;

    public Dictionary<string, string> VideoSettingsMap { get; private set; } = new();
    public bool SettingsSaved { get; private set; }

    public VideoProcessorModel(VideoSettings videoSettings, ISettingsRepository settingsRepo)
    {
        _videoSettings = videoSettings;
        _settingsRepo  = settingsRepo;
    }

    public async Task OnGetAsync()
    {
        await LoadSettingsAsync();
    }

    public async Task<IActionResult> OnPostSaveVideoSettingsAsync(
        bool enabled, string? storagePath,
        string? ffmpegPath, string? ffmpegPreset, int ffmpegCrf, int ffmpegFontSize, int ffmpegTopPadding, int ffmpegTextYPos,
        int maxFileSizeMb, int batchSize, int workerPollSeconds, int wsTokenExpiry, int wsIdleTimeout,
        bool deleteInput, int cleanupOlderThanHours, bool piiRedactionEnabled, bool piiAudioRedactionEnabled)
    {
        await _settingsRepo.SetAsync(VideoSettings.KeyEnabled,          enabled ? "true" : "false",    VideoSettings.Metadata[VideoSettings.KeyEnabled].Description);
        await _settingsRepo.SetAsync(VideoSettings.KeyStoragePath,      storagePath ?? "",              VideoSettings.Metadata[VideoSettings.KeyStoragePath].Description);
        await _settingsRepo.SetAsync(VideoSettings.KeyFfmpegPath,       ffmpegPath ?? VideoSettings.DefaultFfmpegPath, VideoSettings.Metadata[VideoSettings.KeyFfmpegPath].Description);
        await _settingsRepo.SetAsync(VideoSettings.KeyFfmpegPreset,     ffmpegPreset ?? VideoSettings.DefaultPreset,   VideoSettings.Metadata[VideoSettings.KeyFfmpegPreset].Description);
        await _settingsRepo.SetAsync(VideoSettings.KeyFfmpegCrf,        ffmpegCrf.ToString(),           VideoSettings.Metadata[VideoSettings.KeyFfmpegCrf].Description);
        await _settingsRepo.SetAsync(VideoSettings.KeyFfmpegFontSize,   ffmpegFontSize.ToString(),      VideoSettings.Metadata[VideoSettings.KeyFfmpegFontSize].Description);
        await _settingsRepo.SetAsync(VideoSettings.KeyFfmpegTopPadding, ffmpegTopPadding.ToString(),    VideoSettings.Metadata[VideoSettings.KeyFfmpegTopPadding].Description);
        await _settingsRepo.SetAsync(VideoSettings.KeyFfmpegTextYPos,   ffmpegTextYPos.ToString(),      VideoSettings.Metadata[VideoSettings.KeyFfmpegTextYPos].Description);
        await _settingsRepo.SetAsync(VideoSettings.KeyMaxFileSizeMb,    maxFileSizeMb.ToString(),       VideoSettings.Metadata[VideoSettings.KeyMaxFileSizeMb].Description);
        await _settingsRepo.SetAsync(VideoSettings.KeyBatchSize,        batchSize.ToString(),           VideoSettings.Metadata[VideoSettings.KeyBatchSize].Description);
        await _settingsRepo.SetAsync(VideoSettings.KeyWorkerPollSecs,   workerPollSeconds.ToString(),   VideoSettings.Metadata[VideoSettings.KeyWorkerPollSecs].Description);
        await _settingsRepo.SetAsync(VideoSettings.KeyWsTokenExpiry,    wsTokenExpiry.ToString(),       VideoSettings.Metadata[VideoSettings.KeyWsTokenExpiry].Description);
        await _settingsRepo.SetAsync(VideoSettings.KeyWsIdleTimeout,    wsIdleTimeout.ToString(),       VideoSettings.Metadata[VideoSettings.KeyWsIdleTimeout].Description);
        await _settingsRepo.SetAsync(VideoSettings.KeyDeleteInput,           deleteInput ? "true" : "false",       VideoSettings.Metadata[VideoSettings.KeyDeleteInput].Description);
        await _settingsRepo.SetAsync(VideoSettings.KeyCleanupOlderThanHours, cleanupOlderThanHours.ToString(),      VideoSettings.Metadata[VideoSettings.KeyCleanupOlderThanHours].Description);
        await _settingsRepo.SetAsync(VideoSettings.KeyPiiRedactionEnabled,      piiRedactionEnabled      ? "true" : "false", VideoSettings.Metadata[VideoSettings.KeyPiiRedactionEnabled].Description);
        await _settingsRepo.SetAsync(VideoSettings.KeyPiiAudioRedactionEnabled, piiAudioRedactionEnabled ? "true" : "false", VideoSettings.Metadata[VideoSettings.KeyPiiAudioRedactionEnabled].Description);

        SettingsSaved = true;
        await LoadSettingsAsync();
        return Page();
    }

    public async Task<IActionResult> OnGetTestFfmpegAsync()
    {
        try
        {
            var ffmpegPath = await _videoSettings.GetFfmpegPathAsync();
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName               = ffmpegPath,
                    Arguments              = "-version",
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true
                }
            };
            proc.Start();
            var output = await proc.StandardOutput.ReadToEndAsync();
            var error  = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            var combined = (output + error).Split('\n').FirstOrDefault(l => l.StartsWith("ffmpeg")) ?? output;
            return new JsonResult(new { output = combined.Trim() });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { error = ex.Message });
        }
    }

    private async Task LoadSettingsAsync()
    {
        var map = new Dictionary<string, string>();
        foreach (var key in VideoSettings.Metadata.Keys)
            map[key] = await _videoSettings.GetAsync(key);
        VideoSettingsMap = map;
    }
}
