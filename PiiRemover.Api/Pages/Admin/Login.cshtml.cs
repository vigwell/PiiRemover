using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PiiRemover.Data.Repositories;

namespace PiiRemover.Api.Pages.Admin;

public class LoginModel : PageModel
{
    private readonly ISettingsRepository _settings;

    [BindProperty] public string Username  { get; set; } = string.Empty;
    [BindProperty] public string Password  { get; set; } = string.Empty;
    [BindProperty] public string ReturnUrl { get; set; } = "/admin";
    public string Error { get; private set; } = string.Empty;

    public LoginModel(ISettingsRepository settings) => _settings = settings;

    public void OnGet(string? returnUrl) => ReturnUrl = returnUrl ?? "/admin";

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(Username))
        {
            Error = "Invalid username or password.";
            return Page();
        }

        var inputHash = HashPassword(Password);
        var accounts  = await AdminAccountHelper.LoadAccountsAsync(_settings);

        var match = accounts.FirstOrDefault(a =>
            string.Equals(a.Username, Username, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.PasswordHash, inputHash, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            Error = "Invalid username or password.";
            return Page();
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, match.Username)],
            CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity));

        return Redirect(string.IsNullOrEmpty(ReturnUrl) ? "/admin" : ReturnUrl);
    }

    public static string HashPassword(string pw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pw))).ToLowerInvariant();
}
