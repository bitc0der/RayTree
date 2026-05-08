using Moq;
using RayTree.Core.Distribution;
using RayTree.Core.Models;
using RayTree.Core.Plugins.Outbox;
using RayTree.Core.Tracking;
using RayTree.Plugins;

namespace RayTree.Core.Tests;

public class OutboxCleanupServiceTests
{
    [Test]
    public async Task RunCleanupAsync_CallsCleanup_OnAllOutboxes()
    {
        var outbox1 = new Mock<IOutbox>();
        outbox1.Setup(o => o.CleanupPublishedAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(5);

        var outbox2 = new Mock<IOutbox>();
        outbox2.Setup(o => o.CleanupPublishedAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);

        var service = new OutboxCleanupService(new[] { outbox1.Object, outbox2.Object }, TimeSpan.FromDays(7));

        var deleted = await service.RunCleanupAsync();

        Assert.That(deleted, Is.EqualTo(8));
        outbox1.Verify(o => o.CleanupPublishedAsync(TimeSpan.FromDays(7), It.IsAny<CancellationToken>()), Times.Once);
        outbox2.Verify(o => o.CleanupPublishedAsync(TimeSpan.FromDays(7), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task RunCleanupAsync_ReturnsZero_WhenNoOutboxes()
    {
        var service = new OutboxCleanupService(Array.Empty<IOutbox>(), TimeSpan.FromDays(7));

        var deleted = await service.RunCleanupAsync();

        Assert.That(deleted, Is.EqualTo(0));
    }

    [Test]
    public async Task RunCleanupAsync_UsesDefaultRetention_WhenNotSpecified()
    {
        var outbox = new Mock<IOutbox>();
        outbox.Setup(o => o.CleanupPublishedAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var service = new OutboxCleanupService(new[] { outbox.Object });

        await service.RunCleanupAsync();

        outbox.Verify(o => o.CleanupPublishedAsync(TimeSpan.FromDays(7), It.IsAny<CancellationToken>()), Times.Once);
    }
}

public class OutboxPublisherServiceTests
{
    [Test]
    public async Task StopAsync_Completes_WithinTimeout()
    {
        var publisher = new ChangePublisher();
        var options = new OutboxPublisherOptions { PollingInterval = TimeSpan.FromSeconds(1) };
        var service = new OutboxPublisherService(publisher, typeof(DummyEntity), options);

        await service.StartAsync();

        await service.StopAsync();

        Assert.Pass();
    }

    [Test]
    public void Dispose_DoesNotThrow()
    {
        var publisher = new ChangePublisher();
        var options = new OutboxPublisherOptions { PollingInterval = TimeSpan.FromHours(1) };
        var service = new OutboxPublisherService(publisher, typeof(DummyEntity), options);

        Assert.DoesNotThrow(() => service.Dispose());
    }

    private class DummyEntity { public int Id { get; set; } }
}

public class ConcurrentChangeDetectionTests
{
    private class SampleEntity
    {
        public int Id { get; set; }
    }

    [Test]
    public async Task TrackChangeAsync_IsThreadSafe_WithConcurrentCalls()
    {
        var outbox = new Mock<IOutbox>();
        outbox.Setup(o => o.WriteAsync(It.IsAny<EntityChange<SampleEntity>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var publisher = new ChangePublisher();
        publisher.RegisterOutbox(typeof(SampleEntity), outbox.Object);
        var tracker = new EntityChangeTracker(publisher);

        var tasks = Enumerable.Range(0, 100).Select(async i =>
        {
            var change = new EntityChange<SampleEntity>
            {
                EntityType = typeof(SampleEntity).FullName!,
                EntityId = i.ToString(),
                ChangeType = ChangeType.Insert,
                State = new SampleEntity { Id = i }
            };
            await tracker.TrackChangeAsync(change);
        });

        await Task.WhenAll(tasks);

        outbox.Verify(o => o.WriteAsync(It.IsAny<EntityChange<SampleEntity>>(), It.IsAny<CancellationToken>()), Times.Exactly(100));
    }
}
