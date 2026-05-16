namespace RayTree.OpenTelemetry;

/// <summary>
/// Public constants for RayTree's OpenTelemetry instrumentation. Referenced from
/// <see cref="MeterProviderBuilderExtensions.AddRayTreeMetrics"/> and available to
/// callers that build custom OTel views, filters, or exporter configurations.
/// </summary>
public static class RayTreeInstrumentation
{
    /// <summary>The meter name used by all RayTree instruments. Pass to
    /// <c>MeterProviderBuilder.AddMeter</c> to subscribe.</summary>
    public const string MeterName = "RayTree";
}
