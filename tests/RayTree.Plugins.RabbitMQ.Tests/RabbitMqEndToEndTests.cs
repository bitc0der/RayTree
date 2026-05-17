using Microsoft.Extensions.Logging.Abstractions;
using RayTree.Core.Handling;
using RayTree.Core.Telemetry;
using RayTree.Core.Models;
using RayTree.Core.Plugins.Compression;
using RayTree.Core.Tracking;
using RayTree.Plugins.InMemory;
using RayTree.Plugins.Serializers.Json;
using Testcontainers.RabbitMq;

namespace RayTree.Plugins.RabbitMQ.Tests;

/// <summary>
/// Full pipeline tests: EntityChangeTracker → RabbitMqPublisher → RabbitMqConsumer → ChangeSubscriber → handler.
/// </summary>
[NonParallelizable]
public class RabbitMqEndToEndTests : IAsyncDisposable
{
    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder("rabbitmq:4.3.0-alpine")
        .Build();

    [OneTimeSetUp]
    public Task OneTimeSetUp() => _rabbitMq.StartAsync();

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private EntityChangeTracker BuildTracker(RabbitMqPublisher publisher)
        => EntityChangeTracker.Create()
            .UsePublisherOptions(o => o.PollingInterval = TimeSpan.FromMilliseconds(100))
            .ForEntity<Order>(e => e
                .UseOutbox(new InMemoryOutbox())
                .UsePublisher(publisher)
                .UseSerializer(new JsonSerializerPlugin())
                .UseCompressor(new NoOpCompressorPlugin()))
            .Build();

    private RabbitMqPublisher BuildPublisher() => new(new RabbitMqPublisherOptions
    {
        HostName = _rabbitMq.Hostname,
        Port = _rabbitMq.GetMappedPublicPort(5672),
        UserName = RabbitMqBuilder.DefaultUsername,
        Password = RabbitMqBuilder.DefaultPassword,
        ExchangeName = "entity_changes",
        ExchangeType = "topic",
        DeclareExchange = true
    });

    private RabbitMqConsumer BuildConsumer(string queueName) => new(
        new RabbitMqConsumerOptions
        {
            HostName = _rabbitMq.Hostname,
            Port = _rabbitMq.GetMappedPublicPort(5672),
            UserName = RabbitMqBuilder.DefaultUsername,
            Password = RabbitMqBuilder.DefaultPassword,
            QueueName = queueName,
            DeclareQueue = true,
            ExchangeName = "entity_changes",
            BindingKey = "#"
        });

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [Test]
    public async Task TrackInsert_HandlerReceivesCorrectChange()
    {
        // Arrange
        var queueName = $"test-{Guid.NewGuid():N}";
        using var publisher = BuildPublisher();
        await publisher.InitializeAsync();

        using var consumer = BuildConsumer(queueName);
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

        using var cts = new CancellationTokenSource();
        var consumeTask = Task.Run(() => subscriber.ConsumeFromConsumerAsync(consumer, cts.Token));
        using var tracker = BuildTracker(publisher);

        // Act
        await tracker.TrackInsertAsync(new Order { Id = 1, Total = 49.99m });

        // Assert
        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.That(received.EntityId, Is.EqualTo("1"));
        Assert.That(received.ChangeType, Is.EqualTo(ChangeType.Insert));

        cts.Cancel();
    }

    [Test]
    public async Task TrackUpdate_HandlerReceivesCorrectChange()
    {
        // Arrange
        var queueName = $"test-{Guid.NewGuid():N}";
        using var publisher = BuildPublisher();
        await publisher.InitializeAsync();

        using var consumer = BuildConsumer(queueName);
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

        using var cts = new CancellationTokenSource();
        var consumeTask = Task.Run(() => subscriber.ConsumeFromConsumerAsync(consumer, cts.Token));
        using var tracker = BuildTracker(publisher);

        // Act
        await tracker.TrackUpdateAsync(new Order { Id = 55, Total = 200m });

        // Assert
        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.That(received.EntityId, Is.EqualTo("55"));
        Assert.That(received.ChangeType, Is.EqualTo(ChangeType.Update));

        cts.Cancel();
    }

    [Test]
    public async Task TrackMultiple_AllChangesDelivered()
    {
        // Arrange
        var queueName = $"test-{Guid.NewGuid():N}";
        using var publisher = BuildPublisher();
        await publisher.InitializeAsync();

        using var consumer = BuildConsumer(queueName);
        await consumer.InitializeAsync();

        var received = new List<EntityChange>();
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

        using var cts = new CancellationTokenSource();
        var consumeTask = Task.Run(() => subscriber.ConsumeFromConsumerAsync(consumer, cts.Token));
        using var tracker = BuildTracker(publisher);

        // Act
        await tracker.TrackInsertAsync(new Order { Id = 1, Total = 10m });
        await tracker.TrackUpdateAsync(new Order { Id = 2, Total = 20m });
        await tracker.TrackDeleteAsync(new Order { Id = 3, Total = 30m });

        // Assert
        await allReceived.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.That(received, Has.Count.EqualTo(3));
        Assert.That(received.Select(c => c.EntityId).Order(),
            Is.EqualTo(new[] { "1", "2", "3" }));

        cts.Cancel();
    }

    public ValueTask DisposeAsync() => _rabbitMq.DisposeAsync();

    // -------------------------------------------------------------------------
    // Test entity
    // -------------------------------------------------------------------------
    private class Order
    {
        public int Id { get; set; }
        public decimal Total { get; set; }
    }
}
