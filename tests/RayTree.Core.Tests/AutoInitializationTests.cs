using System.IO.Pipelines;
using NUnit.Framework;
using Moq;
using RayTree.Plugins;
using RayTree.Plugins.InMemory;
using RayTree.Tracking;
using RayTree.Models;
using RayTree.Plugins.Serializers.Json;
using RayTree.Plugins.Compressors.Gzip;

namespace RayTree.Core.Tests;

[TestFixture]
public class AutoInitializationTests
{
    [Test]
    public async Task BuildAsync_ShouldInitializeRepositoriesAndOutboxes()
    {
        // Arrange
        var builder = new ChangeTrackingBuilder();

        builder.ForEntity<AutoInitTestEntity>()
            .UseRepository(new InMemoryRepository<AutoInitTestEntity>())
            .UseOutbox(new InMemoryOutbox())
            .UseQueue(new InMemoryQueue())
            .UseSerializer(new JsonSerializerPlugin())
            .UseCompressor(new GzipCompressorPlugin());

        // Act
        var tracker = await builder.BuildAsync();

        // Assert - should not throw
        Assert.That(tracker, Is.Not.Null);

        // Verify we can track changes
        var change = new EntityChange
        {
            EntityType = typeof(AutoInitTestEntity).AssemblyQualifiedName!,
            ChangeType = ChangeType.Insert,
            Timestamp = DateTime.UtcNow
        };

        await tracker.TrackChangeAsync(change);

        // Verify outbox has the change
        var outbox = tracker.GetOutbox(typeof(AutoInitTestEntity)) as InMemoryOutbox;
        Assert.That(outbox, Is.Not.Null);
        var changes = outbox.GetAll();
        Assert.That(changes, Has.Count.EqualTo(1));
    }

    [Test]
    public void Build_ShouldInitializeRepositoriesAndOutboxes()
    {
        // Arrange
        var builder = new ChangeTrackingBuilder();

        builder.ForEntity<AutoInitTestEntity>()
            .UseRepository(new InMemoryRepository<AutoInitTestEntity>())
            .UseOutbox(new InMemoryOutbox())
            .UseQueue(new InMemoryQueue())
            .UseSerializer(new JsonSerializerPlugin())
            .UseCompressor(new GzipCompressorPlugin());

        // Act - should not throw
        var tracker = builder.Build();

        // Assert
        Assert.That(tracker, Is.Not.Null);
    }

    [Test]
    public void Build_ShouldCallInitializeOnOutboxes()
    {
        // Arrange
        var mockOutbox = new Mock<IOutbox>();
        mockOutbox.Setup(o => o.InitializeAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Verifiable();

        var builder = new ChangeTrackingBuilder();

        builder.ForEntity<AutoInitTestEntity>()
            .UseRepository(new InMemoryRepository<AutoInitTestEntity>())
            .UseOutbox(mockOutbox.Object)
            .UseQueue(new InMemoryQueue())
            .UseSerializer(new JsonSerializerPlugin())
            .UseCompressor(new GzipCompressorPlugin());

        // Act
        var tracker = builder.Build();

        // Assert
        mockOutbox.Verify(o => o.InitializeAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public void Build_ShouldCallInitializeOnQueues()
    {
        // Arrange
        var mockQueue = new Mock<IQueuePublisher>();
        mockQueue.Setup(q => q.InitializeAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Verifiable();

        var builder = new ChangeTrackingBuilder();

        builder.ForEntity<AutoInitTestEntity>()
            .UseRepository(new InMemoryRepository<AutoInitTestEntity>())
            .UseOutbox(new InMemoryOutbox())
            .UseQueue(mockQueue.Object)
            .UseSerializer(new JsonSerializerPlugin())
            .UseCompressor(new GzipCompressorPlugin());

        // Act
        var tracker = builder.Build();

        // Assert
        mockQueue.Verify(q => q.InitializeAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}

public class AutoInitTestEntity
{
    public int Id { get; set; }
}
