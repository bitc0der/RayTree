using RayTree.Core.Plugins.Deduplication;
using StackExchange.Redis;

namespace RayTree.Plugins.Deduplication.Redis;

public sealed class RedisDeduplicationStore : IDeduplicationStore
{
    private readonly IDatabase _db;
    private readonly RedisDeduplicationOptions _options;

    public RedisDeduplicationStore(IConnectionMultiplexer multiplexer, RedisDeduplicationOptions options)
    {
        _options = options;
        _db = multiplexer.GetDatabase(options.Database);
    }

    public async Task<bool> TryMarkProcessedAsync(string correlationId, CancellationToken cancellationToken = default)
        => await _db.StringSetAsync(
            BuildKey(correlationId),
            "1",
            _options.RetentionPeriod,
            When.NotExists).ConfigureAwait(false);

    public async Task RevertProcessedAsync(string correlationId, CancellationToken cancellationToken = default)
        => await _db.KeyDeleteAsync(BuildKey(correlationId)).ConfigureAwait(false);

    public Task CleanupAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    private string BuildKey(string correlationId)
        => $"raytree:dedup:{_options.KeyPrefix}:{correlationId}";
}
