using Microsoft.Extensions.DependencyInjection;
using RayTree.Core.Models;
using RayTree.Core.Plugins.Compression;
using RayTree.Core.Tracking;
using RayTree.Plugins.InMemory;
using RayTree.Plugins.Serializers.Json;
using RayTree.Subscriber;

namespace RayTree.Subscriber.Tests;

/// <summary>
/// Full pipeline tests: EntityChangeTracker → InMemoryQueue (publisher) → ChangeSubscriber → handler.
/// </summary>
public class InMemoryEndToEndTests
{
    private EntityChangeTracker _tracker = null!;
    private InMemoryQueue _queue = null!;

    [SetUp]
    public async Task SetUp()
    {
        _queue = new InMemoryQueue();

        _tracker = new EntityChangeTracker();
        _tracker.RegisterOutbox(typeof(Order), new InMemoryOutbox());
        _tracker.RegisterPublisher(typeof(Order), _queue);
        _tracker.RegisterSerializer(typeof(Order), new JsonSerializerPlugin());
        _tracker.RegisterCompressor(typeof(Order), new NoOpCompressorPlugin());
        _tracker.PublisherOptions.PollingInterval = TimeSpan.FromMilliseconds(50);

        await _tracker.InitializeAsync();
    }

    [TearDown]
    public void TearDown()
    {
        _tracker.Dispose();
        _queue.Dispose();
    }

    // -------------------------------------------------------------------------
    // Helper: build a subscriber wired to _queue and start its consume loop.
    // Returns the subscriber + a CTS that stops the loop when cancelled.
    // -------------------------------------------------------------------------
    private (ChangeSubscriber subscriber, CancellationTokenSource cts, Task consumeTask)
        StartSubscriber(Action<ChangeSubscriber> configure)
    {
        var subscriber = new ChangeSubscriber();
        subscriber.RegisterQueue<Order>(_queue);
        configure(subscriber);

        var cts     = new CancellationTokenSource();
        var consume = Task.Run(() => subscriber.ConsumeFromConsumerAsync(_queue, cts.Token));
        return (subscriber, cts, consume);
    }

    [Test]
    public async Task TrackInsert_HandlerReceivesCorrectChange()
    {
        var tcs = new TaskCompletionSource<EntityChange>();

        var (subscriber, cts, _) = StartSubscriber(s =>
            s.OnChange<Order>(ChangeType.Insert, (change, _) =>
            {
                tcs.TrySetResult(change);
                return Task.CompletedTask;
            }));

        await _tracker.TrackInsertAsync(new Order { Id = 1, Total = 99.99m });

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(received.EntityId, Is.EqualTo("1"));
        Assert.That(received.ChangeType, Is.EqualTo(ChangeType.Insert));

        cts.Cancel();
        subscriber.Dispose();
    }

    [Test]
    public async Task TrackUpdate_HandlerReceivesCorrectChange()
    {
        var tcs = new TaskCompletionSource<EntityChange>();

        var (subscriber, cts, _) = StartSubscriber(s =>
            s.OnChange<Order>(ChangeType.Update, (change, _) =>
            {
                tcs.TrySetResult(change);
                return Task.CompletedTask;
            }));

        await _tracker.TrackUpdateAsync(new Order { Id = 42, Total = 150m });

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(received.EntityId, Is.EqualTo("42"));
        Assert.That(received.ChangeType, Is.EqualTo(ChangeType.Update));

        cts.Cancel();
        subscriber.Dispose();
    }

    [Test]
    public async Task TrackDelete_HandlerReceivesCorrectChange()
    {
        var tcs = new TaskCompletionSource<EntityChange>();

        var (subscriber, cts, _) = StartSubscriber(s =>
            s.OnChange<Order>(ChangeType.Delete, (change, _) =>
            {
                tcs.TrySetResult(change);
                return Task.CompletedTask;
            }));

        await _tracker.TrackDeleteAsync(new Order { Id = 7, Total = 0m });

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(received.EntityId, Is.EqualTo("7"));
        Assert.That(received.ChangeType, Is.EqualTo(ChangeType.Delete));

        cts.Cancel();
        subscriber.Dispose();
    }

