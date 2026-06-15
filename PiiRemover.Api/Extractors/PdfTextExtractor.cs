using System.Text;
using PDFtoImage;
using PiiRemover.Core.Extractors;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace PiiRemover.Api.Extractors;

// Per-page extraction strategy:
//   1. PdfPig text layer — zero OCR cost, instant.
//   2. If page has <MinTextChars of text → rasterize with PDFtoImage at 200 DPI → OCR.
//   3. Each embedded image in the page → OCR separately.
//
// Memory model:
//   - Opens the file twice (PdfPig + PDFtoImage) but never loads the whole PDF into a MemoryStream.
//   - Image bytes from embedded images are loaded one at a time and released immediately after OCR.
//   - Rasterized page images are produced one at a time and GC'd before the next page.
public class PdfTextExtractor : ITextExtractor
{
    private const int MinTextCharsToSkipOcr = 20;

    private readonly OcrExtractor _ocr;

    public PdfTextExtractor(OcrExtractor ocr) => _ocr = ocr;

    public bool CanHandle(string mimeType) =>
        mimeType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase);

    public async Task<string> ExtractAsync(string filePath, CancellationToken ct = default)
    {
        var sb = new StringBuilder();

        // Open PdfPig directly from file — no MemoryStream, no byte[] copy
        await using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 65536, useAsync: false);
        using var doc = PdfDocument.Open(fileStream);

        int pageIndex = 0;
        foreach (var page in doc.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            sb.AppendLine($"[Page {page.Number}]");

            // 1. Text layer
            var textSb = new StringBuilder();
            foreach (Word word in page.GetWords())
                textSb.Append(word.Text).Append(' ');
            var textLayer = textSb.ToString().Trim();
            if (!string.IsNullOrEmpty(textLayer))
                sb.AppendLine(textLayer);

            // 2. Rasterize whole page if it has little or no text (scanned page)
            if (textLayer.Length < MinTextCharsToSkipOcr)
            {
                var pageText = await RasterizeAndOcrAsync(filePath, pageIndex, ct);
                if (!string.IsNullOrWhiteSpace(pageText))
                    sb.AppendLine(pageText.Trim());
            }

            // 3. OCR embedded images one at a time — bytes discarded after each call
            foreach (var pdfImage in page.GetImages())
            {
                ct.ThrowIfCancellationRequested();
                if (!pdfImage.TryGetPng(out var pngBytes) || pngBytes is null || pngBytes.Length < 1024)
                    continue;

                // Pass bytes directly into the OCR engine — no temp file, no disk I/O
                var imgText = await _ocr.ExtractFromBytesAsync(pngBytes, ct);
                if (!string.IsNullOrWhiteSpace(imgText))
                    sb.AppendLine(imgText.Trim());

                // pngBytes goes out of scope here → eligible for GC
            }

            pageIndex++;
        }

        return sb.ToString();
    }

    private async Task<string> RasterizeAndOcrAsync(string filePath, int pageIndex, CancellationToken ct)
    {
        byte[]? pageImage = null;
        try
        {
            // Open a fresh FileStream for PDFtoImage — independent of the PdfPig stream above
            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 65536, useAsync: false);

            using var bitmap = Conversion.ToImage(fs, pageIndex, leaveOpen: false,
                password: null, new RenderOptions(Dpi: 200));
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            pageImage = data.ToArray();
        }
        catch { return string.Empty; }

        if (pageImage is null) return string.Empty;

        var result = await _ocr.ExtractFromBytesAsync(pageImage, ct);
        pageImage = null; // release before next page
        return result;
    }
}
