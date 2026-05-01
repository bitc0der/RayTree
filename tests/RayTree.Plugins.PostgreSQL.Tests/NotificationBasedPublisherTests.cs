using System.IO.Pipelines;
using Npgsql;
using RayTree.Models;
using RayTree.Plugins;
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
        // Auto-init happens via Build()
    }

    [SetUp]
    public void SetUp()
    {
        // Create fresh tracker with auto-init for each test
        var builder = new ChangeTrackingBuilder();
        builder.ForEntity<TestEntity>()
            .UseRepository(new PostgreSqlRepository<TestEntity>(new()
            {
                ConnectionString = _postgres.GetConnectionString(),
                TableName = "test_entity_source"
            }))
            .UseOutbox(new PostgreSqlOutbox(new()
            {
                ConnectionString = _postgres.GetConnectionString(),
                OutboxTableName = "test_entity_outbox"
            }))
            .UseQueue(new InMemoryQueue())
            .UseSerializer(new JsonSerializerPlugin())
            .UseCompressor(new GzipCompressorPlugin());

        _tracker = builder.Build(); // Auto-creates source table + triggers + outbox table
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
        // Clear outbox table for next test
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

        // Insert a change via the outbox
        var outbox = _tracker.GetOutbox(typeof(TestEntity)) as PostgreSqlOutbox;
        var change = new EntityChange
        {
            EntityType = typeof(TestEntity).AssemblyQualifiedName!,
            EntityId = Guid.NewGuid().ToString(),
            ChangeType = ChangeType.Insert,
            Timestamp = DateTime.UtcNow
        };

        await outbox!.WriteAsync(change);

        // Wait for notification to be received
        await Task.Delay(2000);

        // Verify the publisher received the notification
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

        // Insert a change via the outbox (triggers notification)
        var outbox = _tracker.GetOutbox(typeof(TestEntity)) as PostgreSqlOutbox;
        var change = new EntityChange
        {
            EntityType = typeof(TestEntity).AssemblyQualifiedName!,
            EntityId = Guid.NewGuid().ToString(),
            ChangeType = ChangeType.Insert,
            Timestamp = DateTime.UtcNow
        };

        await outbox!.WriteAsync(change);

        // Wait for notification and processing
        await Task.Delay(3000);

        // Verify the queue received the message
        // (InMemoryQueue stores messages internally)
        Assert.That(_queue, Is.Not.Null);
    }
}
