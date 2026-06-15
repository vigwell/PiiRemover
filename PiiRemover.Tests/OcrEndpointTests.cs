using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using PiiRemover.Tests.Infrastructure;

namespace PiiRemover.Tests;

/// <summary>Tests for POST /api/v1/ocr/extract</summary>
public class OcrEndpointTests(PiiWebAppFactory factory) : IClassFixture<PiiWebAppFactory>
{
    private readonly HttpClient _client = factory.CreateAuthenticatedClient();
    private const string Endpoint = "/api/v1/ocr/extract";

    // ── Auth ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Extract_NoApiKey_Returns401()
    {
        var client  = factory.CreateClient(); // no key
        var content = TestHelpers.BuildFileContent(SmallTextBytes(), "test.txt", "text/plain");
        var resp    = await client.PostAsync(Endpoint, content);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Extract_WrongApiKey_Returns401()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "wrong-key-000");
        var content = TestHelpers.BuildFileContent(SmallTextBytes(), "test.txt", "text/plain");
        var resp    = await client.PostAsync(Endpoint, content);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Plain-text ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Extract_PlainTextFile_ReturnsExtractedText()
    {
        var text    = "Hello world, this is a test document.";
        var content = TestHelpers.BuildFileContent(
            System.Text.Encoding.UTF8.GetBytes(text), "sample.txt", "text/plain");

        var resp = await _client.PostAsync(Endpoint, content);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("text").GetString().Should().Contain("Hello world");
        body.GetProperty("charCount").GetInt32().Should().BeGreaterThan(0);
        body.GetProperty("durationMs").GetInt64().Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Extract_HebrewPlainText_ReturnsText()
    {
        var text = "שלום עולם. מספר זהות: 205447709. טלפון: 058-3245670.";
        var content = TestHelpers.BuildFileContent(
            System.Text.Encoding.UTF8.GetBytes(text), "hebrew.txt", "text/plain");

        var resp = await _client.PostAsync(Endpoint, content);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("text").GetString().Should().Contain("205447709");
    }

    // ── PDF ───────────────────────────────────────────────────────────────────

    [Fact(Skip = "Requires C:\\temp\\1.pdf — run manually")]
    public async Task Extract_MedicalPdf_ExtractsHebrewAndEnglishText()
    {
        const string path = @"C:\temp\1.pdf";
        if (!File.Exists(path)) return;

        var content = TestHelpers.BuildFileContent(path);
        var resp    = await _client.PostAsync(Endpoint, content);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var text = body.GetProperty("text").GetString()!;

        // Page 1 patient
        text.Should().Contain("205447709");
        text.Should().Contain("כהן");
        // Page 2 patient
        text.Should().Contain("337789168");
        // Page 3 patient
        text.Should().Contain("053561924");

        body.GetProperty("charCount").GetInt32().Should().BeGreaterThan(500);
    }

    // ── Unsupported type ──────────────────────────────────────────────────────

    [Fact]
    public async Task Extract_UnsupportedMimeType_Returns400()
    {
        var content = TestHelpers.BuildFileContent(
            new byte[] { 0x50, 0x4B }, "archive.zip", "application/zip");
        var resp = await _client.PostAsync(Endpoint, content);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static byte[] SmallTextBytes() =>
        System.Text.Encoding.UTF8.GetBytes("integration test document");
}
