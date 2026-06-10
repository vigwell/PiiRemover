namespace PiiRemover.Core.Models;

public class PiiField
{
    public int Id { get; set; }
    public int? ClientId { get; set; }
    public string FieldName { get; set; } = string.Empty;
    public string ReplaceWith { get; set; } = "████";
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// When true, this field defines PROTECTED zones — any redaction match that
    /// overlaps with one of these patterns is suppressed regardless of priority.
    /// Think of it as a "do not touch / leave as is" whitelist.
    /// </summary>
    public bool IsPreserve { get; set; } = false;

    public List<PiiPattern> Patterns { get; set; } = new();
}
