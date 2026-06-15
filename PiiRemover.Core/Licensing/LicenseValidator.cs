using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PiiRemover.Core.Licensing;

public class LicenseValidator
{
    // Public key embedded at compile time — private key stays offline with the vendor
    private const string PublicKeyPem =
        "-----BEGIN RSA PUBLIC KEY-----\n" +
        "MIIBCgKCAQEA5UUHRFNtzykeurr0/ogIb3AF85mVl9QHDaDvJk5NL8Z3obFGmUP2\n" +
        "1WUTOo7kpabXO9iPFj0NG+93T3I4fZSQuOvE6LccaULRV2WPbzf4LNDfc9hez0lP\n" +
        "Fqn0oHsZE6+pEDIHuCDT/i4SuNTotpSu5v1Zl2AEaS/6vCsHGc1laWJJkVbvsSnu\n" +
        "VdLM7sYlZwc6QGoqBDPTGYvjdo4fZeWrFtvjVHTIBehhbcpb7B5O6Io7LU2N32ec\n" +
        "Wzn76yUkyu7ukspdNMhuLRkvlgQ0QsudEFN3n395xo9RI8hM38YdLd/fhqDyj2ZN\n" +
        "VMIvjF07JfU7oxc/irb/kcaUTtn6itNnBQIDAQAB\n" +
        "-----END RSA PUBLIC KEY-----";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public LicenseInfo Validate(string licFilePath)
    {
        if (!File.Exists(licFilePath))
            throw new LicenseMissingException($"License file not found: {licFilePath}");

        var raw = File.ReadAllText(licFilePath).Trim();
        var dotIdx = raw.LastIndexOf('.');
        if (dotIdx < 0)
            throw new InvalidLicenseException("License file format invalid.");

        byte[] payload;
        byte[] sig;
        try
        {
            payload = Convert.FromBase64String(raw[..dotIdx]);
            sig = Convert.FromBase64String(raw[(dotIdx + 1)..]);
        }
        catch
        {
            throw new InvalidLicenseException("License file is corrupted.");
        }

        using var rsa = RSA.Create();
        rsa.ImportFromPem(PublicKeyPem);
        if (!rsa.VerifyData(payload, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new InvalidLicenseException("License signature is invalid. The file may have been tampered with.");

        var info = JsonSerializer.Deserialize<LicenseInfo>(Encoding.UTF8.GetString(payload), JsonOpts)
            ?? throw new InvalidLicenseException("License payload could not be parsed.");

        if (info.ExpiryDate < DateOnly.FromDateTime(DateTime.UtcNow))
            throw new LicenseExpiredException(
                $"License expired on {info.ExpiryDate:yyyy-MM-dd}. Please contact your vendor to renew.");

        return info;
    }
}
