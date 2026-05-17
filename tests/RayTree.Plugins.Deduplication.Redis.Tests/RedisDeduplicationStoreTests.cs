using Moq;
using RayTree.Plugins.Deduplication.Redis;
using StackExchange.Redis;

namespace RayTree.Plugins.Deduplication.Redis.Tests;

public class RedisDeduplicationStoreTests
{
    private Mock<IConnectionMultiplexer> _multiplexer = null!;
    private Mock<IDatabase> _db = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new Mock<IDatabase>(MockBehavior.Strict);
        _multiplexer = new Mock<IConnectionMultiplexer>(MockBehavior.Strict);
        _multiplexer.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Returns(_db.Object);
        _multiplexer.Setup(m => m.GetDatabase()).Returns(_db.Object);
    }

    [Test]
    public async Task TryMarkProcessedAsync_WhenKeyAbsent_CallsStringSetWithNotExistsAndReturnTrue()
    {
        var options = new RedisDeduplicationOptions();
        _db.Setup(d => d.StringSetAsync(
                "raytree:dedup:default:corr-1",
                (RedisValue)"1",
                options.RetentionPeriod,
                When.NotExists))
            .ReturnsAsync(true);

        var store = new RedisDeduplicationStore(_multiplexer.Object, options);
        var result = await store.TryMarkProcessedAsync("corr-1");

        Assert.That(result, Is.True);
        _db.VerifyAll();
    }

    [Test]
    public async Task TryMarkProcessedAsync_WhenKeyPresent_ReturnsFalse()
    {
        var options = new RedisDeduplicationOptions();
        _db.Setup(d => d.StringSetAsync(
                "raytree:dedup:default:corr-2",
                (RedisValue)"1",
                options.RetentionPeriod,
                When.NotExists))
            .ReturnsAsync(false);

        var store = new RedisDeduplicationStore(_multiplexer.Object, options);
        var result = await store.TryMarkProcessedAsync("corr-2");

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task RevertProcessedAsync_CallsKeyDeleteWithCorrectKey()
    {
        var options = new RedisDeduplicationOptions();
        _db.Setup(d => d.KeyDeleteAsync("raytree:dedup:default:corr-3", CommandFlags.None))
            .ReturnsAsync(true);

        var store = new RedisDeduplicationStore(_multiplexer.Object, options);
        await store.RevertProcessedAsync("corr-3");

        _db.VerifyAll();
    }

    [Test]
    public async Task CleanupAsync_IssuesNoRedisCommands()
    {
        var strictDb = new Mock<IDatabase>(MockBehavior.Strict);
        var strictMux = new Mock<IConnectionMultiplexer>(MockBehavior.Strict);
        strictMux.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Returns(strictDb.Object);

        var store = new RedisDeduplicationStore(strictMux.Object, new RedisDeduplicationOptions());

        // No calls on strictDb are set up — any Redis call would throw
        await store.CleanupAsync(TimeSpan.FromHours(1));

        strictDb.VerifyNoOtherCalls();
    }

    [Test]
    public async Task TryMarkProcessedAsync_WithCustomPrefix_UsesCorrectKeyFormat()
    {
        var options = new RedisDeduplicationOptions { KeyPrefix = "orders" };
        _db.Setup(d => d.StringSetAsync(
                "raytree:dedup:orders:corr-4",
                (RedisValue)"1",
                options.RetentionPeriod,
                When.NotExists))
            .ReturnsAsync(true);

        var store = new RedisDeduplicationStore(_multiplexer.Object, options);
        await store.TryMarkProcessedAsync("corr-4");

        _db.VerifyAll();
    }

    [Test]
    public void DefaultOptions_HaveExpectedValues()
    {
        var options = new RedisDeduplicationOptions();

        Assert.Multiple(() =>
        {
            Assert.That(options.KeyPrefix, Is.EqualTo("default"));
            Assert.That(options.RetentionPeriod, Is.EqualTo(TimeSpan.FromHours(24)));
            Assert.That(options.Database, Is.EqualTo(-1));
        });
    }

    [Test]
    public void Constructor_WhenDatabaseIsNegative_PassesNegativeOneToGetDatabase()
    {
        var mux = new Mock<IConnectionMultiplexer>(MockBehavior.Strict);
        mux.Setup(m => m.GetDatabase(-1, It.IsAny<object?>())).Returns(_db.Object);

        _ = new RedisDeduplicationStore(mux.Object, new RedisDeduplicationOptions { Database = -1 });

        mux.Verify(m => m.GetDatabase(-1, It.IsAny<object?>()), Times.Once);
    }

    [Test]
    public void Constructor_WhenDatabaseIsNonNegative_PassesIndexToGetDatabase()
    {
        var mux = new Mock<IConnectionMultiplexer>(MockBehavior.Strict);
        mux.Setup(m => m.GetDatabase(2, It.IsAny<object?>())).Returns(_db.Object);

        _ = new RedisDeduplicationStore(mux.Object, new RedisDeduplicationOptions { Database = 2 });

        mux.Verify(m => m.GetDatabase(2, It.IsAny<object?>()), Times.Once);
    }
}
