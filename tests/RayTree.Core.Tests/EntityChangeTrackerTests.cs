using Moq;
using RayTree.Models;
using RayTree.Plugins;
using RayTree.Tracking;

namespace RayTree.Core.Tests;

public class EntityChangeTrackerTests
{
    [Test]
    public async Task TrackChangeAsync_WritesToOutbox_WhenRegistered()
    {
        var tracker = new EntityChangeTracker();
        var outbox = new Mock<IOutbox>();
        outbox.Setup(o => o.WriteAsync(It.IsAny<EntityChange>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        tracker.RegisterOutbox(typeof(object), outbox.Object);

        var change = new EntityChange
        {
            EntityType = typeof(object).AssemblyQualifiedName!,
            EntityId = "test-id",
            ChangeType = ChangeType.Insert
        };

        await tracker.TrackChangeAsync(change);

        outbox.Verify(o => o.WriteAsync(It.IsAny<EntityChange>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task TrackChangesAsync_SetsSameCorrelationId_OnAllChanges()
    {
        var tracker = new EntityChangeTracker();
        var outbox = new Mock<IOutbox>();
        outbox.Setup(o => o.WriteAsync(It.IsAny<EntityChange>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        tracker.RegisterOutbox(typeof(object), outbox.Object);

        var changes = new[]
        {
            new EntityChange { EntityType = typeof(object).AssemblyQualifiedName!, EntityId = "1", ChangeType = ChangeType.Insert },
            new EntityChange { EntityType = typeof(object).AssemblyQualifiedName!, EntityId = "2", ChangeType = ChangeType.Update }
        };

        await tracker.TrackChangesAsync(changes);

        Assert.That(changes[0].CorrelationId, Is.EqualTo(changes[1].CorrelationId));
    }

    [Test]
    public async Task TrackChangesAsync_WritesAllChanges_ToOutbox()
    {
        var tracker = new EntityChangeTracker();
        var outbox = new Mock<IOutbox>();
        outbox.Setup(o => o.WriteAsync(It.IsAny<EntityChange>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        tracker.RegisterOutbox(typeof(object), outbox.Object);

        var changes = new[]
        {
            new EntityChange { EntityType = typeof(object).AssemblyQualifiedName!, EntityId = "1", ChangeType = ChangeType.Insert },
            new EntityChange { EntityType = typeof(object).AssemblyQualifiedName!, EntityId = "2", ChangeType = ChangeType.Update },
            new EntityChange { EntityType = typeof(object).AssemblyQualifiedName!, EntityId = "3", ChangeType = ChangeType.Delete }
        };

        await tracker.TrackChangesAsync(changes);

        outbox.Verify(o => o.WriteAsync(It.IsAny<EntityChange>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Test]
    public void RegisterOutbox_AddsOutbox_ForEntityType()
    {
        var tracker = new EntityChangeTracker();
        var outbox = new Mock<IOutbox>().Object;

        tracker.RegisterOutbox(typeof(object), outbox);

        Assert.That(tracker.GetOutbox(typeof(object)), Is.SameAs(outbox));
    }

    [Test]
    public void RegisterPublisher_AddsPublisher_ForEntityType()
    {
        var tracker = new EntityChangeTracker();
        var publisher = new Mock<IQueuePublisher>().Object;

        tracker.RegisterPublisher(typeof(object), publisher);

        Assert.That(tracker.GetPublisher(typeof(object)), Is.SameAs(publisher));
    }

    [Test]
    public void GetOutboxes_ReturnsAllRegisteredOutboxes()
    {
        var tracker = new EntityChangeTracker();
        var outbox1 = new Mock<IOutbox>().Object;
        var outbox2 = new Mock<IOutbox>().Object;

        tracker.RegisterOutbox(typeof(string), outbox1);
        tracker.RegisterOutbox(typeof(int), outbox2);

        var outboxes = tracker.GetOutboxes();

        Assert.That(outboxes, Has.Count.EqualTo(2));
        Assert.That(outboxes.ContainsKey(typeof(string)), Is.True);
        Assert.That(outboxes.ContainsKey(typeof(int)), Is.True);
    }

    [Test]
    public void TrackChangeAsync_Throws_WhenNoOutboxRegistered()
    {
        var tracker = new EntityChangeTracker();
        var change = new EntityChange
        {
            EntityType = typeof(object).AssemblyQualifiedName!,
            EntityId = "test-id",
            ChangeType = ChangeType.Insert
        };

        Assert.ThrowsAsync<InvalidOperationException>(async () => await tracker.TrackChangeAsync(change));
    }
}

public class EntityChangeTests
{
    [Test]
    public void NewEntityChange_HasDefaultValues()
    {
        var change = new EntityChange();

        Assert.That(change.Id, Is.EqualTo(0));
        Assert.That(change.EntityType, Is.EqualTo(string.Empty));
        Assert.That(change.EntityId, Is.EqualTo(string.Empty));
        Assert.That(change.ChangeType, Is.EqualTo(ChangeType.Insert));
        Assert.That(change.Version, Is.EqualTo(1));
        Assert.That(change.Published, Is.False);
        Assert.That(change.CorrelationId, Is.Not.EqualTo(Guid.Empty));
    }

    [Test]
    public void NewEntityChange_Timestamp_IsUtcNow()
    {
        var before = DateTime.UtcNow;

        var change = new EntityChange();

        var after = DateTime.UtcNow;
        Assert.That(change.Timestamp, Is.GreaterThanOrEqualTo(before));
        Assert.That(change.Timestamp, Is.LessThanOrEqualTo(after));
    }

    [Test]
    public void EntityChange_CanSetAllProperties()
    {
        var change = new EntityChange
        {
            Id = 42,
            EntityType = "TestEntity",
            EntityId = "e-123",
            ChangeType = ChangeType.Update,
            Version = 5,
            Published = true,
            CorrelationId = Guid.NewGuid()
        };

        Assert.That(change.Id, Is.EqualTo(42));
        Assert.That(change.EntityType, Is.EqualTo("TestEntity"));
        Assert.That(change.EntityId, Is.EqualTo("e-123"));
        Assert.That(change.ChangeType, Is.EqualTo(ChangeType.Update));
        Assert.That(change.Version, Is.EqualTo(5));
        Assert.That(change.Published, Is.True);
    }
}

public class ChangeTypeTests
{
    [Test]
    public void ChangeType_ContainsExpectedValues()
    {
        var names = Enum.GetNames(typeof(ChangeType));

        Assert.That(names, Does.Contain("Insert"));
        Assert.That(names, Does.Contain("Update"));
        Assert.That(names, Does.Contain("Delete"));
    }

    [Test]
    public void ChangeType_Insert_IsZero()
    {
        Assert.That((int)ChangeType.Insert, Is.EqualTo(0));
    }
}
