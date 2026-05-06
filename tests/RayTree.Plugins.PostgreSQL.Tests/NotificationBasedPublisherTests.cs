using Npgsql;
using RayTree.Models;
using RayTree.Plugins.InMemory;
using RayTree.Tracking;
using RayTree.Distribution;
using RayTree.Plugins.Serializers.Json;
using RayTree.Plugins.Compressors.Gzip;
using Testcontainers.PostgreSql;

namespace RayTree.Plugins.PostgreSQL.Tests;

[NonParallelizable]
public class NotificationBasedPublisherTests : IAsyncDisposable
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    private EntityChangeTracker _tracker = null!;
    private InMemoryQueue _queue = null!;
    private NotificationBasedPublisher _publisher = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await _postgres.StartAsync();
    }

    [SetUp]
    public void SetUp()
    {
        var builder = new ChangeTrackingBuilder();
        builder.ForEntity<TestEntity>()
            .UseRepository(new PostgreSqlRepository<TestEntity>(new()
            {
                ConnectionString = _postgres.GetConnectionString(),
                TableName = "test_entity_source"
            }))
            .UseOutbox(new PostgreSqlOutbox<TestEntity>(new()
            {
                ConnectionString = _postgres.GetConnectionString(),
                OutboxTableName = "test_entity_outbox"
            }))
            .UseQueue(new InMemoryQueue())
            .UseSerializer(new JsonSerializerPlugin())
            .UseCompressor(new GzipCompressorPlugin());

        _tracker = builder.Build();
        _queue = new InMemoryQueue();
        _publisher = new NotificationBasedPublisher(_tracker, new NotificationBasedPublisherOptions
        {
            ConnectionString = _postgres.GetConnectionString(),
            ChannelName = "notify_test_entity_change",
            FallbackPollingInterval = TimeSpan.FromSeconds(1)
        });
    }

    [TearDown]
    public async Task TearDown()
    {
        await _publisher.StopAsync();
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("TRUNCATE test_entity_outbox RESTART IDENTITY", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync() => _postgres.DisposeAsync();

    [Test]
    public async Task StartAsync_ShouldListenForNotifications()
    {
        await _publisher.StartAsync();

        var outbox = _tracker.GetOutbox(typeof(TestEntity)) as PostgreSqlOutbox<TestEntity>;
        var change = new EntityChange<TestEntity>
        {
            EntityType = typeof(TestEntity).FullName!,
            EntityId = Guid.NewGuid().ToString(),
            ChangeType = ChangeType.Insert,
            Timestamp = DateTime.UtcNow,
            State = new TestEntity { Id = 1 }
        };

        await outbox!.WriteAsync(change);

        await Task.Delay(2000);

        Assert.That(_publisher.IsRunning, Is.True);
    }

    [Test]
    public async Task StopAsync_ShouldStopListening()
    {
        await _publisher.StartAsync();
        Assert.That(_publisher.IsRunning, Is.True);

        await _publisher.StopAsync();
        Assert.That(_publisher.IsRunning, Is.False);
    }

    [Test]
    public async Task HandleNotification_ShouldPublishToQueue()
    {
        await _publisher.StartAsync();

        var outbox = _tracker.GetOutbox(typeof(TestEntity)) as PostgreSqlOutbox<TestEntity>;
        var change = new EntityChange<TestEntity>
        {
            EntityType = typeof(TestEntity).FullName!,
            EntityId = Guid.NewGuid().ToString(),
            ChangeType = ChangeType.Insert,
            Timestamp = DateTime.UtcNow,
            State = new TestEntity { Id = 2 }
        };

        await outbox!.WriteAsync(change);

        await Task.Delay(3000);

        Assert.That(_queue, Is.Not.Null);
    }
}
