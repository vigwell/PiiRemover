using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PiiRemover.Api.Pages.Admin;

public abstract class AdminPageModel : PageModel
{
    public async Task<IActionResult> OnPostLogoutAsync()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Redirect("/admin/login");
    }
}
