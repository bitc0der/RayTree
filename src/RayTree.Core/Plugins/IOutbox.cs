using RayTree.Models;
using RayTree.Tracking;

namespace RayTree.Plugins;

public interface IOutbox
{
    Task WriteAsync(EntityChange change, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EntityChange>> GetUnpublishedAsync(int batchSize, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EntityChange>> GetUnpublishedAsync(string entityType, ChangeType? changeType = null, DateTime? since = null, int batchSize = 100, CancellationToken cancellationToken = default);
    Task MarkPublishedAsync(long id, CancellationToken cancellationToken = default);
    Task<int> CleanupPublishedAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default);
    Task<EntityChange?> GetByIdAsync(long id, CancellationToken cancellationToken = default);
}
