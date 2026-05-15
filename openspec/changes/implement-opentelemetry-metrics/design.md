## Context

RayTree uses `ILoggerFactory` as its observability injection point, with builders defaulting to `NullLoggerFactory.Instance` so existing call sites need no changes. Metrics should follow the same ergonomic shape but without coupling the core library to `Microsoft.Extensions.Diagnostics`. The .NET 8 BCL ships `System.Diagnostics.Metrics.Meter`, and the recommended pattern for libraries is to construct a named `Meter` directly — the OTel SDK picks it up via `AddMeter("<name>")`. No factory abstraction is needed.

The outbox pattern's defining health signals are **queue depth** (pending records) and **end-to-end lag** (time from outbox write to publish, and from publish to handler completion). These were missing from the first revision of this design and are added here.

## Goals / Non-Goals

**Goals:**
- Emit publisher-side instruments: published, failed, batch size, per-attempt publish duration, attempts-to-success, end-to-end outbox lag, payload bytes, cleanup record counts — all tagged with `entity_type`.
- Emit subscriber-side instruments: processed, deduplicated, skipped (with `reason` tag), handler failures, handler attempts-to-success, local processing duration, end-to-end handler lag — tagged with `entity_type` and `change_type` where meaningful.
- Emit outbox write counter tagged with `entity_type` and `change_type` at the point of `EntityChangeTracker.TrackXxxAsync`.
- Emit an observable gauge `raytree.outbox.pending` per entity type, sampled on each OTel collection tick.
- Zero new runtime dependency in `RayTree.Core` and `RayTree.Hosting` — only BCL `System.Diagnostics.Metrics`.
- All durations in seconds (OTel semantic convention).
- Opt-in OTel wiring via a new peer assembly `RayTree.OpenTelemetry` exposing `AddRayTreeMetrics(this MeterProviderBuilder)` and a public `RayTreeInstrumentation.MeterName = "RayTree"` constant.

**Non-Goals:**
- Distributed tracing (`ActivitySource` / spans) — separate change.
- Per-correlation-id cardinality — unbounded label set.
- Plugin-internal metrics (PostgreSQL connection pool, Kafka broker, RabbitMQ channel) — those belong inside each plugin package; this change covers only core/publisher/subscriber concerns.

## Decisions

### D1: Construct `Meter` directly — no `IMeterFactory`

**Choice:** `RayTreeMeter` creates its meter with `new Meter("RayTree", typeof(RayTreeMeter).Assembly.GetName().Version?.ToString())`. Builders construct a default `RayTreeMeter` if none is provided.

**Rationale:** `IMeterFactory` lives in `Microsoft.Extensions.Diagnostics.Abstractions` and its `Null` implementation is internal — there is no public `NullMeterFactory.Instance` to mirror `NullLoggerFactory.Instance`. Direct `Meter` construction is the standard library pattern: it has no allocations or measurement cost when no `MeterListener` is attached, and the OTel SDK subscribes by meter name. Avoids pulling `Microsoft.Extensions.Diagnostics` into `RayTree.Core` purely for metrics.

**Alternative considered:** Inject `IMeterFactory`. Rejected — adds a runtime dependency for no observable benefit; the factory's main use case (DI-scoped meter lifetime) doesn't apply to a singleton outbox library.

### D2: Observable gauge for queue depth, sampled by OTel collection

**Choice:** `raytree.outbox.pending` is registered as an `ObservableGauge<long>` with a callback that calls `IOutbox.GetPendingCountAsync(...).GetAwaiter().GetResult()` for each registered entity type. A new method `IOutbox.GetPendingCountAsync(CancellationToken)` is added to the outbox abstraction. PostgreSQL implementation is a cheap `SELECT count(*) WHERE published = FALSE` using the existing partial index. InMemory implementation iterates its in-memory dictionary.

