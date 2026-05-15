using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Reflection;
using RayTree.Core.Plugins.Outbox;
using RayTree.Core.Tracking;

namespace RayTree.Core.Telemetry;

/// <summary>
/// Owns the <see cref="System.Diagnostics.Metrics.Meter"/> named <c>RayTree</c> and exposes
/// every instrument emitted by the publisher and subscriber pipelines. Construct one per
/// tracker — the default builder path does this automatically; tests may pass a custom
/// instance via <c>UseMeter</c> to scope a <see cref="MeterListener"/> to a single tracker.
/// </summary>
public sealed class RayTreeMeter : IDisposable
{
    /// <summary>The meter name. OTel consumers subscribe via <c>AddMeter("RayTree")</c>.</summary>
    public const string MeterName = "RayTree";

    private readonly Meter _meter;
    private readonly object _gaugeGate = new();
    private Func<IEnumerable<(Type entityType, IOutbox outbox)>>? _pendingGaugeSource;

    // -------------------------------------------------------------------------
    // Publisher counters
    // -------------------------------------------------------------------------
    internal Counter<long> OutboxWrites { get; }
    internal Counter<long> OutboxPublished { get; }
    internal Counter<long> OutboxFailed { get; }
    internal Counter<long> OutboxRecordsCleaned { get; }
    internal Counter<long> OutboxStaleUnpublishedRemoved { get; }

    // -------------------------------------------------------------------------
    // Publisher histograms
    // -------------------------------------------------------------------------
    internal Histogram<int>    OutboxBatchSize { get; }
    internal Histogram<double> OutboxPublishDuration { get; }
    internal Histogram<int>    OutboxPublishAttempts { get; }
    internal Histogram<double> OutboxLagDuration { get; }
    internal Histogram<int>    OutboxPayloadSize { get; }

    // -------------------------------------------------------------------------
    // Subscriber counters
    // -------------------------------------------------------------------------
    internal Counter<long> SubscriberProcessed { get; }
    internal Counter<long> SubscriberDeduplicated { get; }
    internal Counter<long> SubscriberSkipped { get; }
    internal Counter<long> SubscriberHandlerFailures { get; }

    // -------------------------------------------------------------------------
    // Subscriber histograms
    // -------------------------------------------------------------------------
    internal Histogram<int>    SubscriberHandlerAttempts { get; }
    internal Histogram<double> SubscriberProcessingDuration { get; }
    internal Histogram<double> SubscriberLagDuration { get; }

    public RayTreeMeter()
    {
        var version = typeof(RayTreeMeter).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        _meter = new Meter(MeterName, version);

        OutboxWrites                  = _meter.CreateCounter<long>("raytree.outbox.writes",                  unit: "{writes}");
        OutboxPublished               = _meter.CreateCounter<long>("raytree.outbox.messages.published",      unit: "{messages}");
        OutboxFailed                  = _meter.CreateCounter<long>("raytree.outbox.messages.failed",         unit: "{messages}");
        OutboxRecordsCleaned          = _meter.CreateCounter<long>("raytree.outbox.records.cleaned",         unit: "{records}");
        OutboxStaleUnpublishedRemoved = _meter.CreateCounter<long>("raytree.outbox.stale_unpublished.removed", unit: "{records}");

        OutboxBatchSize       = _meter.CreateHistogram<int>   ("raytree.outbox.batch.size",       unit: "{messages}");
        OutboxPublishDuration = _meter.CreateHistogram<double>("raytree.outbox.publish.duration", unit: "s");
        OutboxPublishAttempts = _meter.CreateHistogram<int>   ("raytree.outbox.publish.attempts", unit: "{attempts}");
        OutboxLagDuration     = _meter.CreateHistogram<double>("raytree.outbox.lag.duration",     unit: "s");
        OutboxPayloadSize     = _meter.CreateHistogram<int>   ("raytree.outbox.payload.size",     unit: "By");

        SubscriberProcessed       = _meter.CreateCounter<long>("raytree.subscriber.messages.processed",     unit: "{messages}");
        SubscriberDeduplicated    = _meter.CreateCounter<long>("raytree.subscriber.messages.deduplicated",  unit: "{messages}");
        SubscriberSkipped         = _meter.CreateCounter<long>("raytree.subscriber.messages.skipped",       unit: "{messages}");
        SubscriberHandlerFailures = _meter.CreateCounter<long>("raytree.subscriber.handler.failures",       unit: "{handlers}");

        SubscriberHandlerAttempts    = _meter.CreateHistogram<int>   ("raytree.subscriber.handler.attempts",    unit: "{attempts}");
        SubscriberProcessingDuration = _meter.CreateHistogram<double>("raytree.subscriber.processing.duration", unit: "s");
        SubscriberLagDuration        = _meter.CreateHistogram<double>("raytree.subscriber.lag.duration",        unit: "s");

        _meter.CreateObservableGauge(
            "raytree.outbox.pending",
            ObservePendingCounts,
            unit: "{messages}",
            description: "Unpublished outbox records per entity type.");
    }

    /// <summary>
    /// Registers a callback used by the <c>raytree.outbox.pending</c> observable gauge. The
    /// callback is invoked once per OTel collection tick; each tuple yields one measurement
    /// tagged with <c>entity_type</c>.
    /// </summary>
    public void RegisterPendingGauge(Func<IEnumerable<(Type entityType, IOutbox outbox)>> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_gaugeGate)
            _pendingGaugeSource = source;
    }

    private IEnumerable<Measurement<long>> ObservePendingCounts()
    {
        Func<IEnumerable<(Type entityType, IOutbox outbox)>>? source;
        lock (_gaugeGate) source = _pendingGaugeSource;

        if (source == null) yield break;

        foreach (var (entityType, outbox) in source())
        {
            long count;
            try
            {
                // The OTel callback is synchronous; the count query is bounded by a partial index
                // and runs at collection cadence (10s+), so blocking briefly here is acceptable.
                count = outbox.GetPendingCountAsync(entityType).GetAwaiter().GetResult();
            }
            catch
            {
                // Never let a failed sample break the entire gauge — skip this entity type.
                continue;
            }

            yield return new Measurement<long>(count, EntityTag(entityType));
        }
    }

    // -------------------------------------------------------------------------
    // Tag helpers — keep instrumentation call sites terse and consistent.
    // -------------------------------------------------------------------------

    internal static KeyValuePair<string, object?> EntityTag(Type entityType)
        => new("entity_type", entityType.Name);

    internal static KeyValuePair<string, object?> EntityTag(string entityTypeName)
        => new("entity_type", SimpleTypeName(entityTypeName));

    internal static KeyValuePair<string, object?> ChangeTag(ChangeType changeType)
        => new("change_type", changeType.ToString());

    internal static KeyValuePair<string, object?> ReasonTag(string reason)
        => new("reason", reason);

    /// <summary>
    /// Resolves the simple class name from a full <c>EntityChange.EntityType</c> string
    /// (which holds <c>Type.FullName</c>) so gauge labels stay low-cardinality and aligned
    /// with the <c>Type.Name</c>-based tagging used elsewhere.
    /// </summary>
    private static string SimpleTypeName(string fullName)
    {
        var lastDot = fullName.LastIndexOf('.');
        return lastDot >= 0 ? fullName[(lastDot + 1)..] : fullName;
    }

    public void Dispose() => _meter.Dispose();
}
