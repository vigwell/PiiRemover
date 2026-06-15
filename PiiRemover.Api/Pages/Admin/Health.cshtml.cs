using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PiiRemover.Api.Pages.Admin;

[Authorize]
public class HealthModel : AdminPageModel
{
    public IActionResult OnGet() =>
        RedirectToPage("/Admin/Settings", new { tab = "health" });
}
