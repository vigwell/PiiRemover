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

            // 1. Text layer — RTL-aware: Hebrew/Arabic words are character-reversed and
            //    word order within RTL lines is corrected (visual LTR → logical RTL).
            var textLayer = BuildRtlAwarePage(page.GetWords().ToList());
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

    // ── RTL normalisation ─────────────────────────────────────────────────────

    private static string BuildRtlAwarePage(List<Word> words)
    {
        if (words.Count == 0) return string.Empty;

        const double lineTolerance = 3.0;
        var lines = words
            .GroupBy(w => Math.Round(w.BoundingBox.Bottom / lineTolerance) * lineTolerance)
            .OrderByDescending(g => g.Key); // page top → bottom

        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            var lineWords = line.ToList();
            bool rtlLine = lineWords.Any(w => ContainsRtl(w.Text));

            var ordered = rtlLine
                ? lineWords.OrderByDescending(w => w.BoundingBox.Left)  // RTL: rightmost word first
                : lineWords.OrderBy(w => w.BoundingBox.Left);            // LTR: leftmost word first

            foreach (var word in ordered)
                sb.Append(rtlLine ? FixRtlWord(word.Text) : word.Text).Append(' ');

            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static bool ContainsRtl(string text) =>
        text.Any(c => c is (>= '֐' and <= '׿')    // Hebrew block
                           or (>= 'יִ' and <= 'ﭏ') // Hebrew presentation forms
                           or (>= '؀' and <= 'ۿ')); // Arabic

    private static string FixRtlWord(string word) =>
        ContainsRtl(word) ? new string(word.Reverse().ToArray()) : word;

    // ─────────────────────────────────────────────────────────────────────────

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
