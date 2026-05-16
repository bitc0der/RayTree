using RayTree.OpenTelemetry;

namespace RayTree.OpenTelemetry.Tests;

[TestFixture]
public class RayTreeInstrumentationTests
{
    [Test]
    public void MeterName_IsRayTree()
    {
        Assert.That(RayTreeInstrumentation.MeterName, Is.EqualTo("RayTree"));
    }
}
