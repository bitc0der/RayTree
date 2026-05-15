## Why

RayTree has no observable metrics surface, making it impossible for operators to monitor outbox queue depth, publish throughput, subscriber processing latency, or handler failure rates without parsing log output. Adding OpenTelemetry metrics via the standard `System.Diagnostics.Metrics` API gives operators first-class visibility through any OTel-compatible backend (Prometheus, Datadog, OTLP) with zero additional dependencies in the core library.

## What Changes

- **New `RayTreeMeter` class in `RayTree.Core`** — owns the named `Meter("RayTree")` and declares all instruments (counters, histograms). Injected via constructor into runtime services.
- **`OutboxPublisherService` records** — published message count, publish failure count, batch size histogram, publish duration histogram, and outbox cleanup counts per entity type.
- **`ChangeSubscriber` records** — processed message count, deduplicated message count, handler failure and retry counts, and message processing duration histogram per entity type and change type.
- **`ChangePublisher` and `ChangeTrackingBuilder`** accept `IMeterFactory?` (nullable, defaults to `NullMeterFactory`) alongside the existing `ILoggerFactory?` pattern — no breaking change to existing call sites.
- **`RayTree.Hosting` extension** — `AddRayTreeMetrics(builder)` registers the `"RayTree"` meter with the OTel `MeterProvider`, making it available to any `UseOpenTelemetry()` setup without requiring callers to know the meter name.

## Capabilities

### New Capabilities

- `opentelemetry-metrics`: Publisher-side and subscriber-side metrics instruments (counters + histograms) exported via the standard `System.Diagnostics.Metrics` `Meter` API, with an opt-in OTel wiring extension in `RayTree.Hosting`.

### Modified Capabilities

*(none — no existing spec-level behavior changes)*

## Impact

- **`src/RayTree.Core`** — new `Telemetry/RayTreeMeter.cs`; constructor changes to `OutboxPublisherService`, `ChangeSubscriber`, `ChangePublisher`, `ChangeTrackingBuilder` (all additive, nullable parameter).
- **`src/RayTree.Hosting`** — new `AddRayTreeMetrics` extension method.
- **`Directory.Packages.props`** — add `OpenTelemetry.Extensions.Hosting` version for the hosting extension; `System.Diagnostics.DiagnosticSource` is already transitively present, no new core dependency.
- **Tests** — unit tests for metric instrument creation; no integration test changes required since metrics are observable via `MeterListener` in-process.
