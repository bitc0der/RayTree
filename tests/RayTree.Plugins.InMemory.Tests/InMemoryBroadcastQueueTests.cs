using RayTree.Core.Models;
using RayTree.Core.Tracking;

namespace RayTree.Plugins.InMemory.Tests;

/// <summary>
/// Unit tests for <see cref="InMemoryBroadcastQueue"/> fan-out delivery.
/// Task 5.4.
/// </summary>
public class InMemoryBroadcastQueueTests
{
    private static MessageEnvelope MakeEnvelope(ChangeType changeType = ChangeType.Insert)
        => new()
        {
            EntityType    = "TestEntity",
            EntityId      = "1",
            ChangeType    = changeType,
            CorrelationId = Guid.NewGuid(),
            Payload       = Array.Empty<byte>(),
        };

    // -------------------------------------------------------------------------
    // Fan-out: every subscriber receives every message
    // -------------------------------------------------------------------------

    [Test]
    public async Task Subscribe_TwoSubscribers_BothReceiveEveryMessage()
    {
        using var broadcast = new InMemoryBroadcastQueue();
        var consumerA = broadcast.Subscribe();
        var consumerB = broadcast.Subscribe();

        var env1 = MakeEnvelope();
        var env2 = MakeEnvelope(ChangeType.Update);

        await broadcast.PublishAsync(env1);
        await broadcast.PublishAsync(env2);
        broadcast.Complete();

        var receivedA = await consumerA.ConsumeAsync()
            .ToListAsync(timeout: TimeSpan.FromSeconds(2));
        var receivedB = await consumerB.ConsumeAsync()
            .ToListAsync(timeout: TimeSpan.FromSeconds(2));

        Assert.That(receivedA, Has.Count.EqualTo(2));
        Assert.That(receivedB, Has.Count.EqualTo(2));
        Assert.That(receivedA.Select(e => e.CorrelationId),
            Is.EquivalentTo(receivedB.Select(e => e.CorrelationId)));
    }

    // -------------------------------------------------------------------------
    // Late subscriber: only receives messages published after Subscribe()
    // -------------------------------------------------------------------------

    [Test]
    public async Task Subscribe_LateSubscriber_OnlyReceivesMessagesPublishedAfterSubscription()
    {
        using var broadcast = new InMemoryBroadcastQueue();
        var earlyConsumer = broadcast.Subscribe();

        var env1 = MakeEnvelope();
        await broadcast.PublishAsync(env1);  // before late subscriber

        var lateConsumer = broadcast.Subscribe();

        var env2 = MakeEnvelope(ChangeType.Delete);
        await broadcast.PublishAsync(env2);  // after late subscriber
        broadcast.Complete();

        var earlyReceived = await earlyConsumer.ConsumeAsync()
            .ToListAsync(timeout: TimeSpan.FromSeconds(2));
        var lateReceived  = await lateConsumer.ConsumeAsync()
            .ToListAsync(timeout: TimeSpan.FromSeconds(2));

        Assert.That(earlyReceived, Has.Count.EqualTo(2),   "Early subscriber should receive both messages");
        Assert.That(lateReceived,  Has.Count.EqualTo(1),   "Late subscriber should only receive the message after it subscribed");
        Assert.That(lateReceived[0].CorrelationId, Is.EqualTo(env2.CorrelationId));
    }

    // -------------------------------------------------------------------------
    // Disposed subscriber: removed from fan-out; further publishes don't throw
    // -------------------------------------------------------------------------

    [Test]
    public async Task Dispose_Subscriber_SubsequentPublishDoesNotDeliverToDisposed()
    {
        using var broadcast = new InMemoryBroadcastQueue();

        var consumerA = broadcast.Subscribe();   // IQueueConsumer (also IDisposable)
        var consumerB = broadcast.Subscribe();

        // Dispose the first subscriber via the IDisposable interface
        ((IDisposable)consumerA).Dispose();

        // Publish after disposal — should not throw
        var env = MakeEnvelope();
        Assert.DoesNotThrowAsync(async () => await broadcast.PublishAsync(env));

        broadcast.Complete();

        var receivedB = await consumerB.ConsumeAsync()
            .ToListAsync(timeout: TimeSpan.FromSeconds(2));
        Assert.That(receivedB, Has.Count.EqualTo(1));
    }

    // -------------------------------------------------------------------------
    // Concurrent publish/subscribe: thread safety
    // -------------------------------------------------------------------------

    [Test]
    public async Task ConcurrentPublishAndSubscribe_AllMessagesDelivered()
    {
        using var broadcast = new InMemoryBroadcastQueue();

        const int messageCount    = 100;
        const int subscriberCount = 4;

        // Subscribe before publishing
        var consumers = Enumerable.Range(0, subscriberCount)
            .Select(_ => broadcast.Subscribe())
            .ToList();

        // Publish concurrently
        await Parallel.ForEachAsync(
            Enumerable.Range(0, messageCount),
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            async (_, ct) => await broadcast.PublishAsync(MakeEnvelope(), ct));

        broadcast.Complete();

        foreach (var consumer in consumers)
        {
            var received = await consumer.ConsumeAsync()
                .ToListAsync(timeout: TimeSpan.FromSeconds(5));
            Assert.That(received, Has.Count.EqualTo(messageCount),
                "Every subscriber should receive every message");
        }
    }

    // -------------------------------------------------------------------------
    // Complete before subscribe: late subscriber's ConsumeAsync ends immediately
    // -------------------------------------------------------------------------

    [Test]
    public async Task Subscribe_AfterComplete_ConsumeAsyncEndsImmediately()
    {
        using var broadcast = new InMemoryBroadcastQueue();
        broadcast.Complete();

        var consumer = broadcast.Subscribe();

        var received = await consumer.ConsumeAsync()
            .ToListAsync(timeout: TimeSpan.FromSeconds(2));

        Assert.That(received, Is.Empty);
    }
}

/// <summary>
/// Drain helper — collects all items from an async enumerable, stopping when
/// the source completes or the timeout elapses.
/// </summary>
internal static class AsyncEnumerableExtensions
{
    public static async Task<List<T>> ToListAsync<T>(
        this IAsyncEnumerable<T> source,
        TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var list = new List<T>();
        try
        {
            await foreach (var item in source.WithCancellation(cts.Token))
                list.Add(item);
        }
        catch (OperationCanceledException)
        {
            // timeout — return what we have so far
        }
        return list;
    }
}
