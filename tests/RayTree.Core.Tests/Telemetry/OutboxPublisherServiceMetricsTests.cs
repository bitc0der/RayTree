using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RayTree.Core.Distribution;
using RayTree.Core.Models;
using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Compression;
using RayTree.Core.Plugins.Outbox;
using RayTree.Core.Plugins.Publisher;
using RayTree.Core.Plugins.Serialization;
using RayTree.Core.Telemetry;
using RayTree.Core.Tracking;
using RayTree.Plugins.InMemory;
using RayTree.Plugins.Serializers.Json;

namespace RayTree.Core.Tests.Telemetry;

[TestFixture]
public class OutboxPublisherServiceMetricsTests
{
    private class Sample { public int Id { get; set; } }

    private static (ChangePublisher publisher, RayTreeMeter meter, TestMetricsCollector collector, InMemoryOutbox outbox)
        Build(IQueuePublisher queuePublisher)
    {
        var meter = new RayTreeMeter();
        var collector = new TestMetricsCollector(meter);
        var outbox = new InMemoryOutbox();
        var publisher = new ChangePublisher(NullLoggerFactory.Instance, meter);
        publisher.RegisterOutbox(typeof(Sample), outbox);
        publisher.RegisterPublisher(typeof(Sample), queuePublisher);
        publisher.RegisterSerializer(typeof(Sample), new JsonSerializerPlugin());
        publisher.RegisterCompressor(typeof(Sample), new NoOpCompressorPlugin());
        return (publisher, meter, collector, outbox);
    }

    [Test]
    public async Task PublishWithRetry_OnSuccess_IncrementsPublished_RecordsLag_RecordsPayloadSize()
    {
        var (publisher, meter, collector, outbox) = Build(new InMemoryQueue());
        using var _meter = meter; using var _collector = collector; using var _publisher = publisher;

        var options = new OutboxPublisherOptions
        {
            BatchSize         = 10,
            PollingInterval   = TimeSpan.FromMilliseconds(50),
            MaxPublishConcurrency = 1
        };

        await outbox.WriteAsync(new EntityChange<Sample>
        {
            EntityType    = typeof(Sample).FullName!,
            EntityId      = "1",
            ChangeType    = ChangeType.Insert,
            CorrelationId = Guid.NewGuid(),
            Timestamp     = DateTime.UtcNow.AddSeconds(-1.0),
            State         = new Sample { Id = 1 }
        });

        using var svc = new OutboxPublisherService(publisher, typeof(Sample), options, NullLoggerFactory.Instance, meter);
        await svc.StartAsync();
        await Task.Delay(300);
        await svc.StopAsync();

        Assert.That(collector.Sum("raytree.outbox.messages.published"), Is.EqualTo(1));
        Assert.That(collector.Get("raytree.outbox.publish.attempts")[0].Value, Is.EqualTo(1));
        Assert.That(collector.Get("raytree.outbox.lag.duration")[0].Value, Is.InRange(0.5, 5.0));
        Assert.That(collector.Get("raytree.outbox.payload.size")[0].Value, Is.GreaterThan(0));
    }

    [Test]
    public async Task PublishWithRetry_AllAttemptsFail_IncrementsFailedCounter()
    {
        var queue = new Mock<IQueuePublisher>();
        queue.Setup(q => q.InitializeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        queue.Setup(q => q.PublishAsync(It.IsAny<MessageEnvelope>(), It.IsAny<CancellationToken>()))
             .ThrowsAsync(new InvalidOperationException("publish-broken"));

        var (publisher, meter, collector, outbox) = Build(queue.Object);
        using var _meter = meter; using var _collector = collector; using var _publisher = publisher;

        var options = new OutboxPublisherOptions
        {
            BatchSize        = 1,
            PollingInterval  = TimeSpan.FromMilliseconds(50),
            MaxRetryCount    = 2,
            RetryDelay       = TimeSpan.FromMilliseconds(1),
            MaxPublishConcurrency = 1
        };

        await outbox.WriteAsync(new EntityChange<Sample>
        {
            EntityType    = typeof(Sample).FullName!,
            EntityId      = "1",
            ChangeType    = ChangeType.Insert,
            CorrelationId = Guid.NewGuid(),
            State         = new Sample { Id = 1 }
        });

        using var svc = new OutboxPublisherService(publisher, typeof(Sample), options, NullLoggerFactory.Instance, meter);
        await svc.StartAsync();
        await Task.Delay(300);
        await svc.StopAsync();

        Assert.That(collector.Sum("raytree.outbox.messages.failed"), Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public async Task ProcessBatch_RecordsBatchSizeHistogram()
    {
        var (publisher, meter, collector, outbox) = Build(new InMemoryQueue());
        using var _meter = meter; using var _collector = collector; using var _publisher = publisher;

        for (var i = 0; i < 3; i++)
        {
            await outbox.WriteAsync(new EntityChange<Sample>
            {
                EntityType = typeof(Sample).FullName!,
                EntityId   = i.ToString(),
                ChangeType = ChangeType.Insert,
                CorrelationId = Guid.NewGuid(),
                State      = new Sample { Id = i }
            });
        }

        var options = new OutboxPublisherOptions
        {
            BatchSize       = 10,
            PollingInterval = TimeSpan.FromMilliseconds(50),
            MaxPublishConcurrency = 1
        };

        using var svc = new OutboxPublisherService(publisher, typeof(Sample), options, NullLoggerFactory.Instance, meter);
        await svc.StartAsync();
        await Task.Delay(250);
        await svc.StopAsync();

        var batchValues = collector.Get("raytree.outbox.batch.size").Select(m => (int)m.Value).ToList();
        Assert.That(batchValues, Has.Some.EqualTo(3));
    }

    [Test]
    public async Task PendingGauge_ReturnsCountForEachEntityType()
    {
        using var meter = new RayTreeMeter();
        using var collector = new TestMetricsCollector(meter);

        var outbox = new InMemoryOutbox();
        await outbox.WriteAsync(new EntityChange<Sample>
        {
            EntityType = typeof(Sample).FullName!,
            EntityId   = "1",
            ChangeType = ChangeType.Insert,
            State      = new Sample { Id = 1 }
        });
        await outbox.WriteAsync(new EntityChange<Sample>
        {
            EntityType = typeof(Sample).FullName!,
            EntityId   = "2",
            ChangeType = ChangeType.Insert,
            State      = new Sample { Id = 2 }
        });

        meter.RegisterPendingGauge(() => new[] { (typeof(Sample), (IOutbox)outbox) });

        collector.RecordObservableInstruments();

        var pending = collector.Get("raytree.outbox.pending");
        Assert.That(pending, Has.Some.Matches<TestMetricsCollector.RecordedMeasurement>(m =>
            (string?)m.Tags["entity_type"] == "Sample" && m.Value == 2));
    }
}
