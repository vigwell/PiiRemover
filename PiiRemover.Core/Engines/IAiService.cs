namespace PiiRemover.Core.Engines;

/// <summary>
/// Abstraction for the local AI extraction engine.
/// Implemented by OllamaService in PiiRemover.Api to avoid a circular project reference.
/// </summary>
public interface IAiService
{
    Task<bool>         IsEnabledAsync();
    Task<List<string>> ExtractEntitiesAsync(string text, string description, CancellationToken ct = default);
    /// <summary>
    /// Extracts all <paramref name="descriptions"/> from <paramref name="text"/> in a single AI call and
    /// stores results in a short-lived request scope. Subsequent <see cref="ExtractEntitiesAsync"/> calls
    /// within the same Redact() invocation return instantly from scope without another AI round-trip.
    /// Call <see cref="ClearScope"/> when the redaction is complete.
    /// </summary>
    Task PrefetchAsync(string text, IList<string> descriptions, CancellationToken ct = default);

    /// <summary>Clears the per-request result scope. Call after each Redact() completes.</summary>
    void ClearScope();
}