**Rationale:** Queue depth is the most important health signal for the outbox pattern but is expensive to track on every write/publish. Sampling on OTel's collection cadence (typically 10–60 s) is the right granularity. Adding `GetPendingCountAsync` to the interface is preferable to reading the existing `GetUnpublishedAsync` (which materialises rows). The `.GetAwaiter().GetResult()` is acceptable inside `ObservableInstrument` callbacks — OTel calls these synchronously and infrequently; we accept the brief block over building an async observable shim.

**Alternative considered:** Track depth via deltas (increment on write, decrement on publish). Rejected — fragile under crashes, claim-revert races, and external writes (e.g., manual SQL inserts).

### D3: Two duration measurements per publish — single-attempt and attempts-to-success

**Choice:** `raytree.outbox.publish.duration` records the time of each single publish attempt (excluding inter-retry sleeps). `raytree.outbox.publish.attempts` is a histogram recording the number of attempts taken to succeed (1 on first-try success, 2 if retried once, etc.). Failed publishes do not record an attempts value (they increment `raytree.outbox.messages.failed` instead).

**Rationale:** A combined "wall time including retries" histogram conflates a slow broker with rapid retries — operators can't tell which is happening. Two instruments give an unambiguous picture. The same pattern applies to subscriber handlers: `raytree.subscriber.processing.duration` measures a single attempt, `raytree.subscriber.handler.attempts` is the retry-shape histogram.

### D4: Lag metrics use `change.Timestamp` and `envelope.Timestamp` as t₀

**Choice:** `raytree.outbox.lag.duration` = `DateTime.UtcNow - change.Timestamp` at publish time. `raytree.subscriber.lag.duration` = `DateTime.UtcNow - envelope.Timestamp` at handler-completion time. Both in seconds.

**Rationale:** These are the timestamps already on the wire — no extra clocks to wire up. They expose the full pipeline delay an external consumer sees. `change.Timestamp` is set on outbox write, `envelope.Timestamp` is copied from it at publish time, so subscriber lag includes both outbox dwell and broker transit.

**Caveat:** Cross-clock drift between writer host and publisher host will distort the lag value. Acceptable for monitoring SLO trends, not for sub-second precision.

### D5: Payload size measured on compressed bytes at publish boundary

**Choice:** `raytree.outbox.payload.size` records `envelope.Payload.Length` bytes after compression, tagged with `entity_type` and `change_type`.

**Rationale:** Compressed size is what reaches the broker and matters for network/queue pressure. Pre-compression size is recoverable from compression ratio measurements only if needed (out of scope here).

### D6: Histograms have explicit boundaries via `View` documentation, not registration

**Choice:** Do not register custom `View`s in core. Document recommended bucket boundaries in the hosting extension's XML doc (e.g., "for `*.duration` metrics, configure `MetricStreamConfiguration` with buckets `[0.001, 0.005, 0.01, 0.05, 0.1, 0.5, 1, 5, 10]`").

**Rationale:** Histogram bucket choice is deployment-specific (high-throughput latency vs. long-tail batch processing). Hardcoding views in a library forces wrong defaults. OTel's default exponential histogram bucketing works adequately out of the box.

### D7: OTel integration lives in a peer assembly, not in `RayTree.Hosting`

**Choice:** Create a new `src/RayTree.OpenTelemetry` project that depends on `OpenTelemetry.Api` and contains `AddRayTreeMetrics(this MeterProviderBuilder)` plus the `"RayTree"` meter-name constant (`RayTreeInstrumentation.MeterName`). `RayTree.Core` and `RayTree.Hosting` reference only the BCL `System.Diagnostics.Metrics`. Applications that want OTel pull in `RayTree.OpenTelemetry`; applications that don't, never see an OTel transitive reference.

