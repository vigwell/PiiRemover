using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PiiRemover.Api.Services;

namespace PiiRemover.Api.Pages.Admin;

[Authorize]
[DisableRequestSizeLimit]
[RequestFormLimits(MultipartBodyLengthLimit = 200 * 1024 * 1024)]
public class ImageRedactorModel : AdminPageModel
{
    private readonly ImageRedactor _redactor;

    public ImageRedactorModel(ImageRedactor redactor)
    {
        _redactor = redactor;
    }

    public void OnGet() { }

    /// <summary>
    /// POST ?handler=AnalyzeImage
    /// Runs OCR on the uploaded image and paints black rectangles over every detected
    /// word — no PII rule filtering. Returns JSON with before/after base64 images.
    /// </summary>
    public async Task<IActionResult> OnPostAnalyzeImageAsync(
        IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "No file provided." });

        var mime = (file.ContentType ?? string.Empty).ToLowerInvariant();
        if (!mime.StartsWith("image/"))
            return BadRequest(new { error = $"Unsupported file type: {file.ContentType}. Upload a PNG, JPEG, TIFF, or BMP image." });

        try
        {
            var imageBytes = new byte[file.Length];
            using var ms   = new MemoryStream(imageBytes);
            await file.CopyToAsync(ms, ct);

            var result = await _redactor.RedactAllTextAsync(imageBytes, ct);

            return new JsonResult(new
            {
                ok             = true,
                fileName       = file.FileName,
                fileSize       = file.Length,
                originalBase64 = Convert.ToBase64String(imageBytes),
                redactedBase64 = Convert.ToBase64String(result.RedactedImage),
                mimeType       = "image/jpeg",
                ocrText        = result.OcrText,
                matchCount     = result.MatchCount,
                durationMs     = result.DurationMs,
                fieldsHit      = result.FieldsHit,
                matches        = Array.Empty<object>()
            });
        }
        catch (OperationCanceledException) { return StatusCode(499); }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = "Image redaction failed.", detail = ex.Message });
        }
    }
}
