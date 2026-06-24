using PiiRemover.Data.Models;

namespace PiiRemover.Data.Repositories;

public interface IVideoJobRepository
{
    Task<VideoJob?> GetAsync(string id);
    Task<IEnumerable<VideoJob>> GetByClientAsync(int clientId, int limit = 20);
    Task<IEnumerable<VideoJob>> GetQueuedAsync(int limit);
    Task InsertAsync(VideoJob job);
    Task UpdateStatusAsync(string id, string status,
        string? outputPath = null, long? durationMs = null, string? errorMsg = null,
        string? startedAt = null, string? completedAt = null);
    Task<IEnumerable<VideoJob>> GetOldCompletedAsync(DateTime olderThan);
}
