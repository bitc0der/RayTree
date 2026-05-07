namespace RayTree.Core.Plugins.Deduplication;

public interface IDeduplicationStore
{
    Task<bool> TryMarkProcessedAsync(string correlationId, CancellationToken cancellationToken = default);
    Task<bool> IsProcessedAsync(string correlationId, CancellationToken cancellationToken = default);
    Task CleanupAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default);
}
