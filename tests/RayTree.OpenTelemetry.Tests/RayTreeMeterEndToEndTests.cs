using System.Diagnostics.Metrics;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using RayTree.Core.Telemetry;
using RayTree.OpenTelemetry;

namespace RayTree.OpenTelemetry.Tests;

/// <summary>
/// Proves that actual RayTree instruments — not synthetic counters with the same meter name —
/// flow end-to-end through the OTel SDK pipeline when an app calls
/// <see cref="MeterProviderBuilderExtensions.AddRayTreeMetrics"/>. Also asserts the produced
/// instrument names are compatible with the Prometheus naming convention (only chars that
/// the Prometheus exporter normalises to underscores, no chars that produce ambiguous output).
/// </summary>
[TestFixture]
public class RayTreeMeterEndToEndTests
{
    private sealed class CapturingExporter : BaseExporter<Metric>
    {
        public HashSet<string> MetricNames { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string?> Units { get; } = new(StringComparer.Ordinal);

        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (var metric in batch)
            {
                MetricNames.Add(metric.Name);
                Units[metric.Name] = metric.Unit;
            }
            return ExportResult.Success;
        }
    }

    [Test]
    public void AddRayTreeMetrics_RealCounterEmission_FlowsThroughExporter()
    {
        // Arrange: full OTel pipeline subscribing to RayTree via the public extension.
        var exporter = new CapturingExporter();
        using var rayTreeMeter = new RayTreeMeter();
        using var provider = Sdk.CreateMeterProviderBuilder()
            .AddRayTreeMetrics()
            .AddReader(new BaseExportingMetricReader(exporter))
            .Build();

        // Act: drive a real RayTree instrument (not a synthetic test counter).
        rayTreeMeter.OutboxWrites.Add(
            1,
            new KeyValuePair<string, object?>("entity_type", "Order"),
            new KeyValuePair<string, object?>("change_type", "Insert"));

        provider!.ForceFlush();

        // Assert: the production instrument name surfaced through the OTel pipeline.
        Assert.That(exporter.MetricNames, Has.Some.EqualTo("raytree.outbox.writes"));
    }

    [Test]
    public void AllRayTreeInstrumentNames_AreCompatibleWithPrometheusNormalization()
    {
        // The Prometheus exporter normalises `.` to `_` and rejects any other chars outside
        // [a-zA-Z0-9_:]. If any instrument name contains such chars two RayTree metrics could
        // collapse onto the same Prometheus name. Lock this down at the instrument level so
        // doc claims about "Prometheus-ready" stay honest.
        using var meter = new RayTreeMeter();
        var names = EnumerateInstrumentNames(meter);

        // Sanity: the published RayTreeMeter surface is 18 instruments:
        //   5 publisher counters + 5 publisher histograms + 1 observable gauge
        //   + 4 subscriber counters + 3 subscriber histograms = 18 total.
        // Lock the count down so a future rename or accidental deletion of an instrument
        // (in particular the observable gauge, which doesn't surface to call sites) is caught
        // here rather than silently changing Prometheus exposition.
        Assert.That(names, Has.Count.GreaterThanOrEqualTo(18),
            $"Expected at least 18 RayTree instruments, found {names.Count}: {string.Join(", ", names)}");
        Assert.That(names, Has.Member("raytree.outbox.pending"),
            "the observable gauge must be present in the published instrument set");

        foreach (var name in names)
        {
            Assert.That(name, Does.Match(@"^[a-zA-Z_:][a-zA-Z0-9_.:]*$"),
                $"Instrument '{name}' contains characters that break Prometheus naming.");

            // Normalised form must be unique among the set.
            var normalised = name.Replace('.', '_');
            var collisions = names.Count(n => n.Replace('.', '_') == normalised);
            Assert.That(collisions, Is.EqualTo(1),
                $"Instrument '{name}' collides with another under Prometheus normalisation.");
        }
    }

    [Test]
    public void AddRayTreeMetrics_ExportedMetrics_PreserveUnitMetadata()
    {
        // Prometheus appends unit suffixes (e.g. `_seconds`, `_bytes`) based on the OTel unit
        // string. Verify that when a duration instrument is exercised through the SDK pipeline
        // the unit "s" survives to the exporter.
        var exporter = new CapturingExporter();
        using var rayTreeMeter = new RayTreeMeter();
        using var provider = Sdk.CreateMeterProviderBuilder()
            .AddRayTreeMetrics()
            .AddReader(new BaseExportingMetricReader(exporter))
            .Build();

        rayTreeMeter.OutboxPublishDuration.Record(0.123,
            new KeyValuePair<string, object?>("entity_type", "Order"),
            new KeyValuePair<string, object?>("change_type", "Insert"));
        rayTreeMeter.OutboxPayloadSize.Record(512,
            new KeyValuePair<string, object?>("entity_type", "Order"),
            new KeyValuePair<string, object?>("change_type", "Insert"));

        provider!.ForceFlush();

        Assert.That(exporter.Units, Does.ContainKey("raytree.outbox.publish.duration"));
        Assert.That(exporter.Units["raytree.outbox.publish.duration"], Is.EqualTo("s"));
        Assert.That(exporter.Units["raytree.outbox.payload.size"], Is.EqualTo("By"));
    }

    /// <summary>
    /// Enumerates the names of every instrument created on the underlying meter by attaching
    /// a transient listener. The set is materialised before the listener is disposed so the
    /// test does not depend on cross-listener ordering guarantees.
    /// </summary>
    private static IReadOnlyList<string> EnumerateInstrumentNames(RayTreeMeter rayTreeMeter)
    {
        var inner = rayTreeMeter.InternalMeter;
        var names = new List<string>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (ReferenceEquals(instrument.Meter, inner))
                    names.Add(instrument.Name);
            }
        };
        listener.Start();
        return names;
    }
}
