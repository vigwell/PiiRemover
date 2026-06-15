using System.Diagnostics;
using PiiRemover.Api.Extractors;
using PiiRemover.Core.Engines;
using PiiRemover.Core.Models;
using SkiaSharp;

namespace PiiRemover.Api.Services;

/// <summary>Result returned by <see cref="ImageRedactor.RedactAsync"/>.</summary>
public sealed class ImageRedactionResult
{
    /// <summary>JPEG bytes of the image with PII words painted over.</summary>
    public required byte[] RedactedImage  { get; init; }

    /// <summary>Raw OCR text extracted from the image (before redaction).</summary>
    public required string OcrText        { get; init; }

    public required int    MatchCount     { get; init; }
    public required long   DurationMs     { get; init; }
    public required string[] FieldsHit    { get; init; }

    /// <summary>Individual match details for the debug UI.</summary>
    public required IReadOnlyList<RedactMatch> Matches { get; init; }
}

/// <summary>
/// Pixel-level image redaction — two modes:
///
/// <list type="bullet">
///   <item><b>Selective (PII)</b> — OCR → run PII rules → paint only matched words.</item>
///   <item><b>All-text</b> — OCR → paint EVERY detected word regardless of content.
///     Use this for screenshots, banking apps, medical UI overlays where ALL visible
///     text must be hidden without any pattern-matching step.</item>
/// </list>
/// </summary>
public sealed class ImageRedactor
{
    // Padding added around each match rectangle so character descenders/ascenders
    // are fully covered even when the OCR bounding box clips slightly.
    private const int Pad = 5;

    private readonly OcrExtractor          _ocr;
    private readonly RedactionOrchestrator _orchestrator;

