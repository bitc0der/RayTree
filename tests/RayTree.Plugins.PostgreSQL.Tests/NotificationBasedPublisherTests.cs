using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using RayTree.Core.Models;
using RayTree.Core.Tracking;
using RayTree.Plugins.Compressors.Gzip;
using RayTree.Plugins.InMemory;
using RayTree.Plugins.PostgreSQL.Outbox;
using RayTree.Plugins.PostgreSQL.Outbox.Notification;
using RayTree.Plugins.Serializers.Json;

namespace RayTree.Plugins.PostgreSQL.Tests;

[NonParallelizable]
public class NotificationBasedPublisherTests : IAsyncDisposable
{
    private readonly IContainer _postgres = PostgresContainerFactory.Create();

    private EntityChangeTracker _tracker = null!;
    private InMemoryQueue _queue = null!;
    private PostgreSqlOutbox<TestEntity> _outbox = null!;
    private NotificationBasedPublisher _publisher = null!;

    private const string OutboxTable = "notification_test_outbox";
    private const string ChannelName = "notification_test_channel";

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await _postgres.StartAsync();
    }

    [SetUp]
    public async Task SetUp()
    {
        _queue = new InMemoryQueue();

        _outbox = new PostgreSqlOutbox<TestEntity>(new PostgreSqlOutboxOptions
        {
            ConnectionString = _postgres.GetConnectionString(),
            OutboxTableName = OutboxTable,
            UseNotificationChannel = true,
            NotificationChannel = ChannelName
        }, NullLoggerFactory.Instance);

        // Initialize outbox directly: creates table + trigger without starting background publisher services
        await _outbox.InitializeAsync();

        _tracker = EntityChangeTracker.Create()
            .ForEntity<TestEntity>(e => e
                .UseOutbox(_outbox)
                .UsePublisher(_queue)
                .UseSerializer(new JsonSerializerPlugin())
                .UseCompressor(new GzipCompressorPlugin()))
            .Build();

        _publisher = new NotificationBasedPublisher(_tracker,
            new NotificationBasedPublisherOptions
            {
                ConnectionString = _postgres.GetConnectionString(),
                ChannelName = ChannelName,
                FallbackPollingInterval = TimeSpan.FromMilliseconds(300)
            }, NullLoggerFactory.Instance);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _publisher.StopAsync();
        _tracker.Dispose();

        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"TRUNCATE {OutboxTable} RESTART IDENTITY", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync() => _postgres.DisposeAsync();

    private static EntityChange<TestEntity> CreateChange(int id) => new()
    {
        EntityType = typeof(TestEntity).FullName!,
        EntityId = id.ToString(),
        ChangeType = ChangeType.Insert,
        Timestamp = DateTime.UtcNow,
        State = new TestEntity { Id = id }
    };

    [Test]
    public async Task StartAsync_SetsIsRunning_StopAsync_ClearsIt()
    {
        // Arrange — setup in [SetUp]

        // Act
        await _publisher.StartAsync();

        // Assert
        Assert.That(_publisher.IsRunning, Is.True);

        await _publisher.StopAsync();
        Assert.That(_publisher.IsRunning, Is.False);
    }

    [Test]
    public async Task FallbackPolling_DeliversUnpublishedChange_ToQueue()
    {
        // Arrange
        var change = CreateChange(1);
        await _outbox.WriteAsync(change);
        await _publisher.StartAsync();

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = await _queue.Reader.ReadAsync(cts.Token);

        // Assert
        Assert.That(received.EntityId, Is.EqualTo("1"));
        Assert.That(received.ChangeType, Is.EqualTo(ChangeType.Insert));
    }

    [Test]
    public async Task FallbackPolling_DoesNotRedeliver_AlreadyPublishedChange()
    {
        // Arrange
        var change = CreateChange(2);
        await _outbox.WriteAsync(change);
        await _outbox.MarkPublishedAsync(change.Id);
        await _publisher.StartAsync();

        // Act
        await Task.Delay(700); // > 2× fallback interval

        // Assert
        Assert.That(_queue.Reader.TryRead(out _), Is.False);
    }

    [Test]
    public async Task FallbackPolling_MarksChangePublished_AfterDelivery()
    {
        // Arrange
        var change = CreateChange(3);
        await _outbox.WriteAsync(change);
        await _publisher.StartAsync();

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _queue.Reader.ReadAsync(cts.Token);

        // Assert
        // Poll until MarkPublishedAsync completes the DB write (runs after PublishAsync in the same loop iteration)
        var deadline = DateTime.UtcNow.AddSeconds(5);
        EntityChange<TestEntity>? stored = null;
        while (DateTime.UtcNow < deadline)
        {
            stored = await _outbox.GetByIdAsync<TestEntity>(change.Id);
            if (stored?.Published == true) break;
            await Task.Delay(50);
        }

        Assert.That(stored!.Published, Is.True);
    }

    [Test]
    public async Task StopAsync_PreventsDelivery_OfSubsequentWrites()
    {
        // Arrange
        await _publisher.StartAsync();
        await _publisher.StopAsync();

        // Act
        await _outbox.WriteAsync(CreateChange(4));
        await Task.Delay(700); // > 2× fallback interval

        // Assert
        Assert.That(_queue.Reader.TryRead(out _), Is.False);
    }

    [Test]
    public async Task FallbackPolling_DeliversBatch_OfMultipleChanges()
    {
        // Arrange
        for (var i = 1; i <= 5; i++)
            await _outbox.WriteAsync(CreateChange(i));
        await _publisher.StartAsync();

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = new List<MessageEnvelope>();
        for (var i = 0; i < 5; i++)
        {
            var envelope = await _queue.Reader.ReadAsync(cts.Token);
            received.Add(envelope);
        }

        // Assert
        Assert.That(received, Has.Count.EqualTo(5));
        Assert.That(received.Select(c => c.EntityId).Order(),
            Is.EqualTo(new[] { "1", "2", "3", "4", "5" }));
    }
}