    [Test]
    public async Task OnChange_WithNullChangeType_ReceivesAllTypes()
    {
        var received = new List<ChangeType>();
        var allThree = new TaskCompletionSource<bool>();

        var (subscriber, cts, _) = StartSubscriber(s =>
            s.OnChange<Order>(changeType: null, (change, _) =>
            {
                lock (received) received.Add(change.ChangeType);
                if (received.Count == 3) allThree.TrySetResult(true);
                return Task.CompletedTask;
            }));

        await _tracker.TrackInsertAsync(new Order { Id = 1, Total = 10m });
        await _tracker.TrackUpdateAsync(new Order { Id = 2, Total = 20m });
        await _tracker.TrackDeleteAsync(new Order { Id = 3, Total = 30m });

        await allThree.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(received, Is.EquivalentTo(new[]
        {
            ChangeType.Insert, ChangeType.Update, ChangeType.Delete
        }));

        cts.Cancel();
        subscriber.Dispose();
    }

    [Test]
    public async Task Deduplication_DuplicateMessage_InvokedOnce()
    {
        var invokeCount = 0;
        var firstArrived = new TaskCompletionSource<bool>();

        var (subscriber, cts, _) = StartSubscriber(s =>
            s.OnChange<Order>(ChangeType.Insert, (_, _) =>
            {
                Interlocked.Increment(ref invokeCount);
                firstArrived.TrySetResult(true);
                return Task.CompletedTask;
            }));

        // Track once — writes a change with a unique CorrelationId
        var change = await _tracker.TrackInsertAsync(new Order { Id = 1, Total = 5m });

        await firstArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Manually publish the same change again (same CorrelationId)
        await _queue.PublishAsync(new MessageEnvelope
        {
            EntityType    = change.EntityType,
            EntityId      = change.EntityId,
            ChangeType    = change.ChangeType,
            CorrelationId = change.CorrelationId,
            Version       = change.Version,
            Timestamp     = change.Timestamp,
            Payload       = Array.Empty<byte>()
        });
        await Task.Delay(300); // give time for second delivery attempt

        Assert.That(invokeCount, Is.EqualTo(1), "Handler must not be invoked for duplicate CorrelationId");

        cts.Cancel();
        subscriber.Dispose();
    }

    [Test]
    public async Task ChangeSubscriberConfiguration_Build_AppliesOptionsAndDedup()
    {
        // Verifies the deferred-build path: options + dedup store are passed through Build()
        var tcs = new TaskCompletionSource<EntityChange>();

        var config = new ChangeSubscriberConfiguration(new ServiceCollection())
            .ConsumeEntity<Order>()
            .UseInMemoryQueue<Order>(_queue)
            .UseSerializer<Order>(new JsonSerializerPlugin())
            .UseCompressor<Order>(new NoOpCompressorPlugin()) // must match the tracker's compressor
            .OnInsert<Order>((change, _) =>
            {
                tcs.TrySetResult(change);
                return Task.CompletedTask;
            });

        var subscriber = config.Build(options: new SubscriberOptions
        {
            MaxRetries    = 1,
            SkipOnFailure = true
        });

        var cts     = new CancellationTokenSource();
        var consume = Task.Run(() => subscriber.ConsumeFromConsumerAsync(_queue, cts.Token));

        await _tracker.TrackInsertAsync(new Order { Id = 99, Total = 1m });

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(received.EntityId, Is.EqualTo("99"));

        cts.Cancel();
        subscriber.Dispose();
    }

    // -------------------------------------------------------------------------
    // Test entity
    // -------------------------------------------------------------------------
    private class Order
    {
        public int Id { get; set; }
        public decimal Total { get; set; }
    }
}
