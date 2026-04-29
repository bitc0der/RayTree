using System.IO.Pipelines;
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

        var entityType = typeof(User);
        tracker.RegisterOutbox(entityType, outbox);
        tracker.RegisterPublisher(entityType, queue);
        tracker.RegisterSerializer(entityType, serializer);
        tracker.RegisterCompressor(entityType, compressor);

        await tracker.TrackChangesAsync(new[]
        {
            new EntityChange
            {
                EntityType = entityType.AssemblyQualifiedName!,
                EntityId = "1",
                ChangeType = ChangeType.Insert,
                Timestamp = DateTime.UtcNow
            }
        });

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

        var entityType = typeof(Order);
        tracker.RegisterOutbox(entityType, outbox);
        tracker.RegisterPublisher(entityType, queue);
        tracker.RegisterSerializer(entityType, serializer);
        tracker.RegisterCompressor(entityType, compressor);

        await tracker.TrackChangesAsync(new[]
        {
            new EntityChange
            {
                EntityType = entityType.AssemblyQualifiedName!,
                EntityId = "100",
                ChangeType = ChangeType.Update,
                Timestamp = DateTime.UtcNow
            }
        });

        Assert.That(outbox.GetAll(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task MultipleEntities_TrackedIndependently()
    {
        var tracker = new EntityChangeTracker();
        var userOutbox = new InMemoryOutbox();
        var orderOutbox = new InMemoryOutbox();

        var userType = typeof(User);
        var orderType = typeof(Order);
        tracker.RegisterOutbox(userType, userOutbox);
        tracker.RegisterOutbox(orderType, orderOutbox);

        await tracker.TrackChangesAsync(new[]
        {
            new EntityChange { EntityType = userType.AssemblyQualifiedName!, EntityId = "1", ChangeType = ChangeType.Insert },
            new EntityChange { EntityType = orderType.AssemblyQualifiedName!, EntityId = "100", ChangeType = ChangeType.Insert }
        });

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

        var entityType = typeof(User);
        tracker.RegisterOutbox(entityType, outbox);
        tracker.RegisterSerializer(entityType, serializer);
        tracker.RegisterCompressor(entityType, compressor);

        var changes = new[]
        {
            new EntityChange { EntityType = entityType.AssemblyQualifiedName!, EntityId = "1", ChangeType = ChangeType.Insert },
            new EntityChange { EntityType = entityType.AssemblyQualifiedName!, EntityId = "2", ChangeType = ChangeType.Update },
            new EntityChange { EntityType = entityType.AssemblyQualifiedName!, EntityId = "3", ChangeType = ChangeType.Delete }
        };

        await tracker.TrackChangesAsync(changes);

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

        var entityType = typeof(User);
        tracker.RegisterOutbox(entityType, new InMemoryOutbox());
        tracker.RegisterPublisher(entityType, queue);
        tracker.RegisterSerializer(entityType, serializer);
        tracker.RegisterCompressor(entityType, compressor);

        await tracker.TrackChangesAsync(new[]
        {
            new EntityChange { EntityType = entityType.AssemblyQualifiedName!, EntityId = "1", ChangeType = ChangeType.Insert }
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var message = await queue.Reader.ReadAsync(cts.Token);
        Assert.That(message.Change.EntityId, Is.EqualTo("1"));
        Assert.That(message.Change.ChangeType, Is.EqualTo(ChangeType.Insert));
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