    public ImageRedactor(OcrExtractor ocr, RedactionOrchestrator orchestrator)
    {
        _ocr          = ocr;
        _orchestrator = orchestrator;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Paint ALL OCR-detected words black — no PII filtering.
    /// Ideal for screenshots (banking apps, mobile UI) where every visible text
    /// token must be obscured regardless of whether it is personal data.
    /// </summary>
    public async Task<ImageRedactionResult> RedactAllTextAsync(
        byte[] imageBytes, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // Run BOTH normal and inverted OCR passes and merge all word rects.
        // This is critical for images with mixed backgrounds (e.g. mobile banking
        // screenshots: dark outer frame + coloured header + white card content).
        // A single pass only covers one background type; merging both covers all.
        var words = await _ocr.ExtractAllWordBoundsAsync(imageBytes, ct);

        sw.Stop();

        if (words.Count == 0)
        {
            return new ImageRedactionResult
            {
                RedactedImage = imageBytes,
                OcrText       = string.Empty,
                MatchCount    = 0,
                DurationMs    = sw.ElapsedMilliseconds,
                FieldsHit     = [],
                Matches       = []
            };
        }

        // Convert every OCR word directly into a paint rectangle — no PII check
        var rects = words
            .Select(w => new SKRect(
                w.X - Pad, w.Y - Pad,
                w.X + w.Width + Pad, w.Y + w.Height + Pad))
            .ToList();

        var redactedBytes = PaintRects(imageBytes, rects);

        return new ImageRedactionResult
        {
            RedactedImage = redactedBytes,
            OcrText       = string.Empty,
            MatchCount    = words.Count,
            DurationMs    = sw.ElapsedMilliseconds,
            FieldsHit     = ["(all text)"],
            Matches       = []
        };
    }

    /// <summary>Selective PII redaction — only matched words are painted.</summary>
    public async Task<ImageRedactionResult> RedactAsync(
        byte[] imageBytes, IReadOnlyList<PiiField> fields, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // 1. OCR with word-level bounds
        var ocrResult = await _ocr.ExtractWithBoundsAsync(imageBytes, ct);

        if (string.IsNullOrWhiteSpace(ocrResult.FullText))
        {
            sw.Stop();
            return new ImageRedactionResult
            {
                RedactedImage = imageBytes,
                OcrText       = string.Empty,
                MatchCount    = 0,
                DurationMs    = sw.ElapsedMilliseconds,
                FieldsHit     = [],
                Matches       = []
            };
        }

        // 2. Text-level PII detection
        var redactResult = _orchestrator.Redact(ocrResult.FullText, fields);

        sw.Stop();

        if (redactResult.Matches.Count == 0)
        {
            return new ImageRedactionResult
            {
                RedactedImage = imageBytes,
                OcrText       = ocrResult.FullText,
                MatchCount    = 0,
                DurationMs    = sw.ElapsedMilliseconds,
                FieldsHit     = [],
                Matches       = []
            };
        }

        // 3. Map text matches → pixel rectangles
        var rects = MapMatchesToRects(ocrResult.Words, redactResult.Matches);

        // 4. Paint
        var redactedBytes = PaintRects(imageBytes, rects);

        return new ImageRedactionResult
        {
            RedactedImage = redactedBytes,
            OcrText       = ocrResult.FullText,
            MatchCount    = redactResult.Matches.Count,
            DurationMs    = sw.ElapsedMilliseconds,
            FieldsHit     = redactResult.Matches.Select(m => m.FieldName).Distinct().ToArray(),
            Matches       = redactResult.Matches
        };
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// For each <see cref="RedactMatch"/> find all OCR words whose character range
    /// overlaps the match, then return the union pixel bounding box (padded).
    /// </summary>
    private static IReadOnlyList<SKRect> MapMatchesToRects(
        IReadOnlyList<OcrWordEntry> words,
        IReadOnlyList<RedactMatch>  matches)
    {
        var rects = new List<SKRect>(matches.Count);

        foreach (var match in matches)
        {
            int matchEnd = match.StartIndex + match.Length;

            // Collect all OCR words whose char interval overlaps the match interval.
            // Overlap condition: wordStart < matchEnd  AND  wordEnd > matchStart
            float left   = float.MaxValue;
            float top    = float.MaxValue;
            float right  = float.MinValue;
            float bottom = float.MinValue;
            bool  any    = false;

            foreach (var w in words)
            {
                if (w.CharStart >= matchEnd || w.CharEnd <= match.StartIndex) continue;

                if (w.X             < left)   left   = w.X;
                if (w.Y             < top)    top    = w.Y;
                if (w.X + w.Width   > right)  right  = w.X + w.Width;
                if (w.Y + w.Height  > bottom) bottom = w.Y + w.Height;
                any = true;
            }

            if (any)
                rects.Add(new SKRect(left - Pad, top - Pad, right + Pad, bottom + Pad));
        }

        return rects;
    }

    /// <summary>
    /// Draw filled black rectangles at each <paramref name="rects"/> position
    /// on a copy of the original image and return JPEG bytes.
    /// </summary>
    private static byte[] PaintRects(byte[] imageBytes, IReadOnlyList<SKRect> rects)
    {
        using var original = SKBitmap.Decode(imageBytes)
            ?? throw new InvalidOperationException("Cannot decode image bytes.");

        using var surface = SKSurface.Create(
            new SKImageInfo(original.Width, original.Height, SKColorType.Bgra8888));

        var canvas = surface.Canvas;
        canvas.DrawBitmap(original, 0, 0);

        using var paint = new SKPaint
        {
            Color = SKColors.Black,
            Style = SKPaintStyle.Fill,
            IsAntialias = false
        };

        foreach (var rect in rects)
        {
            // Clamp to image bounds
            var clamped = new SKRect(
                Math.Max(0, rect.Left),
                Math.Max(0, rect.Top),
                Math.Min(original.Width,  rect.Right),
                Math.Min(original.Height, rect.Bottom));

            if (clamped.Width > 0 && clamped.Height > 0)
                canvas.DrawRect(clamped, paint);
        }

        using var snapshot = surface.Snapshot();
        using var data     = snapshot.Encode(SKEncodedImageFormat.Jpeg, 92);
        return data.ToArray();
    }
}
