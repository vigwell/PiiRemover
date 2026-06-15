using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using PiiRemover.Tests.Infrastructure;

namespace PiiRemover.Tests;

/// <summary>Tests for POST /api/v1/redact/redact</summary>
public class RedactEndpointTests(PiiWebAppFactory factory) : IClassFixture<PiiWebAppFactory>
{
    private readonly HttpClient _client = factory.CreateAuthenticatedClient();
    private const string Endpoint = "/api/v1/redact/redact";

    // ── Auth ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Redact_NoApiKey_Returns401()
    {
        var client = factory.CreateClient();
        var content = MakeText("test");
        var resp = await client.PostAsync(Endpoint, content);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Email redaction ───────────────────────────────────────────────────────

    [Fact]
    public async Task Redact_EmailInText_IsRedacted()
    {
        var resp = await _client.PostAsync(Endpoint, MakeText(
            "Please contact support@example.com for help."));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var redacted = body.GetProperty("redactedText").GetString()!;
        redacted.Should().NotContain("support@example.com");
        redacted.Should().Contain("[email]");
        body.GetProperty("matchCount").GetInt32().Should().Be(1);
    }

    // ── Israeli ID ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Redact_IsraeliId_IsRedacted()
    {
        var resp = await _client.PostAsync(Endpoint, MakeText(
            "מספר זהות: 205447709 של המטופל."));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var redacted = body.GetProperty("redactedText").GetString()!;
        redacted.Should().NotContain("205447709");
        redacted.Should().Contain("[ID]");
    }

    // ── Israeli phone ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Redact_IsraeliMobilePhone_IsRedacted()
    {
        var resp = await _client.PostAsync(Endpoint, MakeText(
            "Call me on 058-3245670 or 052-1234567."));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var redacted = body.GetProperty("redactedText").GetString()!;
        redacted.Should().NotContain("058-3245670");
        redacted.Should().NotContain("052-1234567");
        redacted.Should().Contain("[PHONE]");
    }

    // ── Date of birth ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Redact_DateOfBirth_IsRedacted()
    {
        var resp = await _client.PostAsync(Endpoint, MakeText(
            "תאריך לידה: 30/11/1994, גיל 29."));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("redactedText").GetString()!.Should().NotContain("30/11/1994");
        body.GetProperty("redactedText").GetString()!.Should().Contain("[DOB]");
    }

    // ── Credit card ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Redact_VisaCreditCard_IsRedacted()
    {
        var resp = await _client.PostAsync(Endpoint, MakeText(
            "Card: 4111 1111 1111 1111 expires 12/26."));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("redactedText").GetString()!.Should().NotContain("4111 1111 1111 1111");
        body.GetProperty("redactedText").GetString()!.Should().Contain("[CARD]");
    }

    // ── Hebrew name keyword ───────────────────────────────────────────────────

    [Fact]
    public async Task Redact_HebrewFirstName_IsRedacted()
    {
        var resp = await _client.PostAsync(Endpoint, MakeText(
            "המטופל שרה מגיעה לביקורת."));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("redactedText").GetString()!.Should().NotContain("שרה");
        body.GetProperty("matchCount").GetInt32().Should().BeGreaterThan(0);
    }

    // ── Multiple fields in one text ───────────────────────────────────────────

    [Fact]
    public async Task Redact_MultipleFieldsInText_AllRedacted()
    {
        var input =
            "Patient: כהן, שאול  ID: 205447709  " +
            "DOB: 30/11/1994  Phone: 058-3245670  " +
            "Email: shaul.cohen@mail.co.il";

        var resp = await _client.PostAsync(Endpoint, MakeText(input));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body     = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var redacted = body.GetProperty("redactedText").GetString()!;
        var hits     = body.GetProperty("fieldsHit").EnumerateArray()
                           .Select(e => e.GetString()).ToList();

        redacted.Should().NotContain("205447709");
        redacted.Should().NotContain("30/11/1994");
        redacted.Should().NotContain("058-3245670");
        redacted.Should().NotContain("shaul.cohen@mail.co.il");

        body.GetProperty("matchCount").GetInt32().Should().BeGreaterThanOrEqualTo(4);
        hits.Should().Contain("Email Address");
        hits.Should().Contain("Israeli ID (ת.ז.)");
        hits.Should().Contain("Date of Birth (תאריך לידה)");
        hits.Should().Contain("Israeli Phone (טלפון)");
    }

    // ── Clean text — no PII ───────────────────────────────────────────────────

    [Fact]
    public async Task Redact_NoPiiInText_Returns0Matches()
    {
        // Short words only — avoids the BIC/SWIFT 8-char false positive
        var resp = await _client.PostAsync(Endpoint, MakeText("No PII in this text."));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("matchCount").GetInt32().Should().Be(0);
    }

    // ── PDF — PiiTestDocs/1.pdf (2-page Hadassah MRI referral) ───────────────

    [Fact]
    public async Task Redact_MedicalPdf_RedactsPatientData()
    {
        var path = Doc("1.pdf");

        var content = TestHelpers.BuildFileContent(path);
        var resp    = await _client.PostAsync(Endpoint, content);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body     = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var redacted = body.GetProperty("redactedText").GetString()!;

        // Visible dates in the scan must be redacted
        redacted.Should().NotContain("22/02/2024");
        redacted.Should().NotContain("04/05/2023");
        redacted.Should().NotContain("13/07/2023");

        body.GetProperty("matchCount").GetInt32().Should().BeGreaterThan(0);
    }

    // ── Hash utility ──────────────────────────────────────────────────────────

    [Fact]
    public async Task HashEndpoint_ReturnsCorrectSha256()
    {
        var noAuthClient = factory.CreateClient(); // util needs no key
        var resp = await noAuthClient.PostAsJsonAsync("/api/v1/util/hash", new { value = "2026" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        // SHA-256("2026") = 65b43f2bcf246a62f2fdce0dad61aba0ec64f5e5b1e8c9bbb9a7bf44a60bc87d
        body.GetProperty("hash").GetString()!.Length.Should().Be(64);
        body.GetProperty("hash").GetString()!.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string DocsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "PiiTestDocs");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("PiiTestDocs folder not found.");
    }

    private static string Doc(string name) => Path.Combine(DocsDir(), name);

    private static MultipartFormDataContent MakeText(string text)
        => TestHelpers.BuildFileContent(
               Encoding.UTF8.GetBytes(text), "input.txt", "text/plain");
}
