namespace PiiRemover.Core.Models;

public class PiiField
{
    public int Id { get; set; }
    public int? ClientId { get; set; }
    public string FieldName { get; set; } = string.Empty;
    public string ReplaceWith { get; set; } = "████";
    public bool IsActive { get; set; } = true;
    public List<PiiPattern> Patterns { get; set; } = new();
}
