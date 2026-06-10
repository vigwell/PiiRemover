using System.Text;
using PiiRemover.Core.Extractors;
using SkiaSharp;
using Tesseract;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace PiiRemover.Api.Extractors;

/// <summary>A single OCR word with its pixel bounding box and position in the assembled text.</summary>
public sealed record OcrWordEntry(
    string Text,
    float X, float Y, float Width, float Height,
    int CharStart, int CharEnd);

/// <summary>Full OCR result including text and per-word bounding boxes.</summary>
public sealed record OcrBoundsResult(string FullText, IReadOnlyList<OcrWordEntry> Words)
{
    public static readonly OcrBoundsResult Empty = new(string.Empty, []);
}

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

    // ── OCR with per-word bounding boxes — PII mode ──────────────────────────
    // Dual-pass / longest-wins: finds the single best text extraction for
    // subsequent PII matching.  NOT used for all-text scrubbing — see
    // ExtractAllWordBoundsAsync below.
    internal async Task<OcrBoundsResult> ExtractWithBoundsAsync(
        byte[] imageBytes, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var darkBg    = IsDarkBackground(imageBytes);
            var passOrder = darkBg ? new[] { true, false } : new[] { false, true };

            OcrBoundsResult best = OcrBoundsResult.Empty;

            foreach (var langCode in new[] { "he-IL", "en-US" })
            {
                ct.ThrowIfCancellationRequested();
                var lang = new Language(langCode);
                if (!OcrEngine.IsLanguageSupported(lang)) continue;
                var engine = OcrEngine.TryCreateFromLanguage(lang);
                if (engine is null) continue;

                foreach (var invert in passOrder)
                {
                    var bytes  = invert ? InvertImage(imageBytes) : imageBytes;
                    var result = await RunWindowsPassWithBoundsAsync(engine, bytes);

                    if (result.FullText.Length > best.FullText.Length)
                        best = result;

                    if (best.FullText.Length > 20) break;
                }

                if (!string.IsNullOrWhiteSpace(best.FullText)) break;
            }

            return best;
        }
        finally { _gate.Release(); }
    }

    // ── All-pass word-bounds — all-text scrub mode ────────────────────────────
    // Runs EVERY pass (normal + inverted, at 2× and original scale) for the best
    // available language and merges ALL word bounding boxes.
    //
    // WHY TWO PASSES:
    //   A real-world screenshot has MIXED backgrounds (dark frame, coloured header,
    //   white cards).  Normal pass catches dark-on-light; inverted pass catches
    //   light-on-dark.  Merging both covers every region.
    //
    // WHY UPSCALE:
    //   Windows.Media.Ocr requires glyphs to be ≥ ~40 px tall.  DICOM / mobile-app
    //   overlay text is often 14–20 px at screen resolution — silently skipped by
    //   the engine.  A 2.5× upscale brings those glyphs into the recognisable range.
    //   Bounding boxes are scaled back to original coordinates before returning.
    internal async Task<IReadOnlyList<OcrWordEntry>> ExtractAllWordBoundsAsync(
        byte[] imageBytes, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // Prepare all variants once — normal and inverted at 1× and 2.5×.
            // Running all four passes maximises coverage:
            //   • 1× normal   — large dark-on-light text without upscale blur
            //   • 1× inverted — large light-on-dark text without upscale blur
            //   • 2.5× normal   — small dark-on-light text (upscale brings it into OCR range)
            //   • 2.5× inverted — small light-on-dark text (DICOM overlays, white-on-black)
            const float Scale    = 2.5f;
            var inverted         = InvertImage(imageBytes);
            var passes = new (byte[] bytes, float factor)[]
            {
                (imageBytes,              1f     ),   // 1× normal
                (inverted,                1f     ),   // 1× inverted
                (ScaleImage(imageBytes, Scale), Scale), // 2.5× normal
                (ScaleImage(inverted,   Scale), Scale), // 2.5× inverted
            };

            var allWords = new List<OcrWordEntry>();

            foreach (var langCode in new[] { "he-IL", "en-US" })
            {
                ct.ThrowIfCancellationRequested();
                var lang = new Language(langCode);
                if (!OcrEngine.IsLanguageSupported(lang)) continue;
                var engine = OcrEngine.TryCreateFromLanguage(lang);
                if (engine is null) continue;

                foreach (var (bytes, factor) in passes)
                {
                    var result = await RunWindowsPassWithBoundsAsync(engine, bytes);
                    // Scale bounding boxes back to original-image coordinates
                    allWords.AddRange(factor == 1f ? result.Words : ScaleWords(result.Words, 1f / factor));
                }

                break;   // one language is sufficient for positional detection
            }

            return allWords;
        }
        finally { _gate.Release(); }
    }

    // Scale image to new size (used to upscale before OCR for small-text images)
    private static byte[] ScaleImage(byte[] imageBytes, float scale)
    {
        using var src     = SKBitmap.Decode(imageBytes);
        int newW = (int)(src.Width  * scale);
        int newH = (int)(src.Height * scale);
        using var scaled  = src.Resize(new SKImageInfo(newW, newH), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        using var image   = SKImage.FromBitmap(scaled);
        using var data    = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // Scale bounding boxes back to original-image coordinates
    private static IEnumerable<OcrWordEntry> ScaleWords(
        IReadOnlyList<OcrWordEntry> words, float factor) =>
        words.Select(w => w with
        {
            X      = w.X      * factor,
            Y      = w.Y      * factor,
            Width  = w.Width  * factor,
            Height = w.Height * factor
        });

    private static async Task<OcrBoundsResult> RunWindowsPassWithBoundsAsync(
        OcrEngine engine, byte[] imageBytes)
    {
        using var ms = new MemoryStream(imageBytes, writable: false);
        var decoder  = await BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
        var bitmap   = await decoder.GetSoftwareBitmapAsync(
                           BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var result   = await engine.RecognizeAsync(bitmap);

        var sb        = new StringBuilder();
        var words     = new List<OcrWordEntry>();
        bool firstLine = true;

        foreach (var line in result.Lines)
        {
            if (!firstLine) sb.Append('\n');
            firstLine = false;
            bool firstWord = true;

            foreach (var word in line.Words)
            {
                if (!firstWord) sb.Append(' ');
                firstWord = false;

                int start = sb.Length;
                sb.Append(word.Text);
                int end = sb.Length;

                var r = word.BoundingRect;
                words.Add(new OcrWordEntry(word.Text,
                    (float)r.X, (float)r.Y, (float)r.Width, (float)r.Height,
                    start, end));
            }
        }

        return new OcrBoundsResult(sb.ToString(), words);
    }

    // ── Windows.Media.Ocr ────────────────────────────────────────────────────
    // Strategy:
    //  1. Detect dark-background images (DICOM, PACS viewers, dark terminals).
    //     If >55 % of sampled pixels are dark, the image is inverted for OCR —
    //     white-on-black becomes black-on-white which Windows.Media.Ocr handles
    //     far better.  We still run both passes but start with the better one.
    //  2. Run all supported languages; pick the overall longest result.
    //     (First-non-empty discards better results from later languages.)
    private static async Task<string> TryWindowsOcrAsync(byte[] imageBytes)
    {
        var darkBg   = IsDarkBackground(imageBytes);
        // For dark-background images try inverted first so the primary pass is optimal
        var passOrder = darkBg
            ? new[] { true, false }   // inverted first, then normal as fallback
            : new[] { false, true };  // normal first (light documents)

        string best = string.Empty;

        foreach (var langCode in new[] { "he-IL", "en-US" })
        {
            var lang = new Language(langCode);
            if (!OcrEngine.IsLanguageSupported(lang)) continue;
            var engine = OcrEngine.TryCreateFromLanguage(lang);
            if (engine is null) continue;

            foreach (var invert in passOrder)
            {
                var text = await RunWindowsPassAsync(engine, imageBytes, invert);
                if (text.Length > best.Length)
                    best = text;
                // Early-exit: if the primary pass gave substantial text, skip fallback
                if (best.Length > 20) break;
            }

            if (!string.IsNullOrWhiteSpace(best)) break;   // language found something → stop
        }
        return best.Trim();
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

    /// <summary>
    /// Pure colour inversion (255 – channel).  Works well for DICOM/PACS white-on-black
    /// images.  The previously used ×1.5 boost over-saturates and hurts recognition on
    /// images that already have good contrast.
    /// </summary>
    internal static byte[] InvertImage(byte[] imageBytes)
    {
        using var original = SKBitmap.Decode(imageBytes);
        using var surface  = SKSurface.Create(new SKImageInfo(original.Width, original.Height));
        surface.Canvas.DrawBitmap(original, 0, 0, new SKPaint
        {
            ColorFilter = SKColorFilter.CreateColorMatrix(
            [
                -1f, 0,   0,   0, 255f,
                 0, -1f,  0,   0, 255f,
                 0,  0,  -1f,  0, 255f,
                 0,  0,   0,   1, 0
            ])
        });
        using var image = surface.Snapshot();
        using var data  = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// Returns true when the image has a predominantly dark background (e.g. DICOM viewer,
    /// terminal screenshot, dark-theme PACS).  Samples a grid of pixels and checks the
    /// luminance; if more than 55 % are below the midpoint the background is considered dark.
    /// </summary>
    private static bool IsDarkBackground(byte[] imageBytes)
    {
        try
        {
            using var bmp = SKBitmap.Decode(imageBytes);
            if (bmp is null) return false;

            // Sample at most 2000 pixels on a uniform grid to keep this cheap
            const int maxSamples = 2000;
            int step = Math.Max(1, (int)Math.Sqrt((long)bmp.Width * bmp.Height / maxSamples));

            int dark = 0, total = 0;
            for (int y = 0; y < bmp.Height; y += step)
            for (int x = 0; x < bmp.Width;  x += step)
            {
                var px = bmp.GetPixel(x, y);
                // Perceptual luminance
                float lum = 0.299f * px.Red + 0.587f * px.Green + 0.114f * px.Blue;
                if (lum < 128f) dark++;
                total++;
            }
            return total > 0 && (double)dark / total > 0.55;
        }
        catch { return false; }
    }
}
