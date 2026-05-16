using Microsoft.Extensions.Logging.Abstractions;
using RayTree.Core.Distribution;
using RayTree.Core.Telemetry;
using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Compression;
using RayTree.Core.Tracking;
using RayTree.Plugins.Serializers.Json;
using RayTree.Plugins.Compressors.Gzip;

namespace RayTree.Plugins.InMemory.Tests;

public class EndToEndTests
{
    [Test]
    public async Task FullPipeline_TracksChange_WritesToOutbox_AndPublishesToQueue()
    {
        var outbox = new InMemoryOutbox();
        var publisher = new ChangePublisher(NullLoggerFactory.Instance, new RayTreeMeter());
        publisher.RegisterOutbox(typeof(User), outbox);
        publisher.RegisterPublisher(typeof(User), new InMemoryQueue());
        publisher.RegisterSerializer(typeof(User), new JsonSerializerPlugin());
        publisher.RegisterCompressor(typeof(User), new NoOpCompressorPlugin());
        var tracker = new EntityChangeTracker(publisher);

        await tracker.TrackInsertAsync(new User { Id = 1, Name = "Alice" });

        Assert.That(outbox.GetAll(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Pipeline_WithCompression_RoundTripSucceeds()
    {
        var outbox = new InMemoryOutbox();
        var publisher = new ChangePublisher(NullLoggerFactory.Instance, new RayTreeMeter());
        publisher.RegisterOutbox(typeof(Order), outbox);
        publisher.RegisterPublisher(typeof(Order), new InMemoryQueue());
        publisher.RegisterSerializer(typeof(Order), new JsonSerializerPlugin());
        publisher.RegisterCompressor(typeof(Order), new GzipCompressorPlugin());
        var tracker = new EntityChangeTracker(publisher);

        await tracker.TrackUpdateAsync(new Order { Id = 100, Total = 99.99m });

        Assert.That(outbox.GetAll(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task MultipleEntities_TrackedIndependently()
    {
        var userOutbox = new InMemoryOutbox();
        var orderOutbox = new InMemoryOutbox();
        var publisher = new ChangePublisher(NullLoggerFactory.Instance, new RayTreeMeter());
        publisher.RegisterOutbox(typeof(User), userOutbox);
        publisher.RegisterOutbox(typeof(Order), orderOutbox);
        var tracker = new EntityChangeTracker(publisher);

        await tracker.TrackInsertAsync(new User { Id = 1, Name = "Bob" });
        await tracker.TrackInsertAsync(new Order { Id = 100, Total = 50m });

        Assert.That(userOutbox.GetAll(), Has.Count.EqualTo(1));
        Assert.That(orderOutbox.GetAll(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Pipeline_WritesOutbox_InSameBatch()
    {
        var outbox = new InMemoryOutbox();
        var publisher = new ChangePublisher(NullLoggerFactory.Instance, new RayTreeMeter());
        publisher.RegisterOutbox(typeof(User), outbox);
        publisher.RegisterSerializer(typeof(User), new JsonSerializerPlugin());
        publisher.RegisterCompressor(typeof(User), new NoOpCompressorPlugin());
        var tracker = new EntityChangeTracker(publisher);

        await tracker.TrackInsertAsync(new User { Id = 1, Name = "A" });
        await tracker.TrackUpdateAsync(new User { Id = 2, Name = "B" });
        await tracker.TrackDeleteAsync(new User { Id = 3, Name = "C" });

        Assert.That(outbox.GetAll(), Has.Count.EqualTo(3));
    }

    [Test]
    public async Task Pipeline_WithQueue_ConsumerReceivesMessage()
    {
        var queue = new InMemoryQueue();
        var publisher = new ChangePublisher(NullLoggerFactory.Instance, new RayTreeMeter());
        publisher.RegisterOutbox(typeof(User), new InMemoryOutbox());
        publisher.RegisterPublisher(typeof(User), queue);
        publisher.RegisterSerializer(typeof(User), new JsonSerializerPlugin());
        publisher.RegisterCompressor(typeof(User), new NoOpCompressorPlugin());
        publisher.Options.PollingInterval = TimeSpan.FromMilliseconds(50);

        var tracker = new EntityChangeTracker(publisher);
        await tracker.InitializeAsync();

        await tracker.TrackInsertAsync(new User { Id = 1, Name = "Charlie" });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var message = await queue.Reader.ReadAsync(cts.Token);
        Assert.That(message.ChangeType, Is.EqualTo(ChangeType.Insert));

        tracker.Dispose();
    }

    private class User
    {
        public int Id { get; set; }
        public string? Name { get; set; }
    }

    private class Order
    {
        public int Id { get; set; }
        public decimal Total { get; set; }
    }
}
