using Moq;
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
    }

    [Test]
    public async Task TryMarkProcessedAsync_WhenKeyAbsent_ReturnsTrueAndWritesKeyWithNxTtl()
    {
        // Arrange
        var options = new RedisDeduplicationOptions();
        _db.Setup(d => d.StringSetAsync(
                key: "raytree:dedup:default:corr-1",
                value: (RedisValue)"1",
                expiry: options.RetentionPeriod,
                when: When.NotExists))
            .ReturnsAsync(true);

        var store = new RedisDeduplicationStore(multiplexer: _multiplexer.Object, options: options);

        // Act
        var result = await store.TryMarkProcessedAsync("corr-1");

        // Assert
        Assert.That(result, Is.True);
        _db.VerifyAll();
    }

    [Test]
    public async Task TryMarkProcessedAsync_WhenKeyPresent_ReturnsFalse()
    {
        // Arrange
        var options = new RedisDeduplicationOptions();
        _db.Setup(d => d.StringSetAsync(
                key: "raytree:dedup:default:corr-2",
                value: (RedisValue)"1",
                expiry: options.RetentionPeriod,
                when: When.NotExists))
            .ReturnsAsync(false);

        var store = new RedisDeduplicationStore(multiplexer: _multiplexer.Object, options: options);

        // Act
        var result = await store.TryMarkProcessedAsync("corr-2");

        // Assert
        Assert.That(result, Is.False);
        _db.VerifyAll();
    }

    [Test]
    public async Task RevertProcessedAsync_Always_DeletesKeyByCorrelationId()
    {
        // Arrange
        var options = new RedisDeduplicationOptions();
        _db.Setup(d => d.KeyDeleteAsync(key: "raytree:dedup:default:corr-3", flags: CommandFlags.None))
            .ReturnsAsync(true);

        var store = new RedisDeduplicationStore(multiplexer: _multiplexer.Object, options: options);

        // Act
        await store.RevertProcessedAsync("corr-3");

        // Assert
        _db.VerifyAll();
    }

    [Test]
    public async Task CleanupAsync_Always_IssuesNoRedisCommands()
    {
        // Arrange
        var strictDb = new Mock<IDatabase>(MockBehavior.Strict);
        var strictMux = new Mock<IConnectionMultiplexer>(MockBehavior.Strict);
        strictMux.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Returns(strictDb.Object);

        var store = new RedisDeduplicationStore(multiplexer: strictMux.Object, options: new RedisDeduplicationOptions());

        // Act
        await store.CleanupAsync(TimeSpan.FromHours(1));

        // Assert — no calls on strictDb set up; any Redis call would throw
        strictDb.VerifyNoOtherCalls();
    }

    [Test]
    public async Task TryMarkProcessedAsync_WithCustomPrefix_FormatsKeyAsRayTreeDedupPrefixId()
    {
        // Arrange
        var options = new RedisDeduplicationOptions { KeyPrefix = "orders" };
        _db.Setup(d => d.StringSetAsync(
                key: "raytree:dedup:orders:corr-4",
                value: (RedisValue)"1",
                expiry: options.RetentionPeriod,
                when: When.NotExists))
            .ReturnsAsync(true);

        var store = new RedisDeduplicationStore(multiplexer: _multiplexer.Object, options: options);

        // Act
        await store.TryMarkProcessedAsync("corr-4");

        // Assert
        _db.VerifyAll();
    }

    [Test]
    public void RedisDeduplicationOptions_DefaultConstructor_HasExpectedPropertyValues()
    {
        // Act
        var options = new RedisDeduplicationOptions();

        // Assert
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
        // Arrange
        var mux = new Mock<IConnectionMultiplexer>(MockBehavior.Strict);
        mux.Setup(m => m.GetDatabase(db: -1, asyncState: It.IsAny<object?>())).Returns(_db.Object);

        // Act
        _ = new RedisDeduplicationStore(multiplexer: mux.Object, options: new RedisDeduplicationOptions { Database = -1 });

        // Assert
        mux.Verify(m => m.GetDatabase(db: -1, asyncState: It.IsAny<object?>()), Times.Once);
    }

    [Test]
    public void Constructor_WhenDatabaseIsNonNegative_PassesIndexToGetDatabase()
    {
        // Arrange
        var mux = new Mock<IConnectionMultiplexer>(MockBehavior.Strict);
        mux.Setup(m => m.GetDatabase(db: 2, asyncState: It.IsAny<object?>())).Returns(_db.Object);

        // Act
        _ = new RedisDeduplicationStore(multiplexer: mux.Object, options: new RedisDeduplicationOptions { Database = 2 });

        // Assert
        mux.Verify(m => m.GetDatabase(db: 2, asyncState: It.IsAny<object?>()), Times.Once);
    }

    [Test]
    public void Constructor_WhenMultiplexerIsNull_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            _ = new RedisDeduplicationStore(multiplexer: null!, options: new RedisDeduplicationOptions()));
    }

    [Test]
    public void Constructor_WhenOptionsIsNull_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            _ = new RedisDeduplicationStore(multiplexer: _multiplexer.Object, options: null!));
    }

    [Test]
    public void RedisDeduplicationOptions_WhenKeyPrefixIsEmpty_ThrowsArgumentException()
    {
        // Arrange
        var options = new RedisDeduplicationOptions();

        // Act & Assert
        Assert.Throws<ArgumentException>(() => options.KeyPrefix = string.Empty);
    }

    [Test]
    public void RedisDeduplicationOptions_WhenKeyPrefixIsWhitespace_ThrowsArgumentException()
    {
        // Arrange
        var options = new RedisDeduplicationOptions();

        // Act & Assert
        Assert.Throws<ArgumentException>(() => options.KeyPrefix = "   ");
    }
}
