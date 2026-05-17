using Microsoft.Extensions.Logging.Abstractions;
using RayTree.Core.Handling;
using RayTree.Core.Telemetry;
using RayTree.Core.Models;
using RayTree.Core.Plugins.Compression;
using RayTree.Core.Tracking;
using RayTree.Plugins.InMemory;
using RayTree.Plugins.Serializers.Json;
using Testcontainers.Kafka;

namespace RayTree.Plugins.Kafka.Tests;

/// <summary>
/// Full pipeline tests: EntityChangeTracker → KafkaPublisher → KafkaConsumer → ChangeSubscriber → handler.
/// Each test starts the subscriber first, polls <see cref="KafkaConsumer.IsAssigned"/> until the
/// broker has acknowledged the subscription, then publishes — avoiding the flaky fixed-delay approach.
/// </summary>
[NonParallelizable]
public class KafkaEndToEndTests : IAsyncDisposable
{
    private readonly KafkaContainer _kafka = new KafkaBuilder("confluentinc/cp-kafka:7.7.8")
        .Build();

    [OneTimeSetUp]
    public Task OneTimeSetUp() => _kafka.StartAsync();

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private EntityChangeTracker BuildTracker(string topic)
        => EntityChangeTracker.Create()
            .UsePublisherOptions(o => o.PollingInterval = TimeSpan.FromMilliseconds(100))
            .ForEntity<Order>(e => e
                .UseOutbox(new InMemoryOutbox())
                .UsePublisher(new KafkaPublisher(new KafkaPublisherOptions
                {
                    BootstrapServers = _kafka.GetBootstrapAddress(),
                    Topic            = topic,
                    Acks             = "all"
                }))
                .UseSerializer(new JsonSerializerPlugin())
                .UseCompressor(new NoOpCompressorPlugin()))
            .Build();

    private KafkaConsumer BuildConsumer(string topic, string groupId) => new(new KafkaConsumerOptions
    {
        BootstrapServers = _kafka.GetBootstrapAddress(),
        Topic            = topic,
        GroupId          = groupId,
        FromEarliest     = true,
        PollTimeoutMs    = 200   // short poll for fast test feedback
    }, NullLoggerFactory.Instance);

    /// <summary>
    /// Polls <see cref="KafkaConsumer.IsAssigned"/> until the broker has acknowledged the
    /// consumer's subscription (partition assignment is underway).  This replaces the old
    /// fixed <c>Task.Delay(3s)</c> and is both faster and more reliable on slow CI runners.
    /// </summary>
    private static async Task WaitForAssignmentAsync(KafkaConsumer consumer,
        TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(15));
        while (!consumer.IsAssigned)
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(100, cts.Token);
        }
    }

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [Test]
    public async Task TrackInsert_HandlerReceivesCorrectChange()
    {
        // Arrange
        var topic    = $"test-insert-{Guid.NewGuid():N}";
        using var consumer = BuildConsumer(topic, $"group-{Guid.NewGuid():N}");
        await consumer.InitializeAsync();

        var tcs = new TaskCompletionSource<EntityChange>();
        var subscriber = new ChangeSubscriber(NullLogger<ChangeSubscriber>.Instance, new RayTreeMeter());
        subscriber
            .ForEntity<Order>()
            .RegisterQueue<Order>(consumer)
            .OnChange<Order>(ChangeType.Insert, (change, _) =>
            {
                tcs.TrySetResult(change);
                return Task.CompletedTask;
            });

        using var cts   = new CancellationTokenSource();
        var consumeTask = Task.Run(() => subscriber.ConsumeFromConsumerAsync(consumer, cts.Token));
        // Wait until the broker has acknowledged the subscription before publishing.
        await WaitForAssignmentAsync(consumer);
        using var tracker = BuildTracker(topic);

        // Act
        await tracker.TrackInsertAsync(new Order { Id = 1, Total = 49.99m });

        // Assert
        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.That(received.EntityId, Is.EqualTo("1"));
        Assert.That(received.ChangeType, Is.EqualTo(ChangeType.Insert));

        cts.Cancel();
    }

    [Test]
    public async Task TrackUpdate_HandlerReceivesCorrectChange()
    {
        // Arrange
        var topic    = $"test-update-{Guid.NewGuid():N}";
        using var consumer = BuildConsumer(topic, $"group-{Guid.NewGuid():N}");
        await consumer.InitializeAsync();

        var tcs = new TaskCompletionSource<EntityChange>();
        var subscriber = new ChangeSubscriber(NullLogger<ChangeSubscriber>.Instance, new RayTreeMeter());
        subscriber
            .ForEntity<Order>()
            .RegisterQueue<Order>(consumer)
            .OnChange<Order>(ChangeType.Update, (change, _) =>
            {
                tcs.TrySetResult(change);
                return Task.CompletedTask;
            });

        using var cts   = new CancellationTokenSource();
        var consumeTask = Task.Run(() => subscriber.ConsumeFromConsumerAsync(consumer, cts.Token));
        // Wait until the broker has acknowledged the subscription before publishing.
        await WaitForAssignmentAsync(consumer);
        using var tracker = BuildTracker(topic);

        // Act
        await tracker.TrackUpdateAsync(new Order { Id = 77, Total = 300m });

        // Assert
        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.That(received.EntityId, Is.EqualTo("77"));
        Assert.That(received.ChangeType, Is.EqualTo(ChangeType.Update));

        cts.Cancel();
    }

    [Test]
    public async Task TrackMultiple_AllChangesDeliveredInOrder()
    {
        // Arrange
        var topic    = $"test-batch-{Guid.NewGuid():N}";
        using var consumer = BuildConsumer(topic, $"group-{Guid.NewGuid():N}");
        await consumer.InitializeAsync();

        var received    = new List<EntityChange>();
        var allReceived = new TaskCompletionSource<bool>();

        var subscriber = new ChangeSubscriber(NullLogger<ChangeSubscriber>.Instance, new RayTreeMeter());
        ChangeHandlerAsync<Order> recordChange = (change, _) =>
        {
            lock (received) received.Add(change);
            if (received.Count == 3) allReceived.TrySetResult(true);
            return Task.CompletedTask;
        };
        subscriber
            .ForEntity<Order>()
            .RegisterQueue<Order>(consumer)
            .OnChange<Order>(ChangeType.Insert, recordChange)
            .OnChange<Order>(ChangeType.Update, recordChange)
            .OnChange<Order>(ChangeType.Delete, recordChange);

        using var cts   = new CancellationTokenSource();
        var consumeTask = Task.Run(() => subscriber.ConsumeFromConsumerAsync(consumer, cts.Token));
        // Wait until the broker has acknowledged the subscription before publishing.
        await WaitForAssignmentAsync(consumer);
        using var tracker = BuildTracker(topic);

        // Act
        await tracker.TrackInsertAsync(new Order { Id = 1, Total = 10m });
        await tracker.TrackUpdateAsync(new Order { Id = 2, Total = 20m });
        await tracker.TrackDeleteAsync(new Order { Id = 3, Total = 30m });

        // Assert
        await allReceived.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.That(received, Has.Count.EqualTo(3));
        // Kafka preserves per-partition order; all three share the same entity type key
        Assert.That(received.Select(c => c.EntityId),
            Is.EqualTo(new[] { "1", "2", "3" }));

        cts.Cancel();
    }

    public ValueTask DisposeAsync() => _kafka.DisposeAsync();

    // -------------------------------------------------------------------------
    // Test entity
    // -------------------------------------------------------------------------
    private class Order
    {
        public int Id { get; set; }
        public decimal Total { get; set; }
    }
}
