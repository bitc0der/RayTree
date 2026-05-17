using RayTree.Core.Plugins.Deduplication;
using StackExchange.Redis;

namespace RayTree.Plugins.Deduplication.Redis;

/// <summary>
/// Redis-backed <see cref="IDeduplicationStore"/> that stores processed correlation IDs using
/// TTL-based expiry. Each key expires automatically after <see cref="RedisDeduplicationOptions.RetentionPeriod"/>,
/// making <see cref="CleanupAsync"/> a no-op.
/// </summary>
public sealed class RedisDeduplicationStore : IDeduplicationStore
{
    private readonly IDatabase _db;
    private readonly RedisDeduplicationOptions _options;

    /// <summary>
    /// Initializes a new instance of <see cref="RedisDeduplicationStore"/>.
    /// </summary>
    /// <param name="multiplexer">The StackExchange.Redis connection multiplexer.</param>
    /// <param name="options">Store configuration (key prefix, TTL, database index).</param>
    public RedisDeduplicationStore(IConnectionMultiplexer multiplexer, RedisDeduplicationOptions options)
    {
        ArgumentNullException.ThrowIfNull(multiplexer);
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _db = multiplexer.GetDatabase(db: options.Database);
    }

    /// <inheritdoc/>
    public async Task<bool> TryMarkProcessedAsync(string correlationId, CancellationToken cancellationToken = default)
        => await _db.StringSetAsync(
            key: BuildKey(correlationId),
            value: "1",
            expiry: _options.RetentionPeriod,
            when: When.NotExists);

    /// <inheritdoc/>
    public async Task RevertProcessedAsync(string correlationId, CancellationToken cancellationToken = default)
        => await _db.KeyDeleteAsync(key: BuildKey(correlationId));

    /// <inheritdoc/>
    public Task CleanupAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    private string BuildKey(string correlationId)
        => $"raytree:dedup:{_options.KeyPrefix}:{correlationId}";
}
