using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PiiRemover.Data.Repositories;

namespace PiiRemover.Api.Pages.Admin;

[Authorize]
public class LogsModel : AdminPageModel
{
    private readonly ILogRepository _logs;

    public IEnumerable<RequestLogEntry> Logs { get; private set; } = [];
    public int Total { get; private set; }
    public int CurrentPage { get; private set; }
    public int PageCount { get; private set; }

    public LogsModel(ILogRepository logs) => _logs = logs;

    public async Task OnGetAsync(int page = 1)
    {
        CurrentPage = Math.Max(1, page);
        Total       = await _logs.CountAsync();
        PageCount   = Math.Max(1, (int)Math.Ceiling(Total / 50.0));
        Logs        = await _logs.GetRecentAsync(CurrentPage, 50);
    }
}
