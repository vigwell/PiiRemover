using Dapper;
using Microsoft.Data.Sqlite;

namespace PiiRemover.Data.Repositories;

public class ClientRepository : IClientRepository
{
    private readonly string _cs;
    public ClientRepository(string connectionString) => _cs = connectionString;

    private SqliteConnection Open() { var c = new SqliteConnection(_cs); c.Open(); return c; }

    public async Task<ClientRecord?> GetByApiKeyHashAsync(string hash)
    {
        using var conn = Open();
        return await conn.QueryFirstOrDefaultAsync<ClientRecord>(
            "SELECT * FROM Clients WHERE ApiKeyHash = @hash AND IsActive = 1", new { hash });
    }

    public async Task<IEnumerable<ClientRecord>> GetAllAsync()
    {
        using var conn = Open();
        return await conn.QueryAsync<ClientRecord>("SELECT * FROM Clients ORDER BY Id DESC");
    }

    public async Task<int> CreateAsync(string name, string apiKeyHash)
    {
        using var conn = Open();
        return await conn.ExecuteScalarAsync<int>(
            "INSERT INTO Clients (Name, ApiKeyHash) VALUES (@name, @apiKeyHash); SELECT last_insert_rowid();",
            new { name, apiKeyHash });
    }

    public async Task SetActiveAsync(int id, bool active)
    {
        using var conn = Open();
        await conn.ExecuteAsync("UPDATE Clients SET IsActive = @v WHERE Id = @id", new { v = active ? 1 : 0, id });
    }

    public async Task UpdateApiKeyHashAsync(int id, string newHash)
    {
        using var conn = Open();
        await conn.ExecuteAsync("UPDATE Clients SET ApiKeyHash = @newHash WHERE Id = @id", new { newHash, id });
    }
}
