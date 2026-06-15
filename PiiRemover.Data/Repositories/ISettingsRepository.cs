namespace PiiRemover.Data.Repositories;

public interface ISettingsRepository
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value, string? description = null);
    Task<IEnumerable<SettingEntry>> GetAllAsync();
}

public class SettingEntry
{
    public string Key { get; set; } = string.Empty;
    public string? Value { get; set; }
    public string? Description { get; set; }
}
