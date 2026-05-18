using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RayTree.Core.Distribution;
using RayTree.Core.Telemetry;
using RayTree.Core.Models;
using RayTree.Core.Plugins.Compression;
using RayTree.Core.Plugins.Outbox;
using RayTree.Core.Plugins.Publisher;
using RayTree.Core.Plugins.Serialization;
using RayTree.Core.Tracking;
using RayTree.Plugins;

namespace RayTree.Core.Tests;

public class OutboxPublisherServiceTests
{
    [Test]
    public async Task StopAsync_Completes_WithinTimeout()
    {
        var publisher = new ChangePublisher(NullLoggerFactory.Instance, new RayTreeMeter());
        var options = new OutboxPublisherOptions { PollingInterval = TimeSpan.FromSeconds(1) };
        var service = new OutboxPublisherService(publisher, typeof(DummyEntity), options, NullLoggerFactory.Instance, publisher.Meter);

        await service.StartAsync();

        await service.StopAsync();

        Assert.Pass();
    }

    [Test]
    public void Dispose_DoesNotThrow()
    {
        var publisher = new ChangePublisher(NullLoggerFactory.Instance, new RayTreeMeter());
        var options = new OutboxPublisherOptions { PollingInterval = TimeSpan.FromHours(1) };
        var service = new OutboxPublisherService(publisher, typeof(DummyEntity), options, NullLoggerFactory.Instance, publisher.Meter);

        Assert.DoesNotThrow(() => service.Dispose());
    }

