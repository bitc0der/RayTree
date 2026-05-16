using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using RayTree.Core.Plugins.Compression;
using RayTree.Core.Telemetry;
using RayTree.Core.Tracking;
using RayTree.Plugins.InMemory;
using RayTree.Plugins.Serializers.Json;

namespace RayTree.Core.Tests.Telemetry;

/// <summary>
/// The tracker's lifecycle owns the meter only when the builder created it implicitly. A
/// caller-supplied meter (via <c>UseMeter</c>) must survive <c>tracker.Dispose()</c> so the
/// caller can keep using it (e.g. share across trackers or attach exporters after the fact).
/// </summary>
[TestFixture]
public class UseMeterOwnershipTests
{
    private class Order { public int Id { get; set; } }

    private static IChangeTrackingBuilder Configure(IChangeTrackingBuilder b) => b
        .ForEntity<Order>(e => e
            .UseOutbox(new InMemoryOutbox())
            .UsePublisher(new InMemoryQueue())
            .UseSerializer(new JsonSerializerPlugin())
            .UseCompressor(new NoOpCompressorPlugin()));

    /// <summary>
    /// Returns <c>true</c> if the underlying <see cref="Meter"/> is still alive. There is no
    /// public <c>IsDisposed</c> on <see cref="Meter"/>; the only reliable signal is whether a
    /// freshly-attached <see cref="MeterListener"/> still receives instrument-publish events
    /// when a new instrument is created. After <c>Meter.Dispose()</c>, the BCL silently drops
    /// publish events to new listeners (and no longer throws on <c>CreateCounter</c>), so
    /// observing a publish from a fresh listener proves the meter is alive.
    /// </summary>
    private static bool MeterStillAlive(RayTreeMeter meter)
    {
        var inner = meter.InternalMeter;
        var saw = false;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (ReferenceEquals(instrument.Meter, inner)) saw = true;
            }
        };
        listener.Start();

        // Touch the meter — a live meter publishes the new instrument to the fresh listener.
        _ = inner.CreateCounter<long>("ownership.probe");
        return saw;
    }

    [Test]
    public void CallerSuppliedMeter_NotDisposedByTracker()
    {
        var meter = new RayTreeMeter();
        var tracker = Configure(new ChangeTrackingBuilder(NullLoggerFactory.Instance).UseMeter(meter))
            .Build();

        tracker.Dispose();

        Assert.That(MeterStillAlive(meter), Is.True,
            "tracker must NOT dispose a meter supplied via UseMeter — the caller owns it");

        meter.Dispose();   // caller cleans up
    }

    [Test]
    public void BuilderCreatedMeter_IsDisposedByTracker()
    {
        var tracker = Configure(new ChangeTrackingBuilder(NullLoggerFactory.Instance)).Build();
        var meter = tracker.Meter;   // tracker owns this — exposed via property

        tracker.Dispose();

        Assert.That(MeterStillAlive(meter), Is.False,
            "tracker must dispose its own implicitly-created meter");
    }
}
