using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiiRemover.Core.Licensing;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

return args[0].ToLowerInvariant() switch
{
    "generate" => Generate(args[1..]),
    "verify"   => Verify(args[1..]),
    "keygen"   => KeyGen(args[1..]),
    _          => UnknownCommand()
};

static int Generate(string[] args)
{
    string? org = null, expiry = null, keyFile = null, outFile = "license.lic";
    int maxClients = 999;
    long requestQuota = 0;
    var features = new List<string> { "ocr", "pdf", "api" };

    for (int i = 0; i < args.Length - 1; i++)
    {
        switch (args[i])
        {
            case "--org":           org          = args[++i]; break;
            case "--expiry":        expiry       = args[++i]; break;
            case "--max-clients":   maxClients   = int.Parse(args[++i]); break;
            case "--quota":         requestQuota = long.Parse(args[++i]); break;
            case "--key":           keyFile      = args[++i]; break;
            case "--out":           outFile      = args[++i]; break;
            case "--features":      features     = args[++i].Split(',').ToList(); break;
        }
    }

    if (org is null || expiry is null || keyFile is null)
    {
        Console.Error.WriteLine("generate requires --org, --expiry, --key");
        return 1;
    }

    var info = new LicenseInfo
    {
        OrgName       = org,
        LicenseId     = $"lic-{Guid.NewGuid():N}".Substring(0, 12),
        IssuedDate    = DateOnly.FromDateTime(DateTime.UtcNow),
        ExpiryDate    = DateOnly.Parse(expiry),
        MaxClients    = maxClients,
        RequestQuota  = requestQuota,
        Features      = features
    };

    var payload = Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = false }));

    using var rsa = RSA.Create();
    rsa.ImportFromPem(File.ReadAllText(keyFile));
    var sig = rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

    File.WriteAllText(outFile, Convert.ToBase64String(payload) + "." + Convert.ToBase64String(sig));

    Console.WriteLine($"License generated: {outFile}");
    Console.WriteLine($"  Org:     {info.OrgName}");
    Console.WriteLine($"  Expires: {info.ExpiryDate:yyyy-MM-dd}");
    Console.WriteLine($"  Quota:   {(info.RequestQuota == 0 ? "unlimited" : info.RequestQuota.ToString("N0"))} requests");
    Console.WriteLine($"  Id:      {info.LicenseId}");
    return 0;
}

static int Verify(string[] args)
{
    string? file = null;
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == "--file") file = args[++i];

    if (file is null) { Console.Error.WriteLine("verify requires --file"); return 1; }

    try
    {
        var info = new LicenseValidator().Validate(file);
        Console.WriteLine("License VALID");
        Console.WriteLine($"  Org:      {info.OrgName}");
        Console.WriteLine($"  Expires:  {info.ExpiryDate:yyyy-MM-dd}");
        Console.WriteLine($"  Clients:  {info.MaxClients}");
        Console.WriteLine($"  Features: {string.Join(", ", info.Features)}");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"License INVALID: {ex.Message}");
        return 1;
    }
}

static int KeyGen(string[] args)
{
    string outDir = ".";
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == "--out") outDir = args[++i];

    using var rsa = RSA.Create(2048);
    File.WriteAllText(Path.Combine(outDir, "private.pem"), rsa.ExportRSAPrivateKeyPem());
    File.WriteAllText(Path.Combine(outDir, "public.pem"),  rsa.ExportRSAPublicKeyPem());
    Console.WriteLine($"Keys written to {outDir}");
    Console.WriteLine("IMPORTANT: Keep private.pem secret — never commit it.");
    return 0;
}

static int UnknownCommand() { PrintUsage(); return 1; }

static void PrintUsage()
{
    Console.WriteLine("""
        PiiRemover License Tool

        Commands:
          keygen   --out <dir>
              Generate a new RSA key pair. Embed public.pem content in LicenseValidator.

          generate --org <name> --expiry <yyyy-MM-dd> --key <private.pem>
                   [--max-clients N] [--features ocr,pdf,api] [--out license.lic]
              Generate a signed license file for a customer.

          verify   --file <license.lic>
              Verify a license file against the embedded public key.
        """);
}
