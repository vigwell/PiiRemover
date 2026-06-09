using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PiiRemover.Api.Pages.Admin;

[Authorize]
public class HealthModel : AdminPageModel
{
}
