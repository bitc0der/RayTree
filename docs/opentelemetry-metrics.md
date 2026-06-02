# OpenTelemetry Metrics

RayTree emits a complete set of `System.Diagnostics.Metrics` instruments on a `Meter` named `"RayTree"`. There is **no OpenTelemetry SDK dependency in `RayTree.Core` or `RayTree.Hosting`** — metrics are silently inactive unless a `MeterListener` or OTel `MeterProvider` is attached.

## Wire-up

### Generic Host (recommended)

Reference the `RayTree.OpenTelemetry` package and call `AddRayTreeMetrics` on the OTel meter-provider builder:

```csharp
using RayTree.OpenTelemetry;

builder.Services
    .AddOpenTelemetry()
    .WithMetrics(b => b
        .AddRayTreeMetrics()        // subscribes to the "RayTree" meter
        .AddPrometheusExporter());  // or AddOtlpExporter, AddConsoleExporter, ...
```

`AddRayTreeMetrics` is a thin pass-through — it does **not** configure exporters, views, or histogram bucket boundaries, so the host application keeps full control.

### Custom meter name reference

For OTel views, filters, or alternative wiring use the constant directly instead of hard-coding the string:

```csharp
b.AddMeter(RayTree.OpenTelemetry.RayTreeInstrumentation.MeterName); // == "RayTree"
```

### Standalone (no OTel SDK)

If the consumer only wants raw `System.Diagnostics.Metrics`, attach a `MeterListener` to the meter named `"RayTree"`. No additional package needed.

## Architecture

`RayTreeMeter` lives in `RayTree.Core/Telemetry`. It owns a single `System.Diagnostics.Metrics.Meter("RayTree", <assembly-version>)` and all instruments. `EntityChangeTracker` owns the meter lifecycle when built via `ChangeTrackingBuilder` — the meter is disposed when the tracker is disposed. `AddChangeTracking` registers the meter as a singleton in DI.

`RayTree.OpenTelemetry` is a peer assembly (parallel to `RayTree.Hosting` and `RayTree.EntityFrameworkCore`). Applications that do not reference it receive **zero** transitive OTel dependencies.

## Instruments

All instruments are tagged with `entity_type`. Counters and histograms tied to a specific change additionally carry `change_type`.

### Publisher

| Instrument | Kind | Unit | Tags | Source |
|---|---|---|---|---|
| `raytree.outbox.writes` | counter | `{writes}` | `entity_type`, `change_type` | `EntityChangeTracker.TrackXxxAsync` after successful `IOutbox.WriteAsync` |
| `raytree.outbox.pending` | observable gauge | `{messages}` | `entity_type` | `IOutbox.GetPendingCountAsync`, sampled per collection tick, cached for 10 s |
| `raytree.outbox.messages.published` | counter | `{messages}` | `entity_type`, `change_type` | `OutboxPublisherService` on success |
| `raytree.outbox.messages.failed` | counter | `{messages}` | `entity_type`, `change_type` | `OutboxPublisherService` after retries exhausted |
| `raytree.outbox.batch.size` | histogram | `{messages}` | `entity_type` | Size of every batch returned by `GetUnpublishedAsync` |
| `raytree.outbox.publish.duration` | histogram | `s` | `entity_type`, `change_type` | Each publish attempt (success or fail) |
| `raytree.outbox.publish.attempts` | histogram | `{attempts}` | `entity_type` | Total attempts on every completed publish (success **or** failure) |
| `raytree.outbox.lag.duration` | histogram | `s` | `entity_type` | `(now - change.Timestamp)` for each successfully published change |
| `raytree.outbox.payload.size` | histogram | `By` | `entity_type`, `change_type` | `MessageEnvelope.Payload.Length` |
| `raytree.outbox.records.cleaned` | counter | `{records}` | `entity_type` | Published rows removed by rotation |
| `raytree.outbox.stale_unpublished.removed` | counter | `{records}` | `entity_type` | Stale unpublished rows removed by rotation |

### Subscriber

| Instrument | Kind | Unit | Tags | Source |
|---|---|---|---|---|
| `raytree.subscriber.messages.processed` | counter | `{messages}` | `entity_type`, `change_type` | All handlers ran without error |
| `raytree.subscriber.messages.deduplicated` | counter | `{messages}` | `entity_type`, `change_type` | `CorrelationId` already processed |
| `raytree.subscriber.messages.skipped` | counter | `{messages}` | `entity_type`, `change_type` (when known), `reason` | `reason="unknown_type"` or `reason="no_handler"` |
| `raytree.subscriber.handler.failures` | counter | `{handlers}` | `entity_type`, `change_type` | Handler exhausted all retries (regardless of `SkipOnFailure`) |
| `raytree.subscriber.handler.attempts` | histogram | `{attempts}` | `entity_type` | Total attempts on every completed dispatch (success **or** failure) |
| `raytree.subscriber.processing.duration` | histogram | `s` | `entity_type`, `change_type` | Each handler attempt (success or fail) |
| `raytree.subscriber.lag.duration` | histogram | `s` | `entity_type`, `change_type` | `(now - envelope.Timestamp)` for each successfully dispatched message |

