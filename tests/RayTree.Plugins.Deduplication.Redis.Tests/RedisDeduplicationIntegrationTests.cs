using RayTree.Plugins.Deduplication.Redis;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace RayTree.Plugins.Deduplication.Redis.Tests;

[NonParallelizable]
public class RedisDeduplicationIntegrationTests : IAsyncDisposable
{
    private readonly RedisContainer _redis = new RedisBuilder("redis:7.4-alpine").Build();
    private IConnectionMultiplexer _multiplexer = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await _redis.StartAsync();
        _multiplexer = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
    }

    // Each test uses a unique key prefix derived from a GUID so tests don't interfere.
    private static RedisDeduplicationOptions UniqueOptions(TimeSpan? retention = null)
    {
        var options = new RedisDeduplicationOptions { KeyPrefix = Guid.NewGuid().ToString("N") };
        if (retention.HasValue)
            options.RetentionPeriod = retention.Value;
        return options;
    }

    [Test]
    public async Task TryMarkProcessedAsync_FirstCall_ReturnsTrue_SecondCall_ReturnsFalse()
    {
        var store = new RedisDeduplicationStore(_multiplexer, UniqueOptions());
        const string correlationId = "dedup-test-1";

        var first = await store.TryMarkProcessedAsync(correlationId);
        var second = await store.TryMarkProcessedAsync(correlationId);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.True);
            Assert.That(second, Is.False);
        });
    }

    [Test]
    public async Task RevertProcessedAsync_AllowsReprocessing()
    {
        var store = new RedisDeduplicationStore(_multiplexer, UniqueOptions());
        const string correlationId = "dedup-test-2";

        var first = await store.TryMarkProcessedAsync(correlationId);
        await store.RevertProcessedAsync(correlationId);
        var third = await store.TryMarkProcessedAsync(correlationId);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.True);
            Assert.That(third, Is.True);
        });
    }

    [Test]
    public async Task KeyExpiry_AfterTtlElapses_AllowsReprocessing()
    {
        var store = new RedisDeduplicationStore(_multiplexer, UniqueOptions(TimeSpan.FromSeconds(1)));
        const string correlationId = "dedup-test-3";

        var first = await store.TryMarkProcessedAsync(correlationId);

        // Wait for the TTL to elapse
        await Task.Delay(TimeSpan.FromSeconds(2));

        var second = await store.TryMarkProcessedAsync(correlationId);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.True);
            Assert.That(second, Is.True, "Key should have expired and been treated as new");
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _multiplexer.DisposeAsync();
        await _redis.DisposeAsync();
    }
}
