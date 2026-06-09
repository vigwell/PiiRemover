using Dapper;
using Microsoft.Data.Sqlite;

namespace PiiRemover.Data.Repositories;

public interface IQuotaRepository
{
    Task<long> GetUsedAsync();
    Task<long> IncrementAsync();
}

public class QuotaRepository : IQuotaRepository
{
    private readonly string _cs;
    public QuotaRepository(string connectionString) => _cs = connectionString;

    private SqliteConnection Open() { var c = new SqliteConnection(_cs); c.Open(); return c; }

    public async Task<long> GetUsedAsync()
    {
        using var conn = Open();
        return await conn.ExecuteScalarAsync<long>(
            "SELECT COALESCE(CAST(Value AS INTEGER), 0) FROM Settings WHERE Key = 'quota:used'");
    }

    public async Task<long> IncrementAsync()
    {
        using var conn = Open();
        await conn.ExecuteAsync(
            """
            INSERT INTO Settings (Key, Value, Description)
            VALUES ('quota:used', '1', 'Total API requests served')
            ON CONFLICT(Key) DO UPDATE SET Value = CAST(CAST(Value AS INTEGER) + 1 AS TEXT)
            """);
        return await conn.ExecuteScalarAsync<long>(
            "SELECT CAST(Value AS INTEGER) FROM Settings WHERE Key = 'quota:used'");
    }
}
