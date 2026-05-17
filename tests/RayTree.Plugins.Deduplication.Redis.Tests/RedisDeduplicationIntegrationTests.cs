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

    // Each test gets a unique prefix so tests remain isolated without flushing the database.
    private static RedisDeduplicationOptions UniqueOptions(TimeSpan? retention = null)
    {
        var options = new RedisDeduplicationOptions { KeyPrefix = Guid.NewGuid().ToString("N") };
        if (retention.HasValue)
            options.RetentionPeriod = retention.Value;
        return options;
    }

    [Test]
    public async Task TryMarkProcessedAsync_WhenCalledTwiceWithSameId_ReturnsTrueThenFalse()
    {
        // Arrange
        var store = new RedisDeduplicationStore(multiplexer: _multiplexer, options: UniqueOptions());
        const string correlationId = "dedup-test-1";

        // Act
        var first = await store.TryMarkProcessedAsync(correlationId);
        var second = await store.TryMarkProcessedAsync(correlationId);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(first, Is.True);
            Assert.That(second, Is.False);
        });
    }

    [Test]
    public async Task RevertProcessedAsync_AfterMark_AllowsReprocessing()
    {
        // Arrange
        var store = new RedisDeduplicationStore(multiplexer: _multiplexer, options: UniqueOptions());
        const string correlationId = "dedup-test-2";

        // Act
        var first = await store.TryMarkProcessedAsync(correlationId);
        await store.RevertProcessedAsync(correlationId);
        var afterRevert = await store.TryMarkProcessedAsync(correlationId);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(first, Is.True);
            Assert.That(afterRevert, Is.True);
        });
    }

    [Test]
    public async Task TryMarkProcessedAsync_AfterTtlExpires_ReturnsTrueAgain()
    {
        // Arrange
        var store = new RedisDeduplicationStore(
            multiplexer: _multiplexer,
            options: UniqueOptions(retention: TimeSpan.FromSeconds(1)));
        const string correlationId = "dedup-test-3";

        // Act
        var first = await store.TryMarkProcessedAsync(correlationId);
        await Task.Delay(TimeSpan.FromSeconds(2));
        var afterExpiry = await store.TryMarkProcessedAsync(correlationId);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(first, Is.True);
            Assert.That(afterExpiry, Is.True, "Key should have expired and be accepted as new");
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _multiplexer.DisposeAsync();
        await _redis.DisposeAsync();
    }
}
