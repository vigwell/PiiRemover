using Microsoft.AspNetCore.Mvc;
using PiiRemover.Api.Services;
using PiiRemover.Core.Licensing;

namespace PiiRemover.Api.Controllers;

[ApiController]
[Route("api/v1/stt")]
public class SttController : ControllerBase
{
    private readonly SttWebSocketManager _wsManager;
    private readonly VideoSettings _settings;
    private readonly LicenseInfo _license;

    public SttController(SttWebSocketManager wsManager, VideoSettings settings, LicenseInfo license)
    {
        _wsManager = wsManager;
        _settings  = settings;
        _license   = license;
    }

    /// <summary>Issues a short-lived WebSocket handshake token for the STT endpoint.</summary>
    [HttpPost("ws-token")]
    public async Task<IActionResult> WsToken()
    {
        if (!_license.Features.Contains("VideoProcessing", StringComparer.OrdinalIgnoreCase))
            return StatusCode(402, new { error = "VideoProcessing feature is not included in your license." });

        var clientId   = HttpContext.Items["ClientId"] as int? ?? 0;
        var expiryMins = await _settings.GetWsTokenExpiryAsync();
        var token      = _wsManager.IssueToken(clientId, expiryMins);

        var host  = $"{Request.Scheme.Replace("http", "ws")}://{Request.Host}";
        var base_ = Request.PathBase.ToString().TrimEnd('/');
        var wsUrl = $"{host}{base_}/ws/stt?token={token}";

        return Ok(new { token, wsUrl });
    }
}
