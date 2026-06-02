using System.Diagnostics.Metrics;
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

    /// <summary>
    /// The underlying <see cref="Meter"/>. Exposed to test assemblies via
    /// <c>InternalsVisibleTo</c> so they can scope a <see cref="MeterListener"/> to one
    /// tracker without reaching for reflection.
    /// </summary>
    internal Meter InternalMeter => _meter;

    private readonly object _gaugeGate = new();
    private Func<IEnumerable<(Type entityType, IOutbox outbox)>>? _pendingGaugeSource;

    // Pending-count gauge cache. The OTel collection callback is synchronous and may fire as
    // often as the configured collection interval (1 s for some Prometheus setups), so each
    // call sampling N outboxes hits the DB N times. The cache memoises the last reading and
    // refreshes only when stale to bound DB load to one query per outbox per refresh window.
    private readonly TimeSpan _pendingCacheTtl;
    private readonly Dictionary<Type, (long Count, DateTime SampledAt)> _pendingCache = new();

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

    /// <summary>Default pending-count gauge cache TTL (10 seconds). Roughly aligns with typical
    /// OTel collection cadence; tunable per-instance via the constructor.</summary>
    public static readonly TimeSpan DefaultPendingCacheTtl = TimeSpan.FromSeconds(10);

    public RayTreeMeter() : this(DefaultPendingCacheTtl) { }

    /// <summary>
    /// Constructs the meter with a custom pending-count gauge cache TTL. Pass
    /// <see cref="TimeSpan.Zero"/> to disable caching (every observation polls the outbox).
    /// </summary>
    public RayTreeMeter(TimeSpan pendingCacheTtl)
    {
        _pendingCacheTtl = pendingCacheTtl;
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
    internal void RegisterPendingGauge(Func<IEnumerable<(Type entityType, IOutbox outbox)>> source)
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

        var now = DateTime.UtcNow;
        foreach (var (entityType, outbox) in source())
        {
            long? count = null;

            // Cache lookup: return the previous value if it's still within the TTL window.
            lock (_gaugeGate)
            {
                if (_pendingCache.TryGetValue(entityType, out var cached)
                    && _pendingCacheTtl > TimeSpan.Zero
                    && now - cached.SampledAt < _pendingCacheTtl)
                {
                    count = cached.Count;
                }
            }

            if (count is null)
            {
                // _gaugeGate is intentionally released before the DB call. Holding a lock
                // across a synchronous GetAwaiter().GetResult() call risks priority inversion
                // with threads that also acquire _gaugeGate (RegisterPendingGauge). The
                // trade-off: two concurrent OTel collection callbacks could both miss the cache
                // and both query the DB for the same entity type. The result is two redundant
                // (but correct) DB reads, with last-write-wins on the cache entry. This is
                // acceptable; the cache is a performance hint, not a correctness constraint.
                try
                {
                    // The OTel callback is synchronous; bounded by a partial index in the PG
                    // case. Cached so we hit the DB at most once per TTL window per outbox.
                    count = outbox.GetPendingCountAsync(entityType).GetAwaiter().GetResult();
                }
                catch
                {
                    // Never let a failed sample break the entire gauge — skip this entity type.
                    // Don't update the cache so the next observation retries.
                    continue;
                }

                lock (_gaugeGate)
                    _pendingCache[entityType] = (count.Value, now);
            }

            yield return new Measurement<long>(count.Value, EntityTag(entityType));
        }
    }

    // -------------------------------------------------------------------------
    // Internal emission facade — for RayTree.Core and first-party plugin
    // assemblies that publish to the outbox but live outside RayTree.Core
    // (e.g. NotificationBasedPublisher in RayTree.Plugins.PostgreSQL, which sees
    // Core internals via InternalsVisibleTo). Metric emission is a Core-internal
    // concern; observation is the public contract (RayTree.OpenTelemetry).
    // -------------------------------------------------------------------------

    /// <summary>
    /// Records metrics for a successfully completed outbox publish attempt.
    /// </summary>
    /// <param name="entityType">The entity type being published.</param>
    /// <param name="changeType">The type of change (Insert / Update / Delete).</param>
    /// <param name="durationSeconds">Elapsed wall-clock time of the publish attempt.</param>
    /// <param name="lagSeconds">Time since the outbox record was written, in seconds.
    /// Clamped to zero if negative (clock skew).</param>
    /// <param name="attempts">Total attempts made — 1 for a first-try success.</param>
    internal void RecordPublishSuccess(
        Type entityType, ChangeType changeType,
        double durationSeconds, double lagSeconds,
        int attempts = 1)
    {
        var entityTag = EntityTag(entityType);
        var changeTag = ChangeTag(changeType);
        OutboxPublishDuration.Record(durationSeconds, entityTag, changeTag);
        OutboxPublished.Add(1, entityTag, changeTag);
        OutboxPublishAttempts.Record(attempts, entityTag);
        OutboxLagDuration.Record(Math.Max(0, lagSeconds), entityTag);
    }

    /// <summary>
    /// Records the duration of a failed publish attempt. The caller is responsible for
    /// reverting the outbox claim so the record can be retried.
    /// </summary>
    internal void RecordPublishFailure(Type entityType, ChangeType changeType, double durationSeconds)
    {
        OutboxPublishDuration.Record(durationSeconds, EntityTag(entityType), ChangeTag(changeType));
    }

    /// <summary>
    /// Records the compressed payload byte size for one <c>MessageEnvelope</c>.
    /// Call after compression, before handing the envelope to the queue publisher.
    /// </summary>
    internal void RecordPayloadSize(Type entityType, ChangeType changeType, int bytes)
    {
        OutboxPayloadSize.Record(bytes, EntityTag(entityType), ChangeTag(changeType));
    }

    /// <summary>
    /// Records the number of unpublished records retrieved in one outbox poll batch.
    /// </summary>
    internal void RecordBatchSize(Type entityType, int count)
    {
        OutboxBatchSize.Record(count, EntityTag(entityType));
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
