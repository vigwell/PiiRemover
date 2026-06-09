using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using PiiRemover.Api.Extractors;
using PiiRemover.Api.Logging;
using PiiRemover.Api.Middleware;
using PiiRemover.Api.Services;
using PiiRemover.Core.Engines;
using PiiRemover.Core.Extractors;
using PiiRemover.Core.Licensing;
using PiiRemover.Core.Logging;
using PiiRemover.Data;
using PiiRemover.Data.Repositories;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// ── License ──────────────────────────────────────────────────────────────────
var licFilePath = cfg["License:FilePath"] ?? "license.lic";
LicenseInfo license;
try
{
    license = new LicenseValidator().Validate(licFilePath);
    Console.WriteLine($"License OK — Org: {license.OrgName}, Expires: {license.ExpiryDate:yyyy-MM-dd}, " +
                      $"Quota: {(license.RequestQuota == 0 ? "unlimited" : license.RequestQuota.ToString("N0"))}");
}
catch (LicenseMissingException)
{
    Console.Error.WriteLine("WARNING: No license file. All API calls will return HTTP 402.");
    license = new LicenseInfo { ExpiryDate = DateOnly.MinValue };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"LICENSE ERROR: {ex.Message}");
    license = new LicenseInfo { ExpiryDate = DateOnly.MinValue };
}
builder.Services.AddSingleton(license);

// ── SQLite ────────────────────────────────────────────────────────────────────
var dbPath = cfg["Database:Path"] ?? "piiremovals.db";
DapperConfig.Register(); // must be before any DB access

var cs = $"Data Source={dbPath};Cache=Shared";
new SchemaInitializer(cs).Initialize();
AdminSeeder.SeedAdminPassword(cs);
PiiDataSeeder.Seed(cs);

builder.Services.AddSingleton<IClientRepository>(_ => new ClientRepository(cs));
builder.Services.AddSingleton<IFieldRepository>(_ => new FieldRepository(cs));
builder.Services.AddSingleton<ILogRepository>(_ => new LogRepository(cs));
builder.Services.AddSingleton<IQuotaRepository>(_ => new QuotaRepository(cs));
builder.Services.AddSingleton<ISettingsRepository>(_ => new SettingsRepository(cs));

// ── Windows Event Log ─────────────────────────────────────────────────────────
var logModeStr   = cfg["Logging:EventLog:Mode"]       ?? "Production";
var logSourceName = cfg["Logging:EventLog:SourceName"] ?? "PiiRemover";
var logName       = cfg["Logging:EventLog:LogName"]    ?? "PiiRemover";
var logMode = Enum.TryParse<LogMode>(logModeStr, true, out var lm) ? lm : LogMode.Production;
builder.Services.AddSingleton<IPiiLogger>(_ => new WindowsEventLogger(logSourceName, logName, logMode));

// ── Background services ───────────────────────────────────────────────────────
builder.Services.AddHostedService<LogCleanupService>();

// ── Detection engine ──────────────────────────────────────────────────────────
builder.Services.AddSingleton<IPatternEngine, RegexPatternEngine>();
builder.Services.AddSingleton<IPatternEngine, ConstListEngine>();
builder.Services.AddSingleton<IPatternEngine, LlmPromptEngine>();
builder.Services.AddSingleton<RedactionOrchestrator>(sp =>
    new RedactionOrchestrator(sp.GetServices<IPatternEngine>()));

// ── OCR options + extractors ──────────────────────────────────────────────────
var ocrOpts = new OcrOptions();
cfg.GetSection("Ocr").Bind(ocrOpts);
// Resolve tessdata path relative to the exe so it works in VS (F5) and published deployments
if (!Path.IsPathRooted(ocrOpts.TessdataPath))
    ocrOpts.TessdataPath = Path.Combine(AppContext.BaseDirectory, ocrOpts.TessdataPath);
Console.WriteLine($"OCR engine order : {string.Join(" -> ", ocrOpts.EngineOrder)}");
Console.WriteLine($"OCR tessdata path: {ocrOpts.TessdataPath}");
Console.WriteLine($"OCR tessdata exists: {Directory.Exists(ocrOpts.TessdataPath)}");
builder.Services.AddSingleton(ocrOpts);

// OcrExtractor is a singleton — its SemaphoreSlim lives for the app lifetime
builder.Services.AddSingleton<OcrExtractor>();
builder.Services.AddSingleton<ITextExtractor, PlainTextExtractor>();
builder.Services.AddSingleton<ITextExtractor>(sp => new PdfTextExtractor(sp.GetRequiredService<OcrExtractor>()));
builder.Services.AddSingleton<ITextExtractor>(sp => sp.GetRequiredService<OcrExtractor>());
builder.Services.AddSingleton<ExtractorFactory>(sp =>
    new ExtractorFactory(sp.GetServices<ITextExtractor>()));

// ── ASP.NET ───────────────────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new() { Title = "PiiRemover API", Version = "v1" });
    o.AddSecurityDefinition("ApiKey", new()
    {
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Name = "X-Api-Key",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        Description = "API key for client authentication"
    });
    o.AddSecurityRequirement(new()
    {
        {
            new() { Reference = new() { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "ApiKey" } },
            []
        }
    });
});
builder.Services.AddRazorPages();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o => { o.LoginPath = "/admin/login"; o.LogoutPath = "/admin/logout"; });
builder.Services.AddAuthorization();

// Remove all body size limits — IIS limit is already removed in web.config
builder.Services.Configure<KestrelServerOptions>(o => o.Limits.MaxRequestBodySize = null);
builder.Services.Configure<IISServerOptions>(o => o.MaxRequestBodySize = null);

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(o => o.SwaggerEndpoint("/swagger/v1/swagger.json", "PiiRemover v1"));

app.UseMiddleware<LicenseMiddleware>();
app.UseMiddleware<ApiKeyMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapRazorPages();

app.Run();
