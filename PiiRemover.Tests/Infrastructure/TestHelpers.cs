using System.Net.Http.Headers;

namespace PiiRemover.Tests.Infrastructure;

public static class TestHelpers
{
    public static HttpClient CreateAuthenticatedClient(this PiiWebAppFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", PiiWebAppFactory.DemoApiKey);
        return client;
    }

    public static MultipartFormDataContent BuildFileContent(string filePath, string? mimeType = null)
    {
        var bytes    = File.ReadAllBytes(filePath);
        var fileName = Path.GetFileName(filePath);
        var mime     = mimeType ?? GuessMime(fileName);

        var content = new MultipartFormDataContent();
        var part    = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(mime);
        content.Add(part, "file", fileName);
        return content;
    }

    public static MultipartFormDataContent BuildFileContent(byte[] bytes, string fileName, string mimeType)
    {
        var content = new MultipartFormDataContent();
        var part    = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        content.Add(part, "file", fileName);
        return content;
    }

    private static string GuessMime(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".pdf"  => "application/pdf",
        ".png"  => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".tiff" or ".tif" => "image/tiff",
        ".txt"  => "text/plain",
        _       => "application/octet-stream"
    };
}
