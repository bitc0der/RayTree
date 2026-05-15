## Context

RayTree uses `ILoggerFactory` as its observability injection point, defaulting to `NullLoggerFactory.Instance` in builders so existing call sites need no changes. Metrics should follow the same ergonomic pattern. .NET 8 ships `System.Diagnostics.Metrics` in the BCL, so no new runtime dependency is required in `RayTree.Core`. The `IMeterFactory` interface (also BCL in .NET 8 via `Microsoft.Extensions.Diagnostics`) lets the DI container manage meter lifetime and labelling — mirroring how `ILoggerFactory` works for loggers. The existing runtime service classes (`OutboxPublisherService`, `ChangeSubscriber`) receive their dependencies through constructors; metrics instruments belong there.

## Goals / Non-Goals

**Goals:**
- Emit publisher-side instruments: published count, failure count, batch size, publish duration, and cleanup record counts — all tagged with `entity_type`.
- Emit subscriber-side instruments: processed count, deduplicated count, handler failures, handler retries, processing duration — tagged with `entity_type` and `change_type` where meaningful.
- Zero new runtime dependency in `RayTree.Core` — use BCL `System.Diagnostics.Metrics` only.
- No breaking API change — meter factory is an optional nullable parameter defaulting to null (normalized to `NullMeterFactory`).
- Opt-in OTel wiring via a new `AddRayTreeMetrics` extension in `RayTree.Hosting`.

**Non-Goals:**
- Distributed tracing (`ActivitySource` / spans) — separate concern, separate change.
- Per-message-ID cardinality attributes — would produce unbounded label sets.
- Metrics in plugin packages (PostgreSQL, Kafka, RabbitMQ) — deferred; broker-level metrics belong in those packages independently.
- Automatic dashboard or alerting definitions.

## Decisions

### D1: Use `System.Diagnostics.Metrics` directly, not an abstraction

**Choice:** Declare instruments on a single `RayTreeMeter` class that wraps a `Meter` instance. No `IRayTreeMetrics` interface.

**Rationale:** An interface adds an indirection layer with no current caller that needs to substitute it. Tests can observe instruments via `MeterListener` in-process. The BCL `Meter` is already the abstraction — its no-op path (when no listener is attached) has near-zero cost. YAGNI.

**Alternative considered:** `IRayTreeMetrics` with `NullRayTreeMetrics` and `DefaultRayTreeMetrics`. Rejected: doubles the type count, forces interface maintenance whenever a new instrument is added, and provides no benefit that `MeterListener` doesn't already cover in tests.

### D2: Mirror the `ILoggerFactory` injection pattern for `IMeterFactory`

**Choice:** `ChangeTrackingBuilder` and `ChangePublisherBuilder` accept `IMeterFactory? meterFactory = null`, normalize null to `NullMeterFactory.Instance` (from `Microsoft.Extensions.Diagnostics`), and pass the factory down to `ChangePublisher` and then to `OutboxPublisherService`. `ChangeSubscriberBuilder` accepts the same. `AddChangeTracking` in `RayTree.Hosting` resolves `IMeterFactory` from DI just as it resolves `ILoggerFactory`.

**Rationale:** Consistent with the existing logging pattern; existing call sites that omit `meterFactory` continue to compile and produce no-op metrics. The DI path gets real meters automatically when `AddOpenTelemetry().WithMetrics(b => b.AddMeter("RayTree"))` is wired up.

**Alternative considered:** Accept `Meter` directly instead of `IMeterFactory`. Rejected: callers would have to manage meter lifetime and naming themselves; `IMeterFactory` handles scoping and allows the OTel SDK to intercept creation.

### D3: Meter name `"RayTree"`, instrument names follow `raytree.*` convention

**Choice:** Single meter named `"RayTree"` (version taken from assembly). Instrument names: `raytree.outbox.messages.published`, `raytree.outbox.messages.failed`, `raytree.outbox.batch.size`, `raytree.outbox.publish.duration`, `raytree.outbox.records.cleaned`, `raytree.outbox.stale_unpublished.removed`, `raytree.subscriber.messages.processed`, `raytree.subscriber.messages.deduplicated`, `raytree.subscriber.messages.skipped`, `raytree.subscriber.handler.failures`, `raytree.subscriber.handler.retries`, `raytree.subscriber.processing.duration`.

**Rationale:** Dot-separated hierarchical names match OTel semantic conventions. A single meter name makes it easy for users to opt in: `AddMeter("RayTree")`.

### D4: `RayTree.Hosting` gets `AddRayTreeMetrics` extension, no new package

**Choice:** Add a static `AddRayTreeMetrics(this IMetricsBuilder builder)` extension in `src/RayTree.Hosting` that calls `builder.AddMeter("RayTree")`. This is a thin pass-through — no OTel SDK dependency in `RayTree.Hosting` itself. The user's application project already references OTel.

**Rationale:** Keeps `RayTree.Hosting` dependency-light. The extension just exposes the meter name as a constant so callers don't hard-code it. If the user doesn't use OTel they can skip the call entirely — metrics are emitted regardless and silently no-op.

**Alternative considered:** New `RayTree.OpenTelemetry` package. Rejected: one trivial method doesn't warrant a new package; the hosting package already exists and is the natural home for wiring.

## Risks / Trade-offs

- **Constructor signature growth** — `OutboxPublisherService` and `ChangeSubscriber` gain a `RayTreeMeter` parameter. Any third-party code constructing these directly will break. Mitigation: these classes are public but their constructors are builder-mediated in all documented usage; the change is additive (not removing existing parameters).
- **`NullMeterFactory` availability** — `NullMeterFactory.Instance` requires `Microsoft.Extensions.Diagnostics` which is not explicitly listed in `Directory.Packages.props`. Mitigation: it is already pulled in transitively via `Microsoft.Extensions.Hosting`; add an explicit version pin to make the dependency visible and stable.
- **Instrument cardinality** — `entity_type` and `change_type` tags are low-cardinality by design (bounded by the number of registered entity types and the three `ChangeType` values). No per-message attributes are added.
- **Duration measurement accuracy** — `OutboxPublisherService.PublishWithRetryAsync` measures total wall time including retries. This is intentional: it reflects the operator-visible cost of publishing one change. A separate retry count instrument provides the breakdown.

## Migration Plan

1. Add `Microsoft.Extensions.Diagnostics` pin to `Directory.Packages.props` (explicit, already transitively present).
2. Add `RayTreeMeter` to `RayTree.Core`.
3. Update `ChangePublisher`, `ChangeTrackingBuilder`, `ChangePublisherBuilder`, `ChangeSubscriberBuilder` constructors — nullable parameter, backward-compatible.
4. Instrument `OutboxPublisherService` and `ChangeSubscriber`.
5. Add `AddRayTreeMetrics` to `RayTree.Hosting`.
6. Add unit tests using `MeterListener`.

No schema migrations, no config changes, no rollback needed — metrics emission is purely additive and silently inactive when no listener is registered.

## Open Questions

*(none — all decisions made above)*