### Connection recovery

Connection recovery is **not** exposed as metrics. The reconnect loops (Postgres LISTEN, Kafka publisher/consumer rebuild) and the outbox connection-fault log demotion all remain, but disconnect/recovery visibility is **log-only**: Postgres/Kafka emit retry, recovery, and exhaustion logs at `Information`/`Error`; the RabbitMQ publisher logs `Warning` on shutdown and `Information` on recovery (the RabbitMQ consumer has no logger and is silent for recovery). Each recovery-owning plugin tunes its loop via its own options type — `PostgresConnectionRecoveryOptions` (PostgreSQL) and `KafkaConnectionRecoveryOptions` (Kafka). RabbitMQ recovery is owned by `RabbitMQ.Client`'s `AutomaticRecoveryEnabled` (library default) and has no RayTree options.

## Conventions

- **All durations are seconds** (`s`), per OTel semantic conventions. Use OTel views to convert to milliseconds at export time if your backend prefers.
- **Bytes** use `By`. Counts use either `{messages}`, `{records}`, `{writes}`, `{handlers}`, or `{attempts}` per OTel guidance.
- The `entity_type` tag carries the **CLR short name** (e.g. `"Order"`), not the full type-qualified name.
- The `change_type` tag is one of `"Insert"`, `"Update"`, `"Delete"`.

## Pending-gauge sampling

`raytree.outbox.pending` is an observable gauge. Each collection tick the registered callback iterates all registered entity types and calls `IOutbox.GetPendingCountAsync` per type. Results are cached for **10 seconds** (the default) to bound the DB round-trips when OTel is configured with a sub-second collection interval. The cache TTL is configurable via the `RayTreeMeter(TimeSpan pendingCacheTtl)` constructor overload; pass `TimeSpan.Zero` to disable caching entirely.

## Suggested OTel views (bucket boundaries)

Defaults from OTel's histogram aggregation are tuned for HTTP latencies. For RayTree the following buckets work better:

```csharp
.AddView("raytree.outbox.publish.duration",     new ExplicitBucketHistogramConfiguration { Boundaries = new[] { 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10 } })
.AddView("raytree.subscriber.processing.duration", new ExplicitBucketHistogramConfiguration { Boundaries = new[] { 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10 } })
.AddView("raytree.outbox.lag.duration",         new ExplicitBucketHistogramConfiguration { Boundaries = new[] { 0.01, 0.05, 0.1, 0.5, 1, 5, 10, 30, 60, 300 } })
.AddView("raytree.subscriber.lag.duration",     new ExplicitBucketHistogramConfiguration { Boundaries = new[] { 0.01, 0.05, 0.1, 0.5, 1, 5, 10, 30, 60, 300 } })
.AddView("raytree.outbox.batch.size",           new ExplicitBucketHistogramConfiguration { Boundaries = new[] { 1d, 5, 10, 25, 50, 100, 250, 500, 1000 } })
.AddView("raytree.outbox.publish.attempts",     new ExplicitBucketHistogramConfiguration { Boundaries = new[] { 1d, 2, 3, 5, 10 } })
.AddView("raytree.subscriber.handler.attempts", new ExplicitBucketHistogramConfiguration { Boundaries = new[] { 1d, 2, 3, 5, 10 } })
.AddView("raytree.outbox.payload.size",         new ExplicitBucketHistogramConfiguration { Boundaries = new[] { 256d, 1024, 4096, 16384, 65536, 262144, 1048576 } })
```

## Sample dashboards

Typical queries against the published metric names:

- **Publisher throughput**: `rate(raytree_outbox_messages_published_total[1m])` by `entity_type`
- **Publisher tail latency**: `histogram_quantile(0.99, sum by (le) (rate(raytree_outbox_publish_duration_bucket[5m])))`
- **End-to-end lag p95**: `histogram_quantile(0.95, sum by (le, entity_type) (rate(raytree_subscriber_lag_duration_bucket[5m])))`
- **Failure ratio**: `rate(raytree_outbox_messages_failed_total[5m]) / rate(raytree_outbox_writes_total[5m])`
- **Outbox backlog alert**: `raytree_outbox_pending > 10000 for 5m`
- **Retry shape**: `histogram_quantile(0.99, sum by (le) (rate(raytree_subscriber_handler_attempts_bucket[5m])))` — values > 1 indicate handlers are retrying

Connection disconnect/recovery is no longer exposed as metrics — observe it through the plugin recovery logs (Postgres/Kafka retry + recovery, RabbitMQ publisher `Warning`/`Information`).

## Custom meter injection

To share a `RayTreeMeter` instance across multiple trackers, or supply a pre-configured one, use `UseMeter`:

```csharp
var meter = new RayTreeMeter();           // owned by the caller; caller disposes
var tracker = new ChangeTrackingBuilder(loggerFactory)
    .UseMeter(meter)
    .ForEntity<Order>(/* ... */)
    .Build();
// tracker.Dispose() will NOT dispose the meter because the caller supplied it
```

When `UseMeter` is **not** called, the builder creates a default meter and the resulting `EntityChangeTracker` owns and disposes it.
