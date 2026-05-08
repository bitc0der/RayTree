using Moq;
using RayTree.Core.Distribution;
using RayTree.Core.Models;
using RayTree.Core.Plugins.Outbox;
using RayTree.Core.Tracking;
using RayTree.Plugins;

namespace RayTree.Core.Tests;

public class EntityChangeTrackerTests
{
    private class SampleEntity
    {
        public int Id { get; set; }
        public string? Name { get; set; }
    }

    [Test]
    public async Task TrackChangeAsync_WritesToOutbox_WhenRegistered()
    {
        var outbox = new Mock<IOutbox>();
        outbox.Setup(o => o.WriteAsync(It.IsAny<EntityChange<SampleEntity>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var publisher = new ChangePublisher();
        publisher.RegisterOutbox(typeof(SampleEntity), outbox.Object);

        var tracker = new EntityChangeTracker(publisher);

        var change = new EntityChange<SampleEntity>
        {
            EntityType = typeof(SampleEntity).FullName!,
            EntityId = "1",
            ChangeType = ChangeType.Insert,
            State = new SampleEntity { Id = 1, Name = "Test" }
        };

        await tracker.TrackChangeAsync(change);

        outbox.Verify(o => o.WriteAsync(It.IsAny<EntityChange<SampleEntity>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public void TrackChangeAsync_Throws_WhenNoOutboxRegistered()
    {
        var tracker = new EntityChangeTracker(new ChangePublisher());

        var change = new EntityChange<SampleEntity>
        {
            EntityType = typeof(SampleEntity).FullName!,
            EntityId = "1",
            ChangeType = ChangeType.Insert,
            State = new SampleEntity { Id = 1 }
        };

        Assert.ThrowsAsync<InvalidOperationException>(async () => await tracker.TrackChangeAsync(change));
    }
}
