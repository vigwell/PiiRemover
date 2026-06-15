namespace PiiRemover.Core.Engines;

/// <summary>
/// Abstraction for the local AI extraction engine.
/// Implemented by OllamaService in PiiRemover.Api to avoid a circular project reference.
/// </summary>
public interface IAiService
{
    Task<bool>         IsEnabledAsync();
    Task<List<string>> ExtractEntitiesAsync(string text, string description, CancellationToken ct = default);
}
