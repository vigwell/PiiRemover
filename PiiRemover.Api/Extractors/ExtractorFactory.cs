using PiiRemover.Core.Extractors;

namespace PiiRemover.Api.Extractors;

public class ExtractorFactory
{
    private readonly IReadOnlyList<ITextExtractor> _extractors;

    public ExtractorFactory(IEnumerable<ITextExtractor> extractors)
    {
        _extractors = extractors.ToList();
    }

    public ITextExtractor GetExtractor(string mimeType)
    {
        return _extractors.FirstOrDefault(e => e.CanHandle(mimeType))
            ?? throw new NotSupportedException($"No extractor available for MIME type '{mimeType}'.");
    }
}
