using System.Diagnostics.Metrics;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using RayTree.OpenTelemetry;

namespace RayTree.OpenTelemetry.Tests;

[TestFixture]
public class MeterProviderBuilderExtensionsTests
{
    [Test]
    public void AddRayTreeMetrics_ReturnsBuilderForChaining()
    {
        var builder = Sdk.CreateMeterProviderBuilder();
        var result  = builder.AddRayTreeMetrics();

        Assert.That(result, Is.SameAs(builder));
    }

    [Test]
    public void AddRayTreeMetrics_SubscribesToRayTreeMeter()
    {
        // Arrange: build a MeterProvider that subscribes to RayTree and feeds a capturing
        // exporter so we can verify a measurement made it through the SDK pipeline.
        var exporter = new CapturingExporter();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddRayTreeMetrics()
            .AddReader(new BaseExportingMetricReader(exporter))
            .Build();

        using var meter = new Meter(RayTreeInstrumentation.MeterName);
        var counter = meter.CreateCounter<long>("raytree.test.smoke");
        counter.Add(1);

        // Act
        meterProvider.ForceFlush();

        // Assert
        Assert.That(exporter.MetricNames, Has.Some.EqualTo("raytree.test.smoke"));
    }

    [Test]
    public void AddRayTreeMetrics_NullBuilder_Throws()
    {
        MeterProviderBuilder? nullBuilder = null;
        Assert.Throws<ArgumentNullException>(() => nullBuilder!.AddRayTreeMetrics());
    }

    private sealed class CapturingExporter : BaseExporter<Metric>
    {
        public List<string> MetricNames { get; } = new();

        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (var metric in batch)
                MetricNames.Add(metric.Name);
            return ExportResult.Success;
        }
    }
}
