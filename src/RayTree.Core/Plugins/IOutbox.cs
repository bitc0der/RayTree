using RayTree.Models;

namespace RayTree.Plugins;

public interface IOutbox
{
    Task WriteAsync(EntityChange change, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EntityChange>> GetUnpublishedAsync(int batchSize, CancellationToken cancellationToken = default);
    Task MarkPublishedAsync(long id, CancellationToken cancellationToken = default);
    Task<int> CleanupPublishedAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default);
    Task<EntityChange?> GetByIdAsync(long id, CancellationToken cancellationToken = default);
}