**Rationale:** Matches the existing architectural pattern — `RayTree.Hosting`, `RayTree.EntityFrameworkCore`, and the `RayTree.Plugins.*` family each isolate their third-party dependency in a peer assembly. Bolting OTel onto `RayTree.Hosting` would force every host-integrated consumer to pull in OTel, violating that separation. The peer assembly also gives a natural home for future OTel additions (semantic-convention attribute constants, recommended `View` boundaries, `ActivitySource`-based tracing wiring) without bloating `RayTree.Hosting`.

**Alternative considered:** Single extension in `RayTree.Hosting`. Rejected — couples host integration to OTel, breaks the established peer-assembly pattern, and forces consumers to take an OTel dependency even when they only want `AddChangeTracking`.

**Alternative considered:** Name it `RayTree.Plugins.OpenTelemetry`. Rejected — the `Plugins.*` namespace is reserved for swap-in `IXxx` implementations (one outbox, one serializer, one broker per app). OTel integration is additive instrumentation, not a substitutable plugin.

### D8: Builder integration — additive nullable parameter, default constructed in `Build()`

**Choice:** `ChangeTrackingBuilder`, `ChangePublisherBuilder`, `ChangeSubscriberBuilder` get a `UseMeter(RayTreeMeter)` configuration method (optional). If not called, `Build()` constructs a fresh `RayTreeMeter` and passes it to the runtime services. The meter is owned by the tracker and disposed in `EntityChangeTracker.Dispose`.

