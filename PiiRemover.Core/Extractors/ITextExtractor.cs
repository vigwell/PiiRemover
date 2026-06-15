namespace PiiRemover.Core.Extractors;

public interface ITextExtractor
{
    bool CanHandle(string mimeType);
    // Receives a temp file path — never buffers the full file in RAM.
    Task<string> ExtractAsync(string filePath, CancellationToken ct = default);
}
