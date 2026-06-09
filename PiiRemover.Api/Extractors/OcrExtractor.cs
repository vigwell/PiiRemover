using PiiRemover.Core.Extractors;
using SkiaSharp;
using Tesseract;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace PiiRemover.Api.Extractors;

// Fallback chain: Windows.Media.Ocr → Tesseract
// Windows.Media.Ocr  — built-in Windows 10+, zero install, supports he-IL + en-US.
// Tesseract          — open-source offline fallback; needs tessdata/heb.traineddata + eng.traineddata.
// Pattern: dual-pass (original + color-inverted) sourced from Rads4Vet LocalPiiScrubber.cs.
//
// Thread safety: SemaphoreSlim caps concurrent OCR ops at MaxConcurrency (default = CPU count).
// This prevents CPU spikes and OOM under stress — OCR is memory-heavy.
public class OcrExtractor : ITextExtractor
{
    private static readonly string[] SupportedMimeTypes =
    [
        "image/png", "image/jpeg", "image/jpg", "image/tiff", "image/bmp", "image/gif", "image/webp"
    ];

    private readonly OcrOptions _opts;
    private readonly SemaphoreSlim _gate;

    public OcrExtractor(OcrOptions opts)
    {
        _opts = opts;
        var maxConcurrency = opts.MaxConcurrency > 0 ? opts.MaxConcurrency : Environment.ProcessorCount;
        _gate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    public bool CanHandle(string mimeType) =>
        SupportedMimeTypes.Contains(mimeType.ToLowerInvariant());

    public async Task<string> ExtractAsync(string filePath, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // Read file once into a pooled byte array — re-used across engine attempts
            var imageBytes = await File.ReadAllBytesAsync(filePath, ct);
            return await ExtractFromBytesAsync(imageBytes, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Called by PdfTextExtractor for embedded page images without hitting the gate a second time
    internal async Task<string> ExtractFromBytesAsync(byte[] imageBytes, CancellationToken ct = default)
    {
        var order = _opts.EngineOrder.Count > 0 ? _opts.EngineOrder : ["WindowsOcr"];
        foreach (var engine in order)
        {
            ct.ThrowIfCancellationRequested();
            var text = engine switch
            {
                "Tesseract" => TryTesseract(imageBytes),
                "WindowsOcr" => await TryWindowsOcrAsync(imageBytes),
                _            => string.Empty
            };
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }
        return string.Empty;
    }

    // ── Windows.Media.Ocr ────────────────────────────────────────────────────
    // Try he-IL then en-US; for each language take the better of normal vs inverted pass.
    // Never concatenate multiple language results — that produces doubled/garbled text.
    private static async Task<string> TryWindowsOcrAsync(byte[] imageBytes)
    {
        foreach (var langCode in new[] { "he-IL", "en-US" })
        {
            var lang = new Language(langCode);
            if (!OcrEngine.IsLanguageSupported(lang)) continue;
            var engine = OcrEngine.TryCreateFromLanguage(lang);
            if (engine is null) continue;

            var normal   = await RunWindowsPassAsync(engine, imageBytes, invert: false);
            var inverted = await RunWindowsPassAsync(engine, imageBytes, invert: true);

            // Pick whichever pass returned more content
            var best = normal.Length >= inverted.Length ? normal : inverted;
            if (!string.IsNullOrWhiteSpace(best))
                return best.Trim();
        }
        return string.Empty;
    }

    private static async Task<string> RunWindowsPassAsync(OcrEngine engine, byte[] imageBytes, bool invert)
    {
        var bytes = invert ? InvertImage(imageBytes) : imageBytes;
        using var ms      = new MemoryStream(bytes, writable: false);
        var decoder       = await BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
        var bitmap        = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var result        = await engine.RecognizeAsync(bitmap);
        return result.Text;
    }

    // ── Tesseract ─────────────────────────────────────────────────────────────
    private string TryTesseract(byte[] imageBytes)
    {
        try
        {
            // TesseractEngine is NOT thread-safe — create per call, protected by _gate above
            using var engine = new TesseractEngine(_opts.TessdataPath, _opts.TesseractLanguages, EngineMode.Default);

            var text = RecognizeWithTesseract(engine, imageBytes);
            if (string.IsNullOrWhiteSpace(text))
                text = RecognizeWithTesseract(engine, InvertImage(imageBytes));
            return text;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Tesseract] failed: {ex.Message}");
            return string.Empty;
        }
    }

    private static string RecognizeWithTesseract(TesseractEngine engine, byte[] imageBytes)
    {
        using var pix  = LoadAsPix(imageBytes);
        using var page = engine.Process(pix);
        return page.GetText() ?? string.Empty;
    }

    private static Pix LoadAsPix(byte[] imageBytes)
    {
        using var bmp  = SKBitmap.Decode(imageBytes);
        using var img  = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return Pix.LoadFromMemory(data.ToArray());
    }

    // ── Shared helpers ────────────────────────────────────────────────────────
    internal static byte[] InvertImage(byte[] imageBytes)
    {
        using var original = SKBitmap.Decode(imageBytes);
        using var surface  = SKSurface.Create(new SKImageInfo(original.Width, original.Height));
        surface.Canvas.DrawBitmap(original, 0, 0, new SKPaint
        {
            ColorFilter = SKColorFilter.CreateColorMatrix(
            [
                -1.5f, 0,     0,     0, 255f * 1.25f,
                 0,   -1.5f,  0,     0, 255f * 1.25f,
                 0,    0,    -1.5f,  0, 255f * 1.25f,
                 0,    0,     0,     1, 0
            ])
        });
        using var image = surface.Snapshot();
        using var data  = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
