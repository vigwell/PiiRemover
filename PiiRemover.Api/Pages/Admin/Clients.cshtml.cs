using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PiiRemover.Api.Middleware;
using PiiRemover.Data.Repositories;

namespace PiiRemover.Api.Pages.Admin;

[Authorize]
public class ClientsModel : AdminPageModel
{
    private readonly IClientRepository _clients;

    [BindProperty] public string NewName { get; set; } = string.Empty;
    public IEnumerable<ClientRecord> Clients { get; private set; } = [];
    public string? NewApiKey { get; private set; }

    public ClientsModel(IClientRepository clients) => _clients = clients;

    public async Task OnGetAsync() => Clients = await _clients.GetAllAsync();

    public async Task<IActionResult> OnPostCreateAsync()
    {
        var rawKey = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        await _clients.CreateAsync(NewName, ApiKeyMiddleware.HashKey(rawKey));
        NewApiKey = rawKey;
        Clients = await _clients.GetAllAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostToggleAsync(int id)
    {
        var all = await _clients.GetAllAsync();
        var client = all.FirstOrDefault(c => c.Id == id);
        if (client is not null) await _clients.SetActiveAsync(id, !client.IsActive);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRegenerateAsync(int id)
    {
        var rawKey = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        await _clients.UpdateApiKeyHashAsync(id, ApiKeyMiddleware.HashKey(rawKey));
        NewApiKey = rawKey;
        Clients = await _clients.GetAllAsync();
        return Page();
    }
}
