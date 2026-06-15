using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using PiiRemover.Tests.Infrastructure;

namespace PiiRemover.Tests;

/// <summary>
/// Integration tests against 5 real Hebrew/English medical documents in PiiTestDocs/.
/// Documents are committed to the repo — no manual setup required.
/// OCR relies on Windows.Media.Ocr (built-in on Windows 10+).
/// </summary>
public class RealDocumentTests(PiiWebAppFactory factory) : IClassFixture<PiiWebAppFactory>
{
    private readonly HttpClient _client = factory.CreateAuthenticatedClient();

    // ── path helper ───────────────────────────────────────────────────────────

    private static string DocsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "PiiTestDocs");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "PiiTestDocs folder not found. Ensure solution is checked out from git.");
    }

    private static string Doc(string name) => Path.Combine(DocsDir(), name);

    // ── helpers ───────────────────────────────────────────────────────────────

    private async Task<string> OcrAsync(string filePath)
    {
        var resp = await _client.PostAsync("/api/v1/ocr/extract",
            TestHelpers.BuildFileContent(filePath));
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"OCR failed for {Path.GetFileName(filePath)}: {await resp.Content.ReadAsStringAsync()}");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("text").GetString() ?? string.Empty;
    }

    private async Task<(string redacted, int matchCount, string[] fieldsHit)> RedactAsync(string filePath)
    {
        var resp = await _client.PostAsync("/api/v1/redact/redact",
            TestHelpers.BuildFileContent(filePath));
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"Redact failed for {Path.GetFileName(filePath)}: {await resp.Content.ReadAsStringAsync()}");
        var body       = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var redacted   = body.GetProperty("redactedText").GetString() ?? string.Empty;
        var matchCount = body.GetProperty("matchCount").GetInt32();
        var fieldsHit  = body.GetProperty("fieldsHit").EnumerateArray()
                             .Select(e => e.GetString() ?? "").ToArray();
        return (redacted, matchCount, fieldsHit);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Doc 1 — 1.pdf  (2-page MRI referral, Hadassah Jerusalem)
    // Ref: RS 1016138   Patient: קרוש, מריה  (patient ID is under redaction bar)
    // Contains: referral number 35548031817, dates 04/05/2023, 13/07/2023, 22/02/2024
    // Note: patient ID is physically redacted in the scan — OCR cannot extract it
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Doc1_Pdf_OcrExtractsMedicalText()
    {
        var text = await OcrAsync(Doc("1.pdf"));

        text.Should().NotBeNullOrWhiteSpace();
        text.Length.Should().BeGreaterThan(100, "PDF has 2 pages of dense text");

        // Known stable content that OCR reliably extracts from this scan
        var hasKnownContent = text.Contains("VERTIGO") ||
                              text.Contains("MRI") ||
                              text.Contains("35548031817") ||
                              text.Contains("2024") ||
                              text.Contains("2023");
        hasKnownContent.Should().BeTrue(
            $"Expected known medical content in OCR output. Got:\n{text[..Math.Min(400, text.Length)]}");
    }

    [Fact]
    public async Task Doc1_Pdf_RedactsDatesAndNumbers()
    {
        var (redacted, matchCount, fieldsHit) = await RedactAsync(Doc("1.pdf"));

        // The scan contains visible dates — these must be redacted
        // (at least some of: 22/02/2024, 04/05/2023, 13/07/2023)
        var remainingDates = new[] { "22/02/2024", "04/05/2023", "13/07/2023" }
                                 .Count(d => redacted.Contains(d));
        remainingDates.Should().Be(0, "all dates in the document must be redacted");

        matchCount.Should().BeGreaterThan(0,
            "document contains dates and numbers that should match PII patterns");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Doc 2 — 2.jpg  (Shaare Zedek visit summary — לילייינטל, אורה)
    // Patient ID: 053561924   DOB: 20/08/1955   Phone: 02-6555999
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Doc2_Jpg_OcrExtractsPatientId()
    {
        var text = await OcrAsync(Doc("2.jpg"));

        text.Should().NotBeNullOrWhiteSpace();
        var found = text.Contains("053561924") || text.Contains("20/08/1955")
                                               || text.Contains("6555999");
        found.Should().BeTrue(
            $"Expected patient data (053561924 / 20/08/1955 / 6555999). Got:\n{text[..Math.Min(600, text.Length)]}");
    }

    [Fact]
    public async Task Doc2_Jpg_RedactsPatientPii()
    {
        var (redacted, matchCount, _) = await RedactAsync(Doc("2.jpg"));

        redacted.Should().NotContain("053561924", "patient ID must be redacted");
        redacted.Should().NotContain("20/08/1955", "DOB must be redacted");
        matchCount.Should().BeGreaterThan(0);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Doc 3 — 3.jpg  (Maccabi neurology referral — הורוויץ, מלכה)
    // Patient ID: 337789168   DOB: 17/09/2007   Phone: (internal, redacted in scan)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Doc3_Jpg_OcrExtractsPatientId()
    {
        var text = await OcrAsync(Doc("3.jpg"));

        text.Should().NotBeNullOrWhiteSpace();
        var found = text.Contains("337789168") || text.Contains("17/09/2007");
        found.Should().BeTrue(
            $"Expected 337789168 or 17/09/2007. Got:\n{text[..Math.Min(600, text.Length)]}");
    }

    [Fact]
    public async Task Doc3_Jpg_RedactsIdAndDob()
    {
        var (redacted, matchCount, _) = await RedactAsync(Doc("3.jpg"));

        redacted.Should().NotContain("337789168", "patient ID must be redacted");
        redacted.Should().NotContain("17/09/2007", "DOB must be redacted");
        matchCount.Should().BeGreaterThan(0);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Doc 4 — 4.jpg  (Shaare Zedek ENT summary — כהן, שאול)
    // Patient ID: 205447709   DOB: 30/11/1994   Phone: 058-3245670
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Doc4_Jpg_OcrExtractsPatientId()
    {
        var text = await OcrAsync(Doc("4.jpg"));

        text.Should().NotBeNullOrWhiteSpace();
        text.Should().Contain("205447709",
            $"Patient ID 205447709 not found. Extracted:\n{text[..Math.Min(600, text.Length)]}");
    }

    [Fact]
    public async Task Doc4_Jpg_RedactsIdAndDob()
    {
        var (redacted, matchCount, fieldsHit) = await RedactAsync(Doc("4.jpg"));

        redacted.Should().NotContain("205447709", "patient ID must be redacted");
        redacted.Should().NotContain("30/11/1994", "DOB must be redacted");

        matchCount.Should().BeGreaterThanOrEqualTo(2,
            "should redact at minimum: patient ID + DOB");
        fieldsHit.Should().Contain(f => f.Contains("ID") || f.Contains("ת.ז"),
            "Israeli ID field should be hit");
        fieldsHit.Should().Contain(f => f.Contains("Birth") || f.Contains("לידה"),
            "DOB field should be hit");
    }

    [Fact]
    public async Task Doc4_Jpg_PhoneIsRedactedIfExtracted()
    {
        // Phone number OCR accuracy varies with scan quality;
        // this test only checks it's gone IF it was extracted
        var text              = await OcrAsync(Doc("4.jpg"));
        var (redacted, _, __) = await RedactAsync(Doc("4.jpg"));

        if (text.Contains("058") || text.Contains("3245670"))
            redacted.Should().NotContain("058-3245670",
                "phone extracted by OCR must be redacted");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Doc 5 — 5.jpg  (MRI follow-up referral — סרקאי, פנינה)
    // Patient ID: 080082167   DOB: 08/11/1961
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Doc5_Jpg_OcrExtractsPatientId()
    {
        var text = await OcrAsync(Doc("5.jpg"));

        text.Should().NotBeNullOrWhiteSpace();
        var found = text.Contains("080082167") || text.Contains("08/11/1961");
        found.Should().BeTrue(
            $"Expected 080082167 or 08/11/1961. Got:\n{text[..Math.Min(600, text.Length)]}");
    }

    [Fact]
    public async Task Doc5_Jpg_RedactsIdAndDob()
    {
        var (redacted, matchCount, _) = await RedactAsync(Doc("5.jpg"));

        redacted.Should().NotContain("080082167", "patient ID must be redacted");
        redacted.Should().NotContain("08/11/1961", "DOB must be redacted");
        matchCount.Should().BeGreaterThan(0);
    }
}
