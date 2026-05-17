using RayTree.Core.Models;
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
        // Arrange
        var outbox = new InMemoryOutbox();
        using var tracker = EntityChangeTracker.Create()
            .ForEntity<User>(e => e
                .UseOutbox(outbox)
                .UsePublisher(new InMemoryQueue())
                .UseSerializer(new JsonSerializerPlugin())
                .UseCompressor(new NoOpCompressorPlugin()))
            .Build();

        // Act
        await tracker.TrackInsertAsync(new User { Id = 1, Name = "Alice" });

        // Assert
        Assert.That(outbox.GetAll(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Pipeline_WithCompression_RoundTripSucceeds()
    {
        // Arrange
        var outbox = new InMemoryOutbox();
        using var tracker = EntityChangeTracker.Create()
            .ForEntity<Order>(e => e
                .UseOutbox(outbox)
                .UsePublisher(new InMemoryQueue())
                .UseSerializer(new JsonSerializerPlugin())
                .UseCompressor(new GzipCompressorPlugin()))
            .Build();

        // Act
        await tracker.TrackUpdateAsync(new Order { Id = 100, Total = 99.99m });

        // Assert
        Assert.That(outbox.GetAll(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task MultipleEntities_TrackedIndependently()
    {
        // Arrange
        var userOutbox = new InMemoryOutbox();
        var orderOutbox = new InMemoryOutbox();
        using var tracker = EntityChangeTracker.Create()
            .ForEntity<User>(e => e.UseOutbox(userOutbox))
            .ForEntity<Order>(e => e.UseOutbox(orderOutbox))
            .Build();

        // Act
        await tracker.TrackInsertAsync(new User { Id = 1, Name = "Bob" });
        await tracker.TrackInsertAsync(new Order { Id = 100, Total = 50m });

        // Assert
        Assert.That(userOutbox.GetAll(), Has.Count.EqualTo(1));
        Assert.That(orderOutbox.GetAll(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Pipeline_WritesOutbox_InSameBatch()
    {
        // Arrange
        var outbox = new InMemoryOutbox();
        using var tracker = EntityChangeTracker.Create()
            .ForEntity<User>(e => e
                .UseOutbox(outbox)
                .UseSerializer(new JsonSerializerPlugin())
                .UseCompressor(new NoOpCompressorPlugin()))
            .Build();

        // Act
        await tracker.TrackInsertAsync(new User { Id = 1, Name = "A" });
        await tracker.TrackUpdateAsync(new User { Id = 2, Name = "B" });
        await tracker.TrackDeleteAsync(new User { Id = 3, Name = "C" });

        // Assert
        Assert.That(outbox.GetAll(), Has.Count.EqualTo(3));
    }

    [Test]
    public async Task Pipeline_WithQueue_ConsumerReceivesMessage()
    {
        // Arrange
        var queue = new InMemoryQueue();
        using var tracker = EntityChangeTracker.Create()
            .UsePublisherOptions(o => o.PollingInterval = TimeSpan.FromMilliseconds(50))
            .ForEntity<User>(e => e
                .UseOutbox(new InMemoryOutbox())
                .UsePublisher(queue)
                .UseSerializer(new JsonSerializerPlugin())
                .UseCompressor(new NoOpCompressorPlugin()))
            .Build();

        // Act
        await tracker.TrackInsertAsync(new User { Id = 1, Name = "Charlie" });

        // Assert
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var message = await queue.Reader.ReadAsync(cts.Token);
        Assert.That(message.ChangeType, Is.EqualTo(ChangeType.Insert));
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
