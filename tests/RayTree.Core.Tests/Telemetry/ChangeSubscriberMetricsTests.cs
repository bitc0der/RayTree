using Microsoft.Extensions.Logging.Abstractions;
using RayTree.Core.Handling;
using RayTree.Core.Models;
using RayTree.Core.Telemetry;
using RayTree.Core.Tracking;

namespace RayTree.Core.Tests.Telemetry;

[TestFixture]
public class ChangeSubscriberMetricsTests
{
    private class Sample { public int Id { get; set; } }

    private static MessageEnvelope EnvelopeFor<T>(ChangeType change, Guid? correlationId = null, DateTime? timestamp = null)
        => new()
        {
            EntityType    = typeof(T).FullName!,
            EntityId      = "1",
            ChangeType    = change,
            CorrelationId = correlationId ?? Guid.NewGuid(),
            Timestamp     = timestamp ?? DateTime.UtcNow,
            Version       = 1,
            Payload       = Array.Empty<byte>()
        };

    [Test]
    public async Task ProcessMessageAsync_Successful_IncrementsProcessedCounter()
    {
        using var meter = new RayTreeMeter();
        using var collector = new TestMetricsCollector(meter);

        var subscriber = new ChangeSubscriber(NullLogger<ChangeSubscriber>.Instance, meter);
        subscriber.OnChange<Sample>(null, (_, _) => Task.CompletedTask);

        await subscriber.ProcessMessageAsync(EnvelopeFor<Sample>(ChangeType.Insert));

        Assert.That(collector.Sum("raytree.subscriber.messages.processed"), Is.EqualTo(1));
        var processed = collector.Get("raytree.subscriber.messages.processed")[0];
        Assert.That(processed.Tags["entity_type"], Is.EqualTo("Sample"));
        Assert.That(processed.Tags["change_type"], Is.EqualTo("Insert"));
    }

    [Test]
    public async Task ProcessMessageAsync_DuplicateCorrelationId_IncrementsDeduplicatedCounter()
    {
        using var meter = new RayTreeMeter();
        using var collector = new TestMetricsCollector(meter);

        var subscriber = new ChangeSubscriber(NullLogger<ChangeSubscriber>.Instance, meter);
        subscriber.OnChange<Sample>(null, (_, _) => Task.CompletedTask);

        var correlationId = Guid.NewGuid();
        await subscriber.ProcessMessageAsync(EnvelopeFor<Sample>(ChangeType.Insert, correlationId));
        await subscriber.ProcessMessageAsync(EnvelopeFor<Sample>(ChangeType.Insert, correlationId));

        Assert.That(collector.Sum("raytree.subscriber.messages.deduplicated"), Is.EqualTo(1));
    }

    [Test]
    public async Task ProcessMessageAsync_HandlerThrowsThenSucceeds_RecordsAttemptCount()
    {
        using var meter = new RayTreeMeter();
        using var collector = new TestMetricsCollector(meter);

        var attempts = 0;
        var options = new SubscriberOptions { MaxRetries = 3, RetryDelay = TimeSpan.FromMilliseconds(1), SkipOnFailure = false };
        var subscriber = new ChangeSubscriber(NullLogger<ChangeSubscriber>.Instance, meter, options: options);
        subscriber.OnChange<Sample>(null, (_, _) =>
        {
            attempts++;
            if (attempts < 3) throw new InvalidOperationException("transient");
            return Task.CompletedTask;
        });

        await subscriber.ProcessMessageAsync(EnvelopeFor<Sample>(ChangeType.Insert));

        var attemptHistogram = collector.Get("raytree.subscriber.handler.attempts");
        Assert.That(attemptHistogram, Has.Count.EqualTo(1));
        Assert.That(attemptHistogram[0].Value, Is.EqualTo(3));
    }

    [Test]
    public async Task ProcessMessageAsync_RecordsLagDurationApproximatelyEqualToAge()
    {
        using var meter = new RayTreeMeter();
        using var collector = new TestMetricsCollector(meter);

        var subscriber = new ChangeSubscriber(NullLogger<ChangeSubscriber>.Instance, meter);
        subscriber.OnChange<Sample>(null, (_, _) => Task.CompletedTask);

        var t0 = DateTime.UtcNow.AddSeconds(-2.0);
        await subscriber.ProcessMessageAsync(EnvelopeFor<Sample>(ChangeType.Insert, timestamp: t0));

        var lag = collector.Get("raytree.subscriber.lag.duration")[0];
        Assert.That(lag.Value, Is.InRange(1.5, 5.0));
        Assert.That(lag.Unit, Is.EqualTo("s"));
    }

    [Test]
    public async Task ProcessMessageAsync_UnknownType_IncrementsSkippedWithReason()
    {
        using var meter = new RayTreeMeter();
        using var collector = new TestMetricsCollector(meter);

        var subscriber = new ChangeSubscriber(NullLogger<ChangeSubscriber>.Instance, meter);

        var envelope = new MessageEnvelope
        {
            EntityType    = "NoSuch.Namespace.NoSuchType, NoSuchAssembly",
            EntityId      = "1",
            ChangeType    = ChangeType.Insert,
            CorrelationId = Guid.NewGuid(),
            Timestamp     = DateTime.UtcNow,
            Payload       = Array.Empty<byte>()
        };

        await subscriber.ProcessMessageAsync(envelope);

        var skipped = collector.Get("raytree.subscriber.messages.skipped");
        Assert.That(skipped, Has.Count.EqualTo(1));
        Assert.That(skipped[0].Tags["reason"], Is.EqualTo("unknown_type"));
    }

    [Test]
    public async Task ProcessMessageAsync_NoHandler_IncrementsSkippedWithNoHandlerReason()
    {
        using var meter = new RayTreeMeter();
        using var collector = new TestMetricsCollector(meter);

        // ForEntity registers the type but no handlers.
        var subscriber = new ChangeSubscriber(NullLogger<ChangeSubscriber>.Instance, meter)
            .ForEntity<Sample>();

        await subscriber.ProcessMessageAsync(EnvelopeFor<Sample>(ChangeType.Insert));

        var skipped = collector.Get("raytree.subscriber.messages.skipped");
        Assert.That(skipped, Has.Count.EqualTo(1));
        Assert.That(skipped[0].Tags["reason"], Is.EqualTo("no_handler"));
    }
}
