using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PiiRemover.Core.Models;
using PiiRemover.Data.Repositories;

namespace PiiRemover.Api.Pages.Admin;

[Authorize]
public class FieldsModel : AdminPageModel
{
    private readonly IFieldRepository _fields;

    [BindProperty] public string NewFieldName { get; set; } = string.Empty;
    [BindProperty] public string NewReplaceWith { get; set; } = "████";
    public IEnumerable<PiiField> Fields { get; private set; } = [];

    public FieldsModel(IFieldRepository fields) => _fields = fields;

    public async Task OnGetAsync() => Fields = await _fields.GetAllFieldsAsync();

    public async Task<IActionResult> OnPostCreateFieldAsync()
    {
        await _fields.CreateFieldAsync(null, NewFieldName, string.IsNullOrWhiteSpace(NewReplaceWith) ? "████" : NewReplaceWith);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleFieldAsync(int fieldId)
    {
        var all = await _fields.GetAllFieldsAsync();
        var field = all.FirstOrDefault(f => f.Id == fieldId);
        if (field is not null) await _fields.SetFieldActiveAsync(fieldId, !field.IsActive);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteFieldAsync(int fieldId)
    {
        await _fields.DeleteFieldAsync(fieldId);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCreatePatternAsync(int fieldId, string patternType, string pattern, int priority)
    {
        if (Enum.TryParse<PatternType>(patternType, out var pt))
            await _fields.CreatePatternAsync(fieldId, pt, pattern, priority);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeletePatternAsync(int patternId)
    {
        await _fields.DeletePatternAsync(patternId);
        return RedirectToPage();
    }
}
