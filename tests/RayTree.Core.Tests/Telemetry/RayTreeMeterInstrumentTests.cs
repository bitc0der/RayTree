using System.Diagnostics.Metrics;
using RayTree.Core.Telemetry;

namespace RayTree.Core.Tests.Telemetry;

[TestFixture]
public class RayTreeMeterInstrumentTests
{
    // Collects every instrument created by the meter under test so we can assert
    // names and units in one place.
    private sealed class InstrumentCollector : IDisposable
    {
        private readonly MeterListener _listener;
        public List<Instrument> Instruments { get; } = new();

        public InstrumentCollector(Meter target)
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, _) =>
                {
                    if (ReferenceEquals(instrument.Meter, target))
                        Instruments.Add(instrument);
                }
            };
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }

    [Test]
    public void AllDurationInstruments_HaveUnitSeconds()
    {
        using var meter = new RayTreeMeter();
        using var collector = new InstrumentCollector(meter.GetInternalMeter());

        var durations = collector.Instruments.Where(i => i.Name.EndsWith(".duration")).ToList();

        Assert.That(durations, Is.Not.Empty);
        foreach (var d in durations)
            Assert.That(d.Unit, Is.EqualTo("s"), $"{d.Name} should be in seconds");
    }

    [Test]
    public void PayloadSizeInstrument_HasUnitBytes()
    {
        using var meter = new RayTreeMeter();
        using var collector = new InstrumentCollector(meter.GetInternalMeter());

        var payload = collector.Instruments.SingleOrDefault(i => i.Name == "raytree.outbox.payload.size");

        Assert.That(payload, Is.Not.Null);
        Assert.That(payload!.Unit, Is.EqualTo("By"));
    }

    [Test]
    public void Meter_IsNamedRayTree()
    {
        using var meter = new RayTreeMeter();
        Assert.That(meter.GetInternalMeter().Name, Is.EqualTo("RayTree"));
        Assert.That(RayTreeMeter.MeterName, Is.EqualTo("RayTree"));
    }
}
