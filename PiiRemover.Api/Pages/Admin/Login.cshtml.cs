using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.Sqlite;

namespace PiiRemover.Api.Pages.Admin;

public class LoginModel : PageModel
{
    private readonly IConfiguration _config;

    [BindProperty] public string Username { get; set; } = string.Empty;
    [BindProperty] public string Password { get; set; } = string.Empty;
    [BindProperty] public string ReturnUrl { get; set; } = "/admin";
    public string Error { get; private set; } = string.Empty;

    public LoginModel(IConfiguration config) => _config = config;

    public void OnGet(string? returnUrl) => ReturnUrl = returnUrl ?? "/admin";

    public async Task<IActionResult> OnPostAsync()
    {
        if (!string.Equals(Username, "admin", StringComparison.OrdinalIgnoreCase))
        {
            Error = "Invalid username or password.";
            return Page();
        }

        var cs = ConnectionString();
        using var conn = new SqliteConnection(cs);
        var storedHash = await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT Value FROM Settings WHERE Key = 'admin:passwordHash'");

        if (storedHash is null || !storedHash.Equals(HashPassword(Password), StringComparison.OrdinalIgnoreCase))
        {
            Error = "Invalid username or password.";
            return Page();
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "admin")],
            CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity));

        return Redirect(string.IsNullOrEmpty(ReturnUrl) ? "/admin" : ReturnUrl);
    }

    public static string HashPassword(string pw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pw))).ToLowerInvariant();

    private string ConnectionString() =>
        $"Data Source={_config["Database:Path"] ?? "piiremovals.db"}";
}
