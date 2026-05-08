using RayTree.Core.Distribution;
using RayTree.Core.Handling;
using RayTree.Core.Models;
using RayTree.Core.Plugins.Compression;
using RayTree.Core.Tracking;
using RayTree.Plugins.InMemory;
using RayTree.Plugins.Serializers.Json;

namespace RayTree.Core.Tests;

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

        var publisher = new ChangePublisher();
        publisher.RegisterOutbox(typeof(Order), new InMemoryOutbox());
        publisher.RegisterPublisher(typeof(Order), _queue);
        publisher.RegisterSerializer(typeof(Order), new JsonSerializerPlugin());
        publisher.RegisterCompressor(typeof(Order), new NoOpCompressorPlugin());
        publisher.Options.PollingInterval = TimeSpan.FromMilliseconds(50);

        _tracker = new EntityChangeTracker(publisher);
        await _tracker.InitializeAsync();
    }

    [TearDown]
    public void TearDown()
    {
        _tracker.Dispose();
        _queue.Dispose();
    }

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

        var change = await _tracker.TrackInsertAsync(new Order { Id = 1, Total = 5m });

        await firstArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));

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
        await Task.Delay(300);

        Assert.That(invokeCount, Is.EqualTo(1), "Handler must not be invoked for duplicate CorrelationId");

        cts.Cancel();
        subscriber.Dispose();
    }

    [Test]
    public async Task ChangeSubscriberBuilder_Build_AppliesOptionsAndDedup()
    {
        var tcs = new TaskCompletionSource<EntityChange>();

        var subscriber = new ChangeSubscriberBuilder()
            .UseOptions(opt =>
            {
                opt.MaxRetries    = 1;
                opt.SkipOnFailure = true;
            })
            .ForEntity<Order>(e => e
                .UseInMemoryQueue(_queue)
                .UseSerializer(new JsonSerializerPlugin())
                .UseCompressor(new NoOpCompressorPlugin())
                .OnInsert((change, _) =>
                {
                    tcs.TrySetResult(change);
                    return Task.CompletedTask;
                }))
            .Build();

        var cts     = new CancellationTokenSource();
        var consume = Task.Run(() => subscriber.ConsumeFromConsumerAsync(_queue, cts.Token));

        await _tracker.TrackInsertAsync(new Order { Id = 99, Total = 1m });

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(received.EntityId, Is.EqualTo("99"));

        cts.Cancel();
        subscriber.Dispose();
    }

    [Test]
    public async Task NoSerializer_HandlerReceivesTypedChangeWithNullState()
    {
        var tcs = new TaskCompletionSource<EntityChange<Order>>();

        var subscriber = new ChangeSubscriber();
        subscriber.RegisterQueue<Order>(_queue);
        subscriber.OnChange<Order>(ChangeType.Insert, (change, _) =>
        {
            tcs.TrySetResult(change);
            return Task.CompletedTask;
        });

        var cts     = new CancellationTokenSource();
        var consume = Task.Run(() => subscriber.ConsumeFromConsumerAsync(_queue, cts.Token));

        await _tracker.TrackInsertAsync(new Order { Id = 55, Total = 9.99m });

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(received.EntityId,   Is.EqualTo("55"));
        Assert.That(received.ChangeType, Is.EqualTo(ChangeType.Insert));
        Assert.That(received.State,      Is.Null, "State must be null when no serializer is registered");

        cts.Cancel();
        subscriber.Dispose();
    }

    [Test]
    public async Task InvokeWithRetry_HandlerSucceedsAfterRetries()
    {
        var attempts = 0;
        var succeeded = new TaskCompletionSource<bool>();

        var subscriber = new ChangeSubscriber(options: new SubscriberOptions
        {
            MaxRetries = 2,
            RetryDelay = TimeSpan.FromMilliseconds(10)
        });
        subscriber
            .UseSerializer<Order>(new JsonSerializerPlugin())
            .RegisterQueue<Order>(_queue)
            .OnChange<Order>(null, (_, _) =>
            {
                var n = Interlocked.Increment(ref attempts);
                if (n < 3) throw new InvalidOperationException("transient");
                succeeded.TrySetResult(true);
                return Task.CompletedTask;
            });

        var cts     = new CancellationTokenSource();
        var consume = Task.Run(() => subscriber.ConsumeFromConsumerAsync(_queue, cts.Token));

        await _tracker.TrackInsertAsync(new Order { Id = 10, Total = 1m });

        await succeeded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(attempts, Is.EqualTo(3));

        cts.Cancel();
        subscriber.Dispose();
    }

    [Test]
    public async Task InvokeWithRetry_MaxRetries1_ExhaustedWithSkipOnFailure_DoesNotThrow()
    {
        var attempts = 0;
        var secondAttempt = new TaskCompletionSource<bool>();

        var subscriber = new ChangeSubscriber(options: new SubscriberOptions
        {
            MaxRetries    = 1,
            RetryDelay    = TimeSpan.FromMilliseconds(10),
            SkipOnFailure = true
        });
        subscriber
            .UseSerializer<Order>(new JsonSerializerPlugin())
            .RegisterQueue<Order>(_queue)
            .OnChange<Order>(null, (_, _) =>
            {
                var n = Interlocked.Increment(ref attempts);
                if (n == 2) secondAttempt.TrySetResult(true);
                throw new InvalidOperationException("always fails");
            });

        var cts     = new CancellationTokenSource();
        var consume = Task.Run(() => subscriber.ConsumeFromConsumerAsync(_queue, cts.Token));

        await _tracker.TrackInsertAsync(new Order { Id = 20, Total = 2m });

        await secondAttempt.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(200);

        Assert.That(attempts,     Is.EqualTo(2), "Should attempt exactly 1 initial + 1 retry");
        Assert.That(consume.IsFaulted, Is.False, "SkipOnFailure must prevent exception from bubbling");

        cts.Cancel();
        subscriber.Dispose();
    }

    private class Order
    {
        public int Id { get; set; }
        public decimal Total { get; set; }
    }
}
