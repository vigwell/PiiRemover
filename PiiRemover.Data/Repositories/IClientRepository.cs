using PiiRemover.Core.Models;

namespace PiiRemover.Data.Repositories;

public interface IClientRepository
{
    Task<ClientRecord?> GetByApiKeyHashAsync(string hash);
    Task<IEnumerable<ClientRecord>> GetAllAsync();
    Task<int> CreateAsync(string name, string apiKeyHash);
    Task SetActiveAsync(int id, bool active);
    Task UpdateApiKeyHashAsync(int id, string newHash);
}

public class ClientRecord
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ApiKeyHash { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
}
