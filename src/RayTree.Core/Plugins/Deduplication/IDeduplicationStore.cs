namespace RayTree.Core.Plugins.Deduplication;

public interface IDeduplicationStore
{
    /// <summary>
    /// Atomically marks <paramref name="correlationId"/> as processed.
    /// Returns <c>true</c> if this caller claimed it (first time seen);
    /// <c>false</c> if it was already present (duplicate).
    /// </summary>
    Task<bool> TryMarkProcessedAsync(string correlationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a previously claimed <paramref name="correlationId"/> so the message
    /// can be retried when the handler failed to process it successfully.
    /// </summary>
    Task RevertProcessedAsync(string correlationId, CancellationToken cancellationToken = default);

    Task CleanupAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default);
}
