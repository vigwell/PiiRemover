using Dapper;
using Microsoft.Data.Sqlite;
using PiiRemover.Core.Models;

namespace PiiRemover.Data.Repositories;

public class FieldRepository : IFieldRepository
{
    private readonly string _cs;
    public FieldRepository(string connectionString) => _cs = connectionString;

    private SqliteConnection Open() { var c = new SqliteConnection(_cs); c.Open(); return c; }

    public async Task<IEnumerable<PiiField>> GetFieldsWithPatternsAsync(int? clientId)
    {
        using var conn = Open();
        var fields = (await conn.QueryAsync<PiiField>(
            "SELECT * FROM PiiFields WHERE IsActive = 1 AND (ClientId IS NULL OR ClientId = @clientId)",
            new { clientId })).ToList();

        if (fields.Count == 0) return fields;

        var fieldIds = fields.Select(f => f.Id).ToList();
        var patterns = await conn.QueryAsync<PiiPattern>(
            $"SELECT * FROM PiiPatterns WHERE FieldId IN ({string.Join(",", fieldIds)})");

        var lookup = patterns.GroupBy(p => p.FieldId)
                             .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var f in fields)
            f.Patterns = lookup.TryGetValue(f.Id, out var ps) ? ps : new();

        return fields;
    }

    public async Task<IEnumerable<PiiField>> GetAllFieldsAsync()
    {
        using var conn = Open();
        var fields = (await conn.QueryAsync<PiiField>(
            "SELECT * FROM PiiFields ORDER BY Priority DESC, FieldName")).ToList();

        if (fields.Count == 0) return fields;

        var patterns = await conn.QueryAsync<PiiPattern>(
            $"SELECT * FROM PiiPatterns WHERE FieldId IN ({string.Join(",", fields.Select(f => f.Id))}) ORDER BY Priority DESC");

        var lookup = patterns.GroupBy(p => p.FieldId)
                             .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var f in fields)
            f.Patterns = lookup.TryGetValue(f.Id, out var ps) ? ps : [];

        return fields;
    }

    public async Task<int> CreateFieldAsync(int? clientId, string fieldName, string replaceWith, bool isPreserve = false, int priority = 500)
    {
        using var conn = Open();
        return await conn.ExecuteScalarAsync<int>(
            "INSERT INTO PiiFields (ClientId, FieldName, ReplaceWith, IsPreserve, Priority) VALUES (@clientId, @fieldName, @replaceWith, @isPreserve, @priority); SELECT last_insert_rowid();",
            new { clientId, fieldName, replaceWith, isPreserve = isPreserve ? 1 : 0, priority });
    }

    public async Task SetPreserveAsync(int fieldId, bool isPreserve)
    {
        using var conn = Open();
        await conn.ExecuteAsync("UPDATE PiiFields SET IsPreserve = @v WHERE Id = @fieldId",
            new { v = isPreserve ? 1 : 0, fieldId });
    }

    public async Task SetFieldActiveAsync(int fieldId, bool active)
    {
        using var conn = Open();
        await conn.ExecuteAsync("UPDATE PiiFields SET IsActive = @v WHERE Id = @fieldId", new { v = active ? 1 : 0, fieldId });
    }

    public async Task UpdateFieldReplaceWithAsync(int fieldId, string replaceWith)
    {
        using var conn = Open();
        await conn.ExecuteAsync("UPDATE PiiFields SET ReplaceWith = @replaceWith WHERE Id = @fieldId",
            new { fieldId, replaceWith });
    }

    public async Task UpdateFieldPriorityAsync(int fieldId, int priority)
    {
        using var conn = Open();
        await conn.ExecuteAsync("UPDATE PiiFields SET Priority = @priority WHERE Id = @fieldId",
            new { fieldId, priority });
    }

    public async Task DeleteFieldAsync(int fieldId)
    {
        using var conn = Open();
        await conn.ExecuteAsync("DELETE FROM PiiPatterns WHERE FieldId = @fieldId", new { fieldId });
        await conn.ExecuteAsync("DELETE FROM PiiFields WHERE Id = @fieldId", new { fieldId });
    }

    public async Task<int> CreatePatternAsync(int fieldId, PatternType type, string pattern, int priority)
    {
        using var conn = Open();
        return await conn.ExecuteScalarAsync<int>(
            "INSERT INTO PiiPatterns (FieldId, PatternType, Pattern, Priority) VALUES (@fieldId, @type, @pattern, @priority); SELECT last_insert_rowid();",
            new { fieldId, type = type.ToString(), pattern, priority });
    }

    public async Task UpdatePatternAsync(int patternId, PatternType type, string pattern, int priority)
    {
        using var conn = Open();
        await conn.ExecuteAsync(
            "UPDATE PiiPatterns SET PatternType = @type, Pattern = @pattern, Priority = @priority WHERE Id = @patternId",
            new { patternId, type = type.ToString(), pattern, priority });
    }

    public async Task DeletePatternAsync(int patternId)
    {
        using var conn = Open();
        await conn.ExecuteAsync("DELETE FROM PiiPatterns WHERE Id = @patternId", new { patternId });
    }
}
