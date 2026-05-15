using OpenTelemetry.Metrics;

namespace RayTree.OpenTelemetry;

/// <summary>
/// Extension methods for wiring RayTree metrics into an OpenTelemetry pipeline.
/// </summary>
public static class MeterProviderBuilderExtensions
{
    /// <summary>
    /// Subscribes the OTel <c>MeterProvider</c> to the <c>RayTree</c> meter. After this call,
    /// all RayTree instruments (publisher counters, subscriber counters, duration histograms,
    /// the <c>raytree.outbox.pending</c> observable gauge, etc.) flow through the configured
    /// exporter.
    /// </summary>
    /// <remarks>
    /// <para>Emitted instruments include:</para>
    /// <list type="bullet">
    /// <item><description><c>raytree.outbox.writes</c> — counter, tracker write rate.</description></item>
    /// <item><description><c>raytree.outbox.pending</c> — observable gauge, unpublished record count.</description></item>
    /// <item><description><c>raytree.outbox.messages.published</c> / <c>.failed</c> — counters.</description></item>
    /// <item><description><c>raytree.outbox.batch.size</c> — histogram, records per poll.</description></item>
    /// <item><description><c>raytree.outbox.publish.duration</c> — histogram, seconds (unit <c>s</c>).</description></item>
    /// <item><description><c>raytree.outbox.publish.attempts</c> — histogram, attempts-to-success.</description></item>
    /// <item><description><c>raytree.outbox.lag.duration</c> — histogram, end-to-end outbox lag in seconds.</description></item>
    /// <item><description><c>raytree.outbox.payload.size</c> — histogram, compressed bytes (unit <c>By</c>).</description></item>
    /// <item><description><c>raytree.outbox.records.cleaned</c> / <c>.stale_unpublished.removed</c> — counters.</description></item>
    /// <item><description><c>raytree.subscriber.messages.processed</c> / <c>.deduplicated</c> / <c>.skipped</c> — counters.</description></item>
    /// <item><description><c>raytree.subscriber.handler.failures</c> — counter.</description></item>
    /// <item><description><c>raytree.subscriber.handler.attempts</c> — histogram, attempts-to-success.</description></item>
    /// <item><description><c>raytree.subscriber.processing.duration</c> — histogram, seconds.</description></item>
    /// <item><description><c>raytree.subscriber.lag.duration</c> — histogram, write-to-handler-done lag in seconds.</description></item>
    /// </list>
    /// <para>
    /// Recommended bucket boundaries for the <c>*.duration</c> histograms (configure via
    /// <c>AddView(...)</c> on your <c>MeterProvider</c>):
    /// <c>[0.001, 0.005, 0.01, 0.05, 0.1, 0.5, 1, 5, 10]</c> seconds — covers sub-millisecond
    /// in-process work through tens-of-seconds outbox lag.
    /// </para>
    /// <para>
    /// All tags are low-cardinality: <c>entity_type</c> (bounded by the number of registered
    /// entity types), <c>change_type</c> (3 values: Insert/Update/Delete), and <c>reason</c>
    /// (only on <c>messages.skipped</c>, 2 values: <c>unknown_type</c>, <c>no_handler</c>).
    /// </para>
    /// </remarks>
    public static MeterProviderBuilder AddRayTreeMetrics(this MeterProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddMeter(RayTreeInstrumentation.MeterName);
    }
}