**Rationale:** No constructor signature break. The `UseMeter` opt-in lets tests inject a meter scoped to the test (so `MeterListener` doesn't catch cross-test measurements). Default construction keeps the zero-config path simple.

## Risks / Trade-offs

- **Observable-gauge synchronous DB call.** Sampling pending-count from PostgreSQL on every OTel collection tick adds a `SELECT count(*) WHERE published = FALSE` to the workload. The partial index makes this O(unpublished rows), which is bounded by `BatchSize`-ish under healthy operation. Mitigation: document that operators should configure their OTel collection interval ≥ 10 s; if the gauge becomes hot, switch to a cached value refreshed by the publisher loop.
- **Clock-drift in lag metrics.** Cross-host UTC skew biases lag. Mitigation: documented as a caveat; lag is for trend monitoring, not absolute SLOs.
- **`MeterListener` test isolation.** Tests using `MeterListener` will see measurements from any meter sharing the same name. Mitigation: `UseMeter` in tests allows passing a meter with a unique per-test name; tests filter their listener on that meter instance.
- **Cardinality from `change_type` tag.** `ChangeType` has three values (Insert/Update/Delete); combined with entity types, total series count is bounded by `3 × #entity_types`. Safe.

## Migration Plan

1. Add `IOutbox.GetPendingCountAsync` to the abstraction and to `InMemoryOutbox` + `PostgreSqlOutbox`.
2. Add `RayTreeMeter` in `RayTree.Core/Telemetry/`.
3. Wire `UseMeter` through builders; construct default meter in `Build()`.
4. Instrument `EntityChangeTracker.TrackXxxAsync`, `OutboxPublisherService`, `ChangeSubscriber`.
5. Create `src/RayTree.OpenTelemetry` project; add `AddRayTreeMetrics` extension and `RayTreeInstrumentation` constants class.
6. Create `tests/RayTree.OpenTelemetry.Tests`; add `MeterListener`-based unit tests.

No data migrations, no broker-format changes, no rollback steps — metrics are purely additive and silently inactive when no listener attaches.

## Usage Examples

These illustrate the intended developer experience across the three common entry points. They are non-normative — the spec carries the contract; this section shows what calling the API should feel like.

### Example 1: DI (ASP.NET Core / Generic Host)

```csharp
using OpenTelemetry;
using OpenTelemetry.Metrics;
using RayTree.Hosting;
using RayTree.OpenTelemetry;

var builder = WebApplication.CreateBuilder(args);

// Subscribe OTel to RayTree's meter — one line.
builder.Services
    .AddOpenTelemetry()
    .WithMetrics(m => m
        .AddRayTreeMetrics()                       // only OTel-specific call
        .AddPrometheusExporter());

// RayTree change tracking — no metrics-specific configuration.
// AddChangeTracking registers RayTreeMeter as a singleton; EntityChangeTracker
// resolves it via constructor injection.
builder.Services.AddChangeTracking(builder.Configuration, ct =>
{
    ct.ForEntity<Order>(e => e
        .UsePostgreSqlOutbox(o => o.ConnectionString = "…")
        .UseKafkaPublisher(o => o.BootstrapServers = "…")
        .UseJsonSerializer());
});

var app = builder.Build();
app.MapPrometheusScrapingEndpoint();
app.Run();
```

`RayTree.OpenTelemetry` and `RayTree.Hosting` do not reference each other; they meet through the shared meter name `"RayTree"`.

### Example 2: Non-DI (console app, worker, library embed)

```csharp
using OpenTelemetry;
using OpenTelemetry.Metrics;
using RayTree.Core.Tracking;
using RayTree.OpenTelemetry;

// Build the OTel pipeline directly — no IServiceCollection involved.
using var meterProvider = Sdk.CreateMeterProviderBuilder()
    .AddRayTreeMetrics()
    .AddOtlpExporter(o => o.Endpoint = new Uri("http://localhost:4317"))
    .Build();

// Build the tracker. Build() creates a default RayTreeMeter internally.
var tracker = await new ChangeTrackingBuilder()
    .ForEntity<Order>(e => e
        .UsePostgreSqlOutbox(o => o.ConnectionString = "…")
        .UseKafkaPublisher(o => o.BootstrapServers = "…")
        .UseJsonSerializer())
    .BuildAsync();

await tracker.TrackInsertAsync(order);
```

The OTel SDK subscribes to any `Meter` named `"RayTree"`, regardless of which assembly creates it. RayTree creates exactly one such meter inside `Build()`.

### Example 3: Explicit meter injection (parallel trackers, test isolation)

```csharp
using RayTree.Core.Telemetry;
using RayTree.Core.Tracking;

// Useful when you need a meter scoped to this tracker — e.g. tests that
// filter MeterListener by instance, or two trackers in one process that
// you want to observe separately.
var customMeter = new RayTreeMeter();

var tracker = new ChangeTrackingBuilder()
    .UseMeter(customMeter)
    .ForEntity<Order>(e => e.UseInMemoryOutbox().UseInMemoryQueue())
    .Build();
```

### Example 4: Unit test with `MeterListener`

```csharp
[Test]
public async Task TrackInsertAsync_IncrementsWritesCounter()
{
    // Per-test meter so the listener can filter to this instance only.
    var meter = new RayTreeMeter();
    using var collector = new TestMetricsCollector(meter);

    var tracker = new ChangeTrackingBuilder()
        .UseMeter(meter)
        .ForEntity<Order>(e => e.UseInMemoryOutbox().UseInMemoryQueue())
        .Build();

    await tracker.TrackInsertAsync(new Order { Id = 1 });

    var ms = collector.GetMeasurements<long>("raytree.outbox.writes");
    Assert.That(ms, Has.One.Matches<Measurement<long>>(m =>
        m.Value == 1 &&
        m.Tags.GetValueOrDefault("entity_type")?.Equals("Order") == true &&
        m.Tags.GetValueOrDefault("change_type")?.Equals("Insert") == true));
}
```

`TestMetricsCollector` (introduced in tasks group 10) is a thin `MeterListener` wrapper that filters on `instrument.Meter == meter` so parallel tests don't see each other's measurements.

### Zero-config path

If no listener is attached and `AddRayTreeMetrics` is never called, the runtime services still call their instruments. The BCL `Meter` short-circuits to a no-op — measurements are not recorded and no per-call allocation occurs.

## Open Questions

*(none — all decisions made)*
