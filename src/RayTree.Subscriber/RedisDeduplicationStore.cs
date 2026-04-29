using StackExchange.Redis;

namespace RayTree.Subscriber;

public class RedisDeduplicationStore : IDeduplicationStore, IDisposable
{
    private readonly IConnectionMultiplexer _connection;
    private readonly IDatabase _database;
    private readonly string _keyPrefix;
    private readonly TimeSpan _defaultTtl;

    public RedisDeduplicationStore(string connectionString, string keyPrefix = "raytree:dedup:", TimeSpan? defaultTtl = null)
    {
        _connection = ConnectionMultiplexer.Connect(connectionString);
        _database = _connection.GetDatabase();
        _keyPrefix = keyPrefix;
        _defaultTtl = defaultTtl ?? TimeSpan.FromHours(24);
    }

    public RedisDeduplicationStore(IConnectionMultiplexer connection, string keyPrefix = "raytree:dedup:", TimeSpan? defaultTtl = null)
    {
        _connection = connection;
        _database = connection.GetDatabase();
        _keyPrefix = keyPrefix;
        _defaultTtl = defaultTtl ?? TimeSpan.FromHours(24);
    }

    public async Task<bool> TryMarkProcessedAsync(string correlationId, CancellationToken cancellationToken = default)
    {
        var key = $"{_keyPrefix}{correlationId}";
        return await _database.StringSetAsync(key, "1", _defaultTtl, When.NotExists);
    }

    public async Task<bool> IsProcessedAsync(string correlationId, CancellationToken cancellationToken = default)
    {
        var key = $"{_keyPrefix}{correlationId}";
        return await _database.KeyExistsAsync(key);
    }

    public Task CleanupAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public void Dispose() => _connection.Dispose();
}
