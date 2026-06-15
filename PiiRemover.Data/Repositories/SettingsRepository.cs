using Dapper;
using Microsoft.Data.Sqlite;

namespace PiiRemover.Data.Repositories;

public class SettingsRepository : ISettingsRepository
{
    private readonly string _cs;
    public SettingsRepository(string connectionString) => _cs = connectionString;

    private SqliteConnection Open() { var c = new SqliteConnection(_cs); c.Open(); return c; }

    public async Task<string?> GetAsync(string key)
    {
        using var conn = Open();
        return await conn.ExecuteScalarAsync<string?>(
            "SELECT Value FROM Settings WHERE Key = @key", new { key });
    }

    public async Task SetAsync(string key, string value, string? description = null)
    {
        using var conn = Open();
        await conn.ExecuteAsync(
            """
            INSERT INTO Settings (Key, Value, Description) VALUES (@key, @value, @desc)
            ON CONFLICT(Key) DO UPDATE SET Value = @value
            """,
            new { key, value, desc = description });
    }

    public async Task<IEnumerable<SettingEntry>> GetAllAsync()
    {
        using var conn = Open();
        return await conn.QueryAsync<SettingEntry>("SELECT Key, Value, Description FROM Settings ORDER BY Key");
    }
}
