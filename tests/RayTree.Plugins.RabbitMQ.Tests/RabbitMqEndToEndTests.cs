using Microsoft.Extensions.Logging.Abstractions;
using RayTree.Core.Distribution;
using RayTree.Core.Handling;
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
    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder()
        .WithImage("rabbitmq:3-alpine")
        .Build();

    [OneTimeSetUp]
    public Task OneTimeSetUp() => _rabbitMq.StartAsync();

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private EntityChangeTracker BuildTracker(RabbitMqPublisher publisher)
    {
        var changePublisher = new ChangePublisher(NullLoggerFactory.Instance);
        changePublisher.RegisterOutbox(typeof(Order), new InMemoryOutbox());
        changePublisher.RegisterPublisher(typeof(Order), publisher);
        changePublisher.RegisterSerializer(typeof(Order), new JsonSerializerPlugin());
        changePublisher.RegisterCompressor(typeof(Order), new NoOpCompressorPlugin());
        changePublisher.Options.PollingInterval = TimeSpan.FromMilliseconds(100);
        return new EntityChangeTracker(changePublisher);
    }

    private RabbitMqPublisher BuildPublisher() => new(new RabbitMqPublisherOptions
    {
        HostName        = _rabbitMq.Hostname,
        Port            = _rabbitMq.GetMappedPublicPort(5672),
        UserName        = RabbitMqBuilder.DefaultUsername,
        Password        = RabbitMqBuilder.DefaultPassword,
        ExchangeName    = "entity_changes",
        ExchangeType    = "topic",
        DeclareExchange = true
    });

    private RabbitMqConsumer BuildConsumer(string queueName) => new(new RabbitMqConsumerOptions
    {
        HostName     = _rabbitMq.Hostname,
        Port         = _rabbitMq.GetMappedPublicPort(5672),
        UserName     = RabbitMqBuilder.DefaultUsername,
        Password     = RabbitMqBuilder.DefaultPassword,
        QueueName    = queueName,
        DeclareQueue = true,
        ExchangeName = "entity_changes",
        BindingKey   = "#"
    });

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [Test]
    public async Task TrackInsert_HandlerReceivesCorrectChange()
    {
        var queueName = $"test-{Guid.NewGuid():N}";
        var publisher = BuildPublisher();
        await publisher.InitializeAsync();

        var consumer = BuildConsumer(queueName);
        await consumer.InitializeAsync();

        var tcs = new TaskCompletionSource<EntityChange>();
        var subscriber = new ChangeSubscriber(NullLogger<ChangeSubscriber>.Instance);
        subscriber
            .ForEntity<Order>()
            .RegisterQueue<Order>(consumer)
            .OnChange<Order>(ChangeType.Insert, (change, _) =>
            {
                tcs.TrySetResult(change);
                return Task.CompletedTask;
            });

        using var cts     = new CancellationTokenSource();
        var consumeTask   = Task.Run(() => subscriber.ConsumeFromConsumerAsync(consumer, cts.Token));

        var tracker = BuildTracker(publisher);
        await tracker.InitializeAsync();
        await tracker.TrackInsertAsync(new Order { Id = 1, Total = 49.99m });

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.That(received.EntityId, Is.EqualTo("1"));
        Assert.That(received.ChangeType, Is.EqualTo(ChangeType.Insert));

        cts.Cancel();
        tracker.Dispose();
        consumer.Dispose();
        publisher.Dispose();
    }

    [Test]
    public async Task TrackUpdate_HandlerReceivesCorrectChange()
    {
        var queueName = $"test-{Guid.NewGuid():N}";
        var publisher = BuildPublisher();
        await publisher.InitializeAsync();

        var consumer = BuildConsumer(queueName);
        await consumer.InitializeAsync();

        var tcs = new TaskCompletionSource<EntityChange>();
        var subscriber = new ChangeSubscriber(NullLogger<ChangeSubscriber>.Instance);
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

        var tracker = BuildTracker(publisher);
        await tracker.InitializeAsync();
        await tracker.TrackUpdateAsync(new Order { Id = 55, Total = 200m });

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.That(received.EntityId, Is.EqualTo("55"));
        Assert.That(received.ChangeType, Is.EqualTo(ChangeType.Update));

        cts.Cancel();
        tracker.Dispose();
        consumer.Dispose();
        publisher.Dispose();
    }

    [Test]
    public async Task TrackMultiple_AllChangesDelivered()
    {
        var queueName = $"test-{Guid.NewGuid():N}";
        var publisher = BuildPublisher();
        await publisher.InitializeAsync();

        var consumer = BuildConsumer(queueName);
        await consumer.InitializeAsync();

        var received    = new List<EntityChange>();
        var allReceived = new TaskCompletionSource<bool>();

        var subscriber = new ChangeSubscriber(NullLogger<ChangeSubscriber>.Instance);
        subscriber
            .ForEntity<Order>()
            .RegisterQueue<Order>(consumer)
            .OnChange<Order>(changeType: null, (change, _) =>
            {
                lock (received) received.Add(change);
                if (received.Count == 3) allReceived.TrySetResult(true);
                return Task.CompletedTask;
            });

        using var cts   = new CancellationTokenSource();
        var consumeTask = Task.Run(() => subscriber.ConsumeFromConsumerAsync(consumer, cts.Token));

        var tracker = BuildTracker(publisher);
        await tracker.InitializeAsync();
        await tracker.TrackInsertAsync(new Order { Id = 1, Total = 10m });
        await tracker.TrackUpdateAsync(new Order { Id = 2, Total = 20m });
        await tracker.TrackDeleteAsync(new Order { Id = 3, Total = 30m });

        await allReceived.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.That(received, Has.Count.EqualTo(3));
        Assert.That(received.Select(c => c.EntityId).Order(),
            Is.EqualTo(new[] { "1", "2", "3" }));

        cts.Cancel();
        tracker.Dispose();
        consumer.Dispose();
        publisher.Dispose();
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
