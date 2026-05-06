using RayTree.Models;
using RayTree.Tracking;

namespace RayTree.Plugins;

public interface IOutbox
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task WriteAsync<TEntity>(EntityChange<TEntity> change, CancellationToken cancellationToken = default)
        where TEntity : class;

    Task<IReadOnlyList<EntityChange<TEntity>>> GetUnpublishedAsync<TEntity>(
        int batchSize,
        CancellationToken cancellationToken = default)
        where TEntity : class;

    Task<IReadOnlyList<EntityChange<TEntity>>> GetUnpublishedAsync<TEntity>(
        ChangeType? changeType = null,
        DateTime? since = null,
        int batchSize = 100,
        CancellationToken cancellationToken = default)
        where TEntity : class;

    Task MarkPublishedAsync(long id, CancellationToken cancellationToken = default);

    Task<int> CleanupPublishedAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default);

    Task<EntityChange<TEntity>?> GetByIdAsync<TEntity>(long id, CancellationToken cancellationToken = default)
        where TEntity : class;
}
