using System.Text;

namespace PiiRemover.Core.Extractors;

public class PlainTextExtractor : ITextExtractor
{
    public bool CanHandle(string mimeType) =>
        mimeType.StartsWith("text/", StringComparison.OrdinalIgnoreCase);

    public async Task<string> ExtractAsync(string filePath, CancellationToken ct = default)
    {
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 65536, useAsync: true);
        using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(ct);
    }
}
