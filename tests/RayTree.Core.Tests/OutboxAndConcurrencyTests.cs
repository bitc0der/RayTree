using Moq;
using RayTree.Core.Tracking;
using RayTree.Distribution;
using RayTree.Models;
using RayTree.Plugins;
using RayTree.Tracking;

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
        var tracker = new EntityChangeTracker();
        var options = new OutboxPublisherOptions { PollingInterval = TimeSpan.FromSeconds(1) };
        var service = new OutboxPublisherService(tracker, typeof(DummyEntity), options);

        await service.StartAsync();

        await service.StopAsync();

        Assert.Pass();
    }

    [Test]
    public void Dispose_DoesNotThrow()
    {
        var tracker = new EntityChangeTracker();
        var options = new OutboxPublisherOptions { PollingInterval = TimeSpan.FromHours(1) };
        var service = new OutboxPublisherService(tracker, typeof(DummyEntity), options);

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
        var tracker = new EntityChangeTracker();
        var outbox = new Mock<IOutbox>();
        outbox.Setup(o => o.WriteAsync(It.IsAny<EntityChange<SampleEntity>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        tracker.RegisterOutbox(typeof(SampleEntity), outbox.Object);

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

    [Test]
    public void RegisterAndGetOutbox_IsThreadSafe_WithConcurrentAccess()
    {
        var tracker = new EntityChangeTracker();
        var outboxes = Enumerable.Range(0, 50).Select(i => new Mock<IOutbox>().Object).ToArray();
        var types = Enumerable.Range(0, 50).Select(_ => typeof(SampleEntity)).ToArray();

        var registerTask = Task.Run(() =>
        {
            Parallel.For(0, outboxes.Length, i =>
            {
                tracker.RegisterOutbox(types[i], outboxes[i]);
            });
        });

        registerTask.Wait();

        var getTask = Task.Run(() =>
        {
            Parallel.For(0, outboxes.Length, i =>
            {
                tracker.GetOutbox(types[i]);
            });
        });

        Assert.DoesNotThrowAsync(async () => await getTask);
    }
}
