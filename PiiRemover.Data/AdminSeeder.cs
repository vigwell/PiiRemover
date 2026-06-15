using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;

namespace PiiRemover.Data;

public static class AdminSeeder
{
    // Seeds admin:passwordHash = SHA256("2026") if the setting does not yet exist.
    // The hash will never be re-seeded once a value exists — changing the password
    // via the backoffice Settings page updates the row in place.
    public static void SeedAdminPassword(string connectionString, string defaultPassword = "2026")
    {
        using var conn = new SqliteConnection(connectionString);
        conn.Open();
        var hash = HashPassword(defaultPassword);
        conn.Execute(
            "INSERT OR IGNORE INTO Settings (Key, Value, Description) VALUES ('admin:passwordHash', @hash, 'Admin console password (SHA-256 hex)')",
            new { hash });
    }

    public static string HashPassword(string pw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pw))).ToLowerInvariant();
}
