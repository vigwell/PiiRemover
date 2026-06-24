using Google.Apis.Auth.OAuth2;
using Google.Cloud.Speech.V1;
using Google.Protobuf;
using Grpc.Auth;
using Grpc.Core;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace PiiRemover.Api.Services;

/// <summary>
/// Handles one real-time STT WebSocket connection using Google Cloud Speech (medical_dictation model).
/// Audio flow: browser PCM 16kHz 16-bit mono binary frames → Google streaming API → WS text events.
/// Post-processing: decimal numbers, spoken dates, punctuation commands (TranscriptProcessor).
/// </summary>
public class SttSession : IAsyncDisposable
{
    private readonly WebSocket _ws;
    private readonly IConfiguration _config;
    private readonly ILogger _logger;

    // Active Google stream — replaced on auto-restart after 5-min limit
    private GoogleStream? _current;
    private string _language = "en-US";
    private bool _started;

    private SttSession(WebSocket ws, IConfiguration config, ILogger logger)
    {
        _ws     = ws;
        _config = config;
        _logger = logger;
    }

    public static async Task RunAsync(WebSocket ws, IConfiguration config, ILogger logger, CancellationToken ct)
    {
        await using var session = new SttSession(ws, config, logger);
        await session.LoopAsync(ct);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var buf = new byte[65536];
        await SendJsonAsync(new { type = "ready" }, ct);

        while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            try { result = await _ws.ReceiveAsync(buf, ct); }
            catch { break; }

            if (result.MessageType == WebSocketMessageType.Close) break;

            if (result.MessageType == WebSocketMessageType.Text)
            {
                await HandleControlAsync(Encoding.UTF8.GetString(buf, 0, result.Count), ct);
            }
            else if (result.MessageType == WebSocketMessageType.Binary && _started)
            {
                // Auto-restart if the 5-minute Google stream limit expired
                if (_current?.IsCompleted == true)
                {
                    _logger.LogInformation("Google STT stream expired — restarting");
                    await (_current?.StopAsync() ?? Task.CompletedTask);
                    _current = await StartGoogleStreamAsync(ct);
                }

                if (_current is not null)
                {
                    var chunk = new byte[result.Count];
                    Buffer.BlockCopy(buf, 0, chunk, 0, result.Count);
                    await _current.WriteAudioAsync(chunk);
                }
            }
        }
    }

    private async Task HandleControlAsync(string json, CancellationToken ct)
    {
        try
        {
            using var doc  = JsonDocument.Parse(json);
            var type = doc.RootElement.GetProperty("type").GetString();

            if (type == "start")
            {
                _language = doc.RootElement.TryGetProperty("language", out var l)
                    ? l.GetString() ?? "en-US" : "en-US";

                if (_current is not null) await _current.StopAsync();
                _streamBaseMs = 0; // reset on explicit new session
                _current = await StartGoogleStreamAsync(ct);
                _started = true;
            }
            else if (type == "stop")
            {
                _started = false;
                if (_current is not null) { await _current.StopAsync(); _current = null; }
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "STT control message error"); }
    }

    // Accumulated ms from all streams that have ended — so timestamps survive the 5-min restart
    private long _streamBaseMs;

    private async Task<GoogleStream> StartGoogleStreamAsync(CancellationToken ct)
    {
        var credPath = _config["GoogleSpeech:CredentialsFilePath"]
            ?? throw new InvalidOperationException("GoogleSpeech:CredentialsFilePath not configured.");

        if (!File.Exists(credPath))
            throw new FileNotFoundException($"Google credentials not found: {credPath}");

        var credential = GoogleCredential.FromFile(credPath).CreateScoped(SpeechClient.DefaultScopes);
        var client = new SpeechClientBuilder { ChannelCredentials = credential.ToChannelCredentials() }.Build();
        var call   = client.StreamingRecognize();

        var useMedical = _language.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        var cfg = new RecognitionConfig
        {
            Encoding                   = RecognitionConfig.Types.AudioEncoding.Linear16,
            SampleRateHertz            = 16000,
            LanguageCode               = _language,
            Model                      = useMedical ? "medical_dictation" : "default",
            EnableAutomaticPunctuation = true,
            EnableWordTimeOffsets      = true,  // real per-word timestamps for VTT generation
        };

        await call.WriteAsync(new StreamingRecognizeRequest
        {
            StreamingConfig = new StreamingRecognitionConfig { Config = cfg, InterimResults = true }
        });

        _logger.LogInformation("Google STT started: language={Lang} model={Model}", _language, cfg.Model);

        var cts       = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var baseMs    = _streamBaseMs;
        var streamSw  = System.Diagnostics.Stopwatch.StartNew();

        var responseTask = Task.Run(async () =>
        {
            try
            {
                long lastEndMs = 0;
                await foreach (var response in call.GetResponseStream().WithCancellation(cts.Token))
                {
                    foreach (var r in response.Results)
                    {
                        if (r.Alternatives.Count == 0) continue;
                        var alt     = r.Alternatives[0];
                        var text    = TranscriptProcessor.Process(alt.Transcript);
                        var isFinal = r.IsFinal;
                        if (string.IsNullOrWhiteSpace(text)) continue;

                        if (isFinal && alt.Words.Count > 0)
                        {
                            // Real per-word timestamps — used by the browser to build accurate VTT cues
                            var startMs = baseMs + (long)alt.Words[0].StartTime.ToTimeSpan().TotalMilliseconds;
                            var endMs   = baseMs + (long)alt.Words[^1].EndTime.ToTimeSpan().TotalMilliseconds;
                            lastEndMs   = endMs - baseMs;
                            await SendJsonAsync(
                                new { type = "transcript", text, isFinal = true, startMs, endMs },
                                CancellationToken.None);
                        }
                        else
                        {
                            await SendJsonAsync(new { type = "transcript", text, isFinal }, CancellationToken.None);
                        }
                    }
                }
                // Advance base for next stream restart — use last known result end or elapsed wall time
                _streamBaseMs = baseMs + Math.Max(lastEndMs, streamSw.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) { }
            catch (RpcException ex) when (ex.StatusCode is StatusCode.Cancelled) { }
            catch (RpcException ex) when (ex.StatusCode is StatusCode.OutOfRange or StatusCode.Unavailable)
            {
                _logger.LogWarning("Google STT stream ended: {Status}", ex.StatusCode);
                _streamBaseMs = baseMs + Math.Max(0, streamSw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Google STT response error");
                await SendJsonAsync(new { type = "error", message = ex.Message }, CancellationToken.None);
            }
        }, cts.Token);

        return new GoogleStream(call, responseTask, cts);
    }

    private async Task SendJsonAsync(object payload, CancellationToken ct)
    {
        if (_ws.State != WebSocketState.Open) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        try { await _ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct); }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_current is not null) await _current.StopAsync();
    }

    // ── Inner class: one Google streaming call ───────────────────────────────

    private sealed class GoogleStream
    {
        private readonly SpeechClient.StreamingRecognizeStream _stream;
        private readonly Task _responseTask;
        private readonly CancellationTokenSource _cts;
        private int _stopped;

        public bool IsCompleted => _responseTask.IsCompleted;

        public GoogleStream(SpeechClient.StreamingRecognizeStream stream, Task responseTask, CancellationTokenSource cts)
        {
            _stream = stream; _responseTask = responseTask; _cts = cts;
        }

        public async Task WriteAudioAsync(byte[] chunk)
        {
            if (Interlocked.CompareExchange(ref _stopped, 0, 0) == 1) return;
            try
            {
                await _stream.WriteAsync(new StreamingRecognizeRequest
                    { AudioContent = ByteString.CopyFrom(chunk) });
            }
            catch (InvalidOperationException) { }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled) { }
        }

        public async Task StopAsync()
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 1) return;
            try { await _stream.WriteCompleteAsync(); } catch { }
            try { await _responseTask; } catch (OperationCanceledException) { }
              catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled) { }
            _cts.Dispose();
        }
    }
}
