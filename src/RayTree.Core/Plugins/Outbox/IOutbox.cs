using RayTree.Core.Models;

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

    /// <summary>
    /// Returns the number of unpublished records currently held by this outbox for
    /// <paramref name="entityType"/>. Used to feed the <c>raytree.outbox.pending</c>
    /// observable gauge; implementations should make this a cheap, indexed lookup.
    /// </summary>
    Task<long> GetPendingCountAsync(Type entityType, CancellationToken cancellationToken = default);

    Task<EntityChange<TEntity>?> GetByIdAsync<TEntity>(long id, CancellationToken cancellationToken = default)
        where TEntity : class;

    /// <summary>
    /// Classifies an exception thrown by this outbox as a connection-level fault. Consumers
    /// (e.g. <c>OutboxPublisherService</c>'s polling loop) use this to demote per-batch
    /// <c>Error</c> logs to <c>Warning</c> and to emit connection-recovery metrics keyed
    /// on <see cref="ConnectionComponent"/> / <see cref="ConnectionEndpoint"/>.
    /// <para>
    /// Default implementation returns <c>false</c> — outboxes that have no observable
    /// external connection (e.g. <c>InMemoryOutbox</c>) inherit the no-op default.
    /// </para>
    /// </summary>
    bool IsConnectionFault(Exception ex) => false;

    /// <summary>
    /// The <c>component</c> tag value applied to connection-recovery metrics for this
    /// outbox (e.g. <c>"postgres.outbox"</c>). Returns <c>null</c> when this outbox has
    /// no observable external connection.
    /// </summary>
    string? ConnectionComponent => null;

    /// <summary>
    /// The <c>endpoint</c> tag value applied to connection-recovery metrics (e.g. the
    /// host:port of the underlying database). Returns <c>null</c> when this outbox has
    /// no observable external connection.
    /// </summary>
    string? ConnectionEndpoint => null;
}