    [Test]
    public async Task PollLoop_CallsCleanupPublishedAsync_WhenIntervalElapses()
    {
        var cleanupCalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outbox = new Mock<IOutbox>();
        outbox.Setup(o => o.GetUnpublishedAsync<DummyEntity>(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<EntityChange<DummyEntity>>());
        outbox.Setup(o => o.CleanupPublishedAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<TimeSpan, CancellationToken>((_, _) => cleanupCalled.TrySetResult(true))
            .ReturnsAsync(0);

        var publisher = BuildPublisher(outbox.Object);
        var options = new OutboxPublisherOptions
        {
            PollingInterval        = TimeSpan.FromMilliseconds(20),
            CleanupInterval        = TimeSpan.Zero,
            CleanupRetentionPeriod = TimeSpan.FromDays(7)
        };
        var service = new OutboxPublisherService(publisher, typeof(DummyEntity), options, NullLoggerFactory.Instance, publisher.Meter);

        await service.StartAsync();
        var triggered = await Task.WhenAny(cleanupCalled.Task, Task.Delay(5000));
        await service.StopAsync();

        Assert.That(triggered, Is.EqualTo(cleanupCalled.Task), "CleanupPublishedAsync was not called within 5 s");
        outbox.Verify(o => o.CleanupPublishedAsync(TimeSpan.FromDays(7), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Test]
    public async Task PollLoop_CallsCleanupOnce_WhenIntervalHasNotElapsedAgain()
    {
        var firstCleanup = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outbox = new Mock<IOutbox>();
        outbox.Setup(o => o.GetUnpublishedAsync<DummyEntity>(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<EntityChange<DummyEntity>>());
        outbox.Setup(o => o.CleanupPublishedAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<TimeSpan, CancellationToken>((_, _) => firstCleanup.TrySetResult(true))
            .ReturnsAsync(0);

        var publisher = BuildPublisher(outbox.Object);
        var options = new OutboxPublisherOptions
        {
            PollingInterval        = TimeSpan.FromMilliseconds(20),
            CleanupInterval        = TimeSpan.FromHours(1),
            CleanupRetentionPeriod = TimeSpan.FromDays(7)
        };
        var service = new OutboxPublisherService(publisher, typeof(DummyEntity), options, NullLoggerFactory.Instance, publisher.Meter);

        await service.StartAsync();
        // Wait until the eager first-tick cleanup fires, then let a few more poll cycles pass.
        await Task.WhenAny(firstCleanup.Task, Task.Delay(5000));
        await Task.Delay(100);
        await service.StopAsync();

        // Eager first-tick cleanup runs exactly once; subsequent ticks within the 1-hour interval are skipped.
        outbox.Verify(o => o.CleanupPublishedAsync(TimeSpan.FromDays(7), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task PollLoop_CallsStaleUnpublishedCleanup_WhenThresholdIsSet()
    {
        var staleCalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outbox = new Mock<IOutbox>();
        outbox.Setup(o => o.GetUnpublishedAsync<DummyEntity>(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<EntityChange<DummyEntity>>());
        outbox.Setup(o => o.CleanupPublishedAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        outbox.Setup(o => o.CleanupStaleUnpublishedAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<TimeSpan, CancellationToken>((_, _) => staleCalled.TrySetResult(true))
            .ReturnsAsync(0);

        var publisher = BuildPublisher(outbox.Object);
        var options = new OutboxPublisherOptions
        {
            PollingInterval           = TimeSpan.FromMilliseconds(20),
            CleanupInterval           = TimeSpan.Zero,
            CleanupRetentionPeriod    = TimeSpan.FromDays(7),
            StaleUnpublishedThreshold = TimeSpan.FromDays(30)
        };
        var service = new OutboxPublisherService(publisher, typeof(DummyEntity), options, NullLoggerFactory.Instance, publisher.Meter);

        await service.StartAsync();
        var triggered = await Task.WhenAny(staleCalled.Task, Task.Delay(5000));
        await service.StopAsync();

        Assert.That(triggered, Is.EqualTo(staleCalled.Task), "CleanupStaleUnpublishedAsync was not called within 5 s");
        outbox.Verify(o => o.CleanupStaleUnpublishedAsync(TimeSpan.FromDays(30), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Test]
    public async Task PollLoop_DoesNotCallStaleUnpublishedCleanup_WhenThresholdIsNull()
    {
        var publishedCleaned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outbox = new Mock<IOutbox>();
        outbox.Setup(o => o.GetUnpublishedAsync<DummyEntity>(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<EntityChange<DummyEntity>>());
        outbox.Setup(o => o.CleanupPublishedAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<TimeSpan, CancellationToken>((_, _) => publishedCleaned.TrySetResult(true))
            .ReturnsAsync(0);

        var publisher = BuildPublisher(outbox.Object);
        var options = new OutboxPublisherOptions
        {
            PollingInterval           = TimeSpan.FromMilliseconds(20),
            CleanupInterval           = TimeSpan.Zero,
            CleanupRetentionPeriod    = TimeSpan.FromDays(7),
            StaleUnpublishedThreshold = null
        };
        var service = new OutboxPublisherService(publisher, typeof(DummyEntity), options, NullLoggerFactory.Instance, publisher.Meter);

        await service.StartAsync();
        // Wait until published cleanup ran — if stale cleanup were wired up, it would have fired by now.
        await Task.WhenAny(publishedCleaned.Task, Task.Delay(5000));
        await service.StopAsync();

        outbox.Verify(o => o.CleanupStaleUnpublishedAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task PollLoop_ContinuesAfterCleanupError()
    {
        var loopConfirmed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        var outbox = new Mock<IOutbox>();
        outbox.Setup(o => o.GetUnpublishedAsync<DummyEntity>(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<int, CancellationToken>((_, _) =>
            {
                if (Interlocked.Increment(ref callCount) >= 3)
                    loopConfirmed.TrySetResult(true);
            })
            .ReturnsAsync(Array.Empty<EntityChange<DummyEntity>>());
        outbox.Setup(o => o.CleanupPublishedAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db error"));

        var publisher = BuildPublisher(outbox.Object);
        var options = new OutboxPublisherOptions
        {
            PollingInterval        = TimeSpan.FromMilliseconds(20),
            CleanupInterval        = TimeSpan.Zero,
            CleanupRetentionPeriod = TimeSpan.FromDays(7)
        };
        var service = new OutboxPublisherService(publisher, typeof(DummyEntity), options, NullLoggerFactory.Instance, publisher.Meter);

        await service.StartAsync();
        var triggered = await Task.WhenAny(loopConfirmed.Task, Task.Delay(5000));
        Assert.That(triggered, Is.EqualTo(loopConfirmed.Task), "Poll loop stopped after cleanup error");

        // StopAsync completes cleanly — the poll loop was not killed by the cleanup error.
        Assert.DoesNotThrowAsync(() => service.StopAsync());
    }

    private static ChangePublisher BuildPublisher(IOutbox outbox)
    {
        var queuePublisher = new Mock<IQueuePublisher>();
        queuePublisher.Setup(p => p.PublishAsync(It.IsAny<MessageEnvelope>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var serializer = new Mock<IChangeSerializer>();

        var cp = new ChangePublisher(NullLoggerFactory.Instance, new RayTreeMeter());
        cp.RegisterOutbox(typeof(DummyEntity), outbox);
        cp.RegisterPublisher(typeof(DummyEntity), queuePublisher.Object);
        cp.RegisterSerializer(typeof(DummyEntity), serializer.Object);
        cp.RegisterCompressor(typeof(DummyEntity), new NoOpCompressorPlugin());
        return cp;
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

        var publisher = new ChangePublisher(NullLoggerFactory.Instance, new RayTreeMeter());
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

public class EntityChangeTrackerRunCleanupTests
{
    private class Entity1 { public int Id { get; set; } }
    private class Entity2 { public int Id { get; set; } }

    private static EntityChangeTracker BuildTracker(params (Type entityType, IOutbox outbox)[] registrations)
    {
        var publisher = new ChangePublisher(NullLoggerFactory.Instance, new RayTreeMeter());
        foreach (var (entityType, outbox) in registrations)
            publisher.RegisterOutbox(entityType, outbox);
        return new EntityChangeTracker(publisher);
    }

    [Test]
    public async Task RunCleanupAsync_CallsCleanupOnAllOutboxes_AndAccumulatesCount()
    {
        var outbox1 = new Mock<IOutbox>();
        outbox1.Setup(o => o.CleanupPublishedAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(5);

        var outbox2 = new Mock<IOutbox>();
        outbox2.Setup(o => o.CleanupPublishedAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);

        var tracker = BuildTracker((typeof(Entity1), outbox1.Object), (typeof(Entity2), outbox2.Object));

        var deleted = await tracker.RunCleanupAsync(TimeSpan.FromDays(7));

        Assert.That(deleted, Is.EqualTo(8));
        outbox1.Verify(o => o.CleanupPublishedAsync(TimeSpan.FromDays(7), It.IsAny<CancellationToken>()), Times.Once);
        outbox2.Verify(o => o.CleanupPublishedAsync(TimeSpan.FromDays(7), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task RunCleanupAsync_ReturnsZero_WhenNoOutboxesRegistered()
    {
        var tracker = BuildTracker();

        var deleted = await tracker.RunCleanupAsync(TimeSpan.FromDays(7));

        Assert.That(deleted, Is.EqualTo(0));
    }

    [Test]
    public async Task RunCleanupAsync_PassesRetentionPeriodToEachOutbox()
    {
        var retention = TimeSpan.FromDays(14);
        var outbox = new Mock<IOutbox>();
        outbox.Setup(o => o.CleanupPublishedAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var tracker = BuildTracker((typeof(Entity1), outbox.Object));

        await tracker.RunCleanupAsync(retention);

        outbox.Verify(o => o.CleanupPublishedAsync(retention, It.IsAny<CancellationToken>()), Times.Once);
    }
}
