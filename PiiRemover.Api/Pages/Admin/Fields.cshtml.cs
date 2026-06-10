using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PiiRemover.Api.Services;
using PiiRemover.Core.Engines;
using PiiRemover.Core.Models;
using PiiRemover.Data.Repositories;

namespace PiiRemover.Api.Pages.Admin;

[Authorize]
[DisableRequestSizeLimit]
[RequestFormLimits(MultipartBodyLengthLimit = 200 * 1024 * 1024)] // 200 MB for file uploads
public class FieldsModel : AdminPageModel
{
    private readonly IFieldRepository _fields;
    private readonly FieldsCache _cache;

    [BindProperty] public string NewFieldName  { get; set; } = string.Empty;
    [BindProperty] public string NewReplaceWith { get; set; } = "████";
    public IEnumerable<PiiField> Fields { get; private set; } = [];

    public FieldsModel(IFieldRepository fields, FieldsCache cache)
    {
        _fields = fields;
        _cache  = cache;
    }

    public async Task OnGetAsync() => Fields = await _fields.GetAllFieldsAsync();

    public async Task<IActionResult> OnPostCreateFieldAsync()
    {
        await _fields.CreateFieldAsync(null, NewFieldName,
            string.IsNullOrWhiteSpace(NewReplaceWith) ? "████" : NewReplaceWith);
        _cache.Invalidate();
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleFieldAsync(int fieldId)
    {
        var all   = await _fields.GetAllFieldsAsync();
        var field = all.FirstOrDefault(f => f.Id == fieldId);
        if (field is not null) await _fields.SetFieldActiveAsync(fieldId, !field.IsActive);
        _cache.Invalidate();
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteFieldAsync(int fieldId)
    {
        await _fields.DeleteFieldAsync(fieldId);
        _cache.Invalidate();
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTogglePreserveAsync(int fieldId)
    {
        var all   = await _fields.GetAllFieldsAsync();
        var field = all.FirstOrDefault(f => f.Id == fieldId);
        if (field is not null) await _fields.SetPreserveAsync(fieldId, !field.IsPreserve);
        _cache.Invalidate();
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCreatePreserveFieldAsync(string preserveFieldName, string preserveTerms)
    {
        if (string.IsNullOrWhiteSpace(preserveFieldName)) return RedirectToPage();

        var fieldId = await _fields.CreateFieldAsync(null, preserveFieldName, "—", isPreserve: true);

        if (!string.IsNullOrWhiteSpace(preserveTerms))
            await _fields.CreatePatternAsync(fieldId, PatternType.ConstList, preserveTerms.Trim(), 999);

        _cache.Invalidate();
        TempData["Success"] = $"Preserve field '{preserveFieldName}' created.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUpdateFieldReplaceWithAsync(int fieldId, string replaceWith)
    {
        await _fields.UpdateFieldReplaceWithAsync(fieldId,
            string.IsNullOrWhiteSpace(replaceWith) ? "████" : replaceWith.Trim());
        _cache.Invalidate();
        TempData["Success"] = "Replacement text updated.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUpdatePatternAsync(int patternId, string patternValue)
    {
        var all     = await _fields.GetAllFieldsAsync();
        var pattern = all.SelectMany(f => f.Patterns).FirstOrDefault(p => p.Id == patternId);
        if (pattern is not null)
        {
            await _fields.UpdatePatternAsync(patternId, pattern.PatternType,
                                             patternValue.Trim(), pattern.Priority);
            FileListEngine.InvalidateCache(patternId);
            _cache.Invalidate();
            TempData["Success"] = "Pattern updated.";
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCreatePatternAsync(int fieldId, string patternType, string pattern, int priority)
    {
        if (Enum.TryParse<PatternType>(patternType, true, out var pt))
            await _fields.CreatePatternAsync(fieldId, pt, pattern, priority);
        _cache.Invalidate();
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeletePatternAsync(int patternId)
    {
        await _fields.DeletePatternAsync(patternId);
        _cache.Invalidate();
        return RedirectToPage();
    }

    /// <summary>
    /// Consolidates all WholeWord and ConstList patterns in a Preserve field into a
    /// single ConstList containing the distinct union of every term.
    /// Individual WholeWord patterns are removed after merging.
    /// Existing ConstList patterns are replaced by the unified one.
    /// </summary>
    public async Task<IActionResult> OnPostGroupAllAsync(int fieldId)
    {
        var all   = await _fields.GetAllFieldsAsync();
        var field = all.FirstOrDefault(f => f.Id == fieldId);
        if (field is null)
        {
            TempData["Error"] = "Field not found.";
            return RedirectToPage();
        }

        // Collect all terms from WholeWord and ConstList patterns
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var patternIdsToDelete = new List<int>();

        foreach (var p in field.Patterns)
        {
            if (p.PatternType == PatternType.WholeWord)
            {
                terms.Add(p.Pattern.Trim());
                patternIdsToDelete.Add(p.Id);
            }
            else if (p.PatternType == PatternType.ConstList)
            {
                foreach (var t in p.Pattern.Split('|', StringSplitOptions.RemoveEmptyEntries))
                    terms.Add(t.Trim());
                patternIdsToDelete.Add(p.Id);
            }
            // FileList / Regex / other patterns are left untouched
        }

        if (terms.Count == 0)
        {
            TempData["Error"] = "No WholeWord or ConstList patterns found to consolidate.";
            return RedirectToPage();
        }

        // Delete all collected patterns
        foreach (var pid in patternIdsToDelete)
            await _fields.DeletePatternAsync(pid);

        // Create one unified ConstList with all terms sorted
        var unified = string.Join("|", terms.OrderBy(t => t, StringComparer.OrdinalIgnoreCase));
        await _fields.CreatePatternAsync(fieldId, PatternType.ConstList, unified, priority: 999);

        _cache.Invalidate();
        TempData["Success"] = $"Consolidated {patternIdsToDelete.Count} patterns into 1 ConstList with {terms.Count} distinct terms.";
        return RedirectToPage();
    }

    /// <summary>
    /// Upload a .txt/.dat/.csv file and create (or replace) the FileList pattern for the given field.
    /// If fieldId == 0, a new field is created first using the uploaded file name as the field name.
    /// </summary>
    public async Task<IActionResult> OnPostUploadFileListAsync(int fieldId, string? fieldName, IFormFile? file, bool append = false)
    {
        try
        {
            if (file is null || file.Length == 0)
            {
                TempData["Error"] = "No file selected or file is empty.";
                return RedirectToPage();
            }

            string content;
            using (var reader = new StreamReader(
                       file.OpenReadStream(),
                       System.Text.Encoding.UTF8,
                       detectEncodingFromByteOrderMarks: true,
                       leaveOpen: false))
                content = await reader.ReadToEndAsync();

            var newTerms = FileListEngine.ParseFile(content);
            if (newTerms.Count == 0)
            {
                TempData["Error"] = $"No terms could be parsed from '{file.FileName}'. Check the file format.";
                return RedirectToPage();
            }

            // Create a new field if no target field was selected
            if (fieldId == 0)
            {
                var name = string.IsNullOrWhiteSpace(fieldName)
                    ? Path.GetFileNameWithoutExtension(file.FileName)
                    : fieldName;
                fieldId = await _fields.CreateFieldAsync(null, name, "████");
            }

            // Find an existing FileList pattern on this field (if any)
            var all      = await _fields.GetAllFieldsAsync();
            var field    = all.FirstOrDefault(f => f.Id == fieldId);
            var existing = field?.Patterns.FirstOrDefault(p => p.PatternType == PatternType.FileList);

            IReadOnlyList<string> finalTerms;
            if (append && existing is not null)
            {
                // Merge: existing terms + new terms, deduplicated
                var existingTerms = FileListEngine.ParseFile(existing.Pattern);
                var merged = existingTerms
                    .Concat(newTerms)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(t => t)
                    .ToList();
                finalTerms = merged;
            }
            else
            {
                finalTerms = newTerms;
            }

            var serialized = FileListEngine.Serialize(finalTerms);

            if (existing is not null)
                await _fields.UpdatePatternAsync(existing.Id, PatternType.FileList, serialized, existing.Priority);
            else
                await _fields.CreatePatternAsync(fieldId, PatternType.FileList, serialized, 0);

            FileListEngine.InvalidateAll();
            _cache.Invalidate();

            var action = (append && existing is not null) ? "Appended" : "Imported";
            TempData["Success"] = $"{action} {newTerms.Count} terms from '{file.FileName}' — field now has {finalTerms.Count} total terms.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"Import failed: {ex.Message}";
        }

        return RedirectToPage();
    }
}
