## Why

RayTree has no observable metrics surface, making it impossible for operators to monitor outbox queue depth, publish lag, throughput, or handler failure rates without parsing log output. Adding OpenTelemetry-compatible metrics via the BCL `System.Diagnostics.Metrics` API gives operators first-class visibility through any OTel backend (Prometheus, Datadog, OTLP) with zero new runtime dependencies in the core library.

## What Changes

- **New `RayTreeMeter` class in `RayTree.Core`** — owns a single `Meter("RayTree", <assembly-version>)` and declares all instruments. Constructed directly (`new Meter(...)`) rather than via `IMeterFactory` — no DI factory dependency in the core library.
- **Outbox write tracking** — `EntityChangeTracker.TrackInsert/Update/DeleteAsync` increments a write counter so input rate is observable.
- **Outbox queue depth** — observable gauge sampled per-entity-type, polling `IOutbox` for pending count. The single most important health signal for the outbox pattern.
- **End-to-end outbox lag histogram** — measured at publish time as `now - change.Timestamp`, the SLO-relevant publisher delay metric.
- **End-to-end handler lag histogram** — measured at handler-completion time as `now - envelope.Timestamp`, the SLO-relevant subscriber delay metric.
- **Payload size histogram** — compressed-bytes-per-message, observed at publish time, for broker pressure and serializer regression detection.
- **`OutboxPublisherService` instrumentation** — published count, failure count, batch size histogram, per-attempt publish duration histogram, attempts-to-success histogram, lag histogram, payload size histogram, cleanup record counts.
- **`ChangeSubscriber` instrumentation** — processed, deduplicated, and skipped counters; handler failure counter; handler attempts-to-success histogram; local processing duration histogram; end-to-end lag histogram.
- **All `*.duration` histograms use `s` (seconds)** per OTel semantic conventions.
- **New `RayTree.OpenTelemetry` assembly** — peer of `RayTree.Hosting` / `RayTree.EntityFrameworkCore`. Contains `AddRayTreeMetrics(this MeterProviderBuilder)` and any future OTel-specific helpers (semantic-convention attribute constants, recommended view configurations, future `ActivitySource` wiring). Keeps the OTel SDK dependency isolated — `RayTree.Core` and `RayTree.Hosting` remain free of `OpenTelemetry.*` references.

## Capabilities

### New Capabilities

- `opentelemetry-metrics`: Publisher-side and subscriber-side metrics — counters, histograms, and an observable gauge — emitted via the BCL `System.Diagnostics.Metrics` `Meter` API, plus an opt-in OTel wiring extension in `RayTree.Hosting`.

### Modified Capabilities

*(none — no existing spec-level behavior changes)*

## Impact

- **`src/RayTree.Core`** — new `Telemetry/RayTreeMeter.cs`; additive constructor parameter (`RayTreeMeter`) on `OutboxPublisherService`, `ChangeSubscriber`, `ChangePublisher`, `EntityChangeTracker`; builders construct a default `RayTreeMeter` if none supplied. **No OTel SDK reference** — only BCL `System.Diagnostics.Metrics`.
- **`src/RayTree.Hosting`** — unchanged; no OTel reference. Continues to provide host integration only.
- **`src/RayTree.OpenTelemetry` (new project)** — single-purpose assembly hosting `AddRayTreeMetrics(this MeterProviderBuilder)` and the `"RayTree"` meter-name constant. Targets `net8.0`; references `OpenTelemetry.Api`.
- **`tests/RayTree.OpenTelemetry.Tests` (new project)** — unit tests for the extension and meter-name constant.
- **`Directory.Packages.props`** — add `OpenTelemetry.Api` version pin (referenced only by `RayTree.OpenTelemetry`).
- **`RayTree.sln`** — add the new src + test projects.
- **Tests** — unit tests use `MeterListener` in-process; no integration test changes required.
