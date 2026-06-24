namespace PiiRemover.Data.Repositories;

public interface IVideoConnectionRepository
{
    Task InsertAsync(string connectionId, int clientId);
    Task UpdateLastSeenAsync(string connectionId);
    Task MarkInactiveAsync(string connectionId);
    Task PruneInactiveAsync(int olderThanMinutes);
}
