using System.Globalization;
using System.Net.WebSockets;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using System.Text;
using System.Text.Json;

namespace PiiRemover.Api.Services;

/// <summary>
/// Handles one real-time STT WebSocket connection.
/// Audio flow: browser PCM binary frames → AudioPipeStream → SpeechRecognitionEngine → WS text events.
/// Engine: System.Speech.Recognition (Windows built-in, zero install, offline).
/// </summary>
public class SttSession : IDisposable
{
    private readonly WebSocket _ws;
    private readonly AudioPipeStream _pipe = new();
    private SpeechRecognitionEngine? _engine;
    private bool _disposed;

    public SttSession(WebSocket ws) => _ws = ws;

    /// <summary>
    /// Main loop: reads WS frames, feeds PCM to engine, pushes transcripts back.
    /// </summary>
    public static async Task RunAsync(WebSocket ws, CancellationToken ct)
    {
        using var session = new SttSession(ws);
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
                var json = Encoding.UTF8.GetString(buf, 0, result.Count);
                HandleControlMessage(json);
            }
            else if (result.MessageType == WebSocketMessageType.Binary && _engine is not null)
            {
                // Feed PCM chunk to the recognition engine
                _pipe.Write(buf, 0, result.Count);
            }
        }
    }

    private void HandleControlMessage(string json)
    {
        try
        {
            using var doc  = JsonDocument.Parse(json);
            var type = doc.RootElement.GetProperty("type").GetString();

            if (type == "start")
            {
                var lang = doc.RootElement.TryGetProperty("language", out var l)
                    ? l.GetString() ?? "en-US"
                    : "en-US";
                StartEngine(lang);
            }
            else if (type == "stop")
            {
                StopEngine();
            }
        }
        catch { /* malformed control message — ignore */ }
    }

    private void StartEngine(string language)
    {
        StopEngine();
        try
        {
            var culture = new CultureInfo(language);
            _engine = new SpeechRecognitionEngine(culture);
            _engine.LoadGrammar(new DictationGrammar());
            _engine.SetInputToAudioStream(_pipe,
                new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            _engine.SpeechRecognized   += OnFinal;
            _engine.SpeechHypothesized += OnInterim;
            _engine.RecognizeAsync(RecognizeMode.Multiple);
        }
        catch (Exception ex)
        {
            _ = SendJsonAsync(new { type = "error", message = ex.Message }, CancellationToken.None);
        }
    }

    private void StopEngine()
    {
        if (_engine is null) return;
        try { _engine.RecognizeAsyncStop(); } catch { }
        _engine.SpeechRecognized   -= OnFinal;
        _engine.SpeechHypothesized -= OnInterim;
        _engine.Dispose();
        _engine = null;
    }

    private void OnFinal(object? _, SpeechRecognizedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Result.Text)) return;
        _ = SendJsonAsync(new { type = "transcript", text = e.Result.Text, isFinal = true },
            CancellationToken.None);
    }

    private void OnInterim(object? _, SpeechHypothesizedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Result.Text)) return;
        _ = SendJsonAsync(new { type = "transcript", text = e.Result.Text, isFinal = false },
            CancellationToken.None);
    }

    private async Task SendJsonAsync(object payload, CancellationToken ct)
    {
        if (_ws.State != WebSocketState.Open) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        try
        {
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        catch { /* connection may close concurrently */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopEngine();
        _pipe.Complete();
        _pipe.Dispose();
    }
}

/// <summary>
/// Thread-safe blocking pipe stream: Write() appends data; Read() blocks until data is available.
/// Used to bridge async WebSocket frames into the synchronous SpeechRecognitionEngine.
/// </summary>
public sealed class AudioPipeStream : Stream
{
    private readonly Queue<byte[]> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private volatile bool _completed;

    public override bool CanRead  => true;
    public override bool CanSeek  => false;
    public override bool CanWrite => true;
    public override long Length   => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        var chunk = new byte[count];
        Buffer.BlockCopy(buffer, offset, chunk, 0, count);
        lock (_queue) _queue.Enqueue(chunk);
        _signal.Release();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        while (true)
        {
            _signal.Wait();
            lock (_queue)
            {
                if (_queue.Count == 0)
                {
                    if (_completed) return 0;
                    continue;
                }
                var chunk = _queue.Dequeue();
                var copy  = Math.Min(count, chunk.Length);
                Buffer.BlockCopy(chunk, 0, buffer, offset, copy);
                return copy;
            }
        }
    }

    public void Complete()
    {
        _completed = true;
        _signal.Release();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { Complete(); _signal.Dispose(); }
        base.Dispose(disposing);
    }
}
