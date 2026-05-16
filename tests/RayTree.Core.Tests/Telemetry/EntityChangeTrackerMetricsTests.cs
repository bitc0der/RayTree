using Microsoft.Extensions.Logging.Abstractions;
using RayTree.Core.Distribution;
using RayTree.Core.Plugins.Compression;
using RayTree.Core.Telemetry;
using RayTree.Core.Tracking;
using RayTree.Plugins.InMemory;
using RayTree.Plugins.Serializers.Json;

namespace RayTree.Core.Tests.Telemetry;

[TestFixture]
public class EntityChangeTrackerMetricsTests
{
    private class Order { public int Id { get; set; } public string? Name { get; set; } }

    private static EntityChangeTracker BuildTracker(RayTreeMeter meter, out InMemoryQueue queue)
    {
        var inMemQueue = new InMemoryQueue();
        queue = inMemQueue;
        return new ChangeTrackingBuilder(NullLoggerFactory.Instance)
            .UseMeter(meter)
            .ForEntity<Order>(e => e
                .UseOutbox(new InMemoryOutbox())
                .UsePublisher(inMemQueue)
                .UseConsumer(inMemQueue)
                .UseSerializer(new JsonSerializerPlugin())
                .UseCompressor(new NoOpCompressorPlugin()))
            .Build();
    }

    [Test]
    public async Task TrackInsertAsync_IncrementsWritesCounter_WithInsertTag()
    {
        using var meter = new RayTreeMeter();
        using var collector = new TestMetricsCollector(meter);
        using var tracker = BuildTracker(meter, out _);

        await tracker.TrackInsertAsync(new Order { Id = 1, Name = "a" });

        var writes = collector.Get("raytree.outbox.writes");
        Assert.That(writes, Has.Count.EqualTo(1));
        Assert.That(writes[0].Value, Is.EqualTo(1));
        Assert.That(writes[0].Tags["entity_type"], Is.EqualTo("Order"));
        Assert.That(writes[0].Tags["change_type"], Is.EqualTo("Insert"));
    }

    [Test]
    public async Task TrackUpdateAndDelete_IncrementsWritesWithRespectiveTags()
    {
        using var meter = new RayTreeMeter();
        using var collector = new TestMetricsCollector(meter);
        using var tracker = BuildTracker(meter, out _);

        await tracker.TrackUpdateAsync(new Order { Id = 1 });
        await tracker.TrackDeleteAsync(new Order { Id = 1 });

        var writes = collector.Get("raytree.outbox.writes").OrderBy(m => (string)m.Tags["change_type"]!).ToList();
        Assert.That(writes, Has.Count.EqualTo(2));
        Assert.That(writes.Select(m => m.Tags["change_type"]),
            Is.EquivalentTo(new[] { "Delete", "Update" }));
    }

    [Test]
    public async Task NoListener_FullPublishSubscribePath_NoException()
    {
        // Smoke test (10.15): no MeterListener attached anywhere — instrumentation must be
        // silent across the entire publish/subscribe pipeline, not just the write path.
        var queue = new InMemoryQueue();
        var handlerInvoked = new TaskCompletionSource<bool>();

        using var tracker = new ChangeTrackingBuilder(NullLoggerFactory.Instance)
            // Default publisher polling is 5 s — speed it up so the test doesn't have to wait.
            .UsePublisherOptions(o => o.PollingInterval = TimeSpan.FromMilliseconds(50))
            .ForEntity<Order>(e => e
                .UseOutbox(new InMemoryOutbox())
                .UsePublisher(queue)
                .UseConsumer(queue)
                .UseSerializer(new JsonSerializerPlugin())
                .UseCompressor(new NoOpCompressorPlugin())
                .OnChange(null, (_, _) =>
                {
                    handlerInvoked.TrySetResult(true);
                    return Task.CompletedTask;
                }))
            .Build();

        using var consumerCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        // Start the consumer loop in the background; the test asserts the full pipeline
        // (track → outbox → publish → consume → deserialize → handler dispatch) runs without
        // any instrumentation-related exception when no listener is attached.
        _ = Task.Run(() => tracker.ConsumeFromConsumerAsync(queue, consumerCts.Token), consumerCts.Token);

        Assert.DoesNotThrowAsync(async () =>
        {
            await tracker.TrackInsertAsync(new Order { Id = 1, Name = "a" });
            await tracker.TrackUpdateAsync(new Order { Id = 1, Name = "b" });
            await tracker.TrackDeleteAsync(new Order { Id = 1 });

            // Wait for at least one handler dispatch to confirm the subscriber path executed.
            var first = await Task.WhenAny(handlerInvoked.Task, Task.Delay(4000));
            Assert.That(first, Is.SameAs(handlerInvoked.Task), "handler should have been invoked");
        });
    }
}
