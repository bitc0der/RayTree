using System.IO.Pipelines;
using RayTree.Core.Tracking;
using RayTree.Models;
using RayTree.Plugins.Serializers.Json;
using RayTree.Plugins.Compressors.Gzip;
using RayTree.Tracking;

namespace RayTree.Plugins.InMemory.Tests;

public class EndToEndTests
{
    [Test]
    public async Task FullPipeline_TracksChange_WritesToOutbox_AndPublishesToQueue()
    {
        var tracker = new EntityChangeTracker();
        var outbox = new InMemoryOutbox();
        var queue = new InMemoryQueue();
        var serializer = new JsonSerializerPlugin();
        var compressor = new NoOpCompressorPlugin();

        tracker.RegisterOutbox(typeof(User), outbox);
        tracker.RegisterPublisher(typeof(User), queue);
        tracker.RegisterSerializer(typeof(User), serializer);
        tracker.RegisterCompressor(typeof(User), compressor);

        await tracker.TrackInsertAsync(new User { Id = 1, Name = "Alice" });

        Assert.That(outbox.GetAll(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Pipeline_WithCompression_RoundTripSucceeds()
    {
        var tracker = new EntityChangeTracker();
        var outbox = new InMemoryOutbox();
        var queue = new InMemoryQueue();
        var serializer = new JsonSerializerPlugin();
        var compressor = new GzipCompressorPlugin();

        tracker.RegisterOutbox(typeof(Order), outbox);
        tracker.RegisterPublisher(typeof(Order), queue);
        tracker.RegisterSerializer(typeof(Order), serializer);
        tracker.RegisterCompressor(typeof(Order), compressor);

        await tracker.TrackUpdateAsync(new Order { Id = 100, Total = 99.99m });

        Assert.That(outbox.GetAll(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task MultipleEntities_TrackedIndependently()
    {
        var tracker = new EntityChangeTracker();
        var userOutbox = new InMemoryOutbox();
        var orderOutbox = new InMemoryOutbox();

        tracker.RegisterOutbox(typeof(User), userOutbox);
        tracker.RegisterOutbox(typeof(Order), orderOutbox);

        await tracker.TrackInsertAsync(new User { Id = 1, Name = "Bob" });
        await tracker.TrackInsertAsync(new Order { Id = 100, Total = 50m });

        Assert.That(userOutbox.GetAll(), Has.Count.EqualTo(1));
        Assert.That(orderOutbox.GetAll(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Pipeline_WritesOutbox_InSameBatch()
    {
        var tracker = new EntityChangeTracker();
        var outbox = new InMemoryOutbox();
        var serializer = new JsonSerializerPlugin();
        var compressor = new NoOpCompressorPlugin();

        tracker.RegisterOutbox(typeof(User), outbox);
        tracker.RegisterSerializer(typeof(User), serializer);
        tracker.RegisterCompressor(typeof(User), compressor);

        await tracker.TrackInsertAsync(new User { Id = 1, Name = "A" });
        await tracker.TrackUpdateAsync(new User { Id = 2, Name = "B" });
        await tracker.TrackDeleteAsync(new User { Id = 3, Name = "C" });

        var stored = outbox.GetAll();
        Assert.That(stored, Has.Count.EqualTo(3));
    }

    [Test]
    public async Task Pipeline_WithQueue_ConsumerReceivesMessage()
    {
        var tracker = new EntityChangeTracker();
        var queue = new InMemoryQueue();
        var serializer = new JsonSerializerPlugin();
        var compressor = new NoOpCompressorPlugin();

        tracker.RegisterOutbox(typeof(User), new InMemoryOutbox());
        tracker.RegisterPublisher(typeof(User), queue);
        tracker.RegisterSerializer(typeof(User), serializer);
        tracker.RegisterCompressor(typeof(User), compressor);

        tracker.PublisherOptions.PollingInterval = TimeSpan.FromMilliseconds(50);
        await tracker.InitializeAsync();

        await tracker.TrackInsertAsync(new User { Id = 1, Name = "Charlie" });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var message = await queue.Reader.ReadAsync(cts.Token);
        Assert.That(message.Change.ChangeType, Is.EqualTo(ChangeType.Insert));

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
