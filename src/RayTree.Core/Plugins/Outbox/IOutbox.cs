using RayTree.Core.Models;
using RayTree.Core.Tracking;

namespace RayTree.Core.Plugins.Outbox;

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

    /// <summary>
    /// Atomically transitions the record from unpublished to published.
    /// Returns <c>true</c> if this caller claimed the record; <c>false</c> if it was
    /// already published (i.e., another publisher got there first).
    /// </summary>
    Task<bool> TryClaimForPublishingAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reverts a previously claimed record back to unpublished so the fallback
    /// polling loop can retry it after a publish failure.
    /// </summary>
    Task RevertClaimAsync(long id, CancellationToken cancellationToken = default);

    Task<int> CleanupPublishedAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default);

    Task<int> CleanupStaleUnpublishedAsync(TimeSpan staleThreshold, CancellationToken cancellationToken = default);

    Task<EntityChange<TEntity>?> GetByIdAsync<TEntity>(long id, CancellationToken cancellationToken = default)
        where TEntity : class;
}
