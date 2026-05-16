## 1. Outbox Abstraction Extension

- [x] 1.1 Add `Task<long> GetPendingCountAsync(Type entityType, CancellationToken)` to `IOutbox`
- [x] 1.2 Implement `GetPendingCountAsync` in `InMemoryOutbox` (count entries with `Published == false` matching entity type)
- [x] 1.3 Implement `GetPendingCountAsync` in `PostgreSqlOutbox<TEntity>` as `SELECT count(*) FROM <table> WHERE published = FALSE` (uses existing `idx_*_outbox_unpublished` partial index)
- [x] 1.4 Add unit/integration tests for `GetPendingCountAsync` on both implementations

## 2. Core Metrics Class

- [x] 2.1 Create `src/RayTree.Core/Telemetry/RayTreeMeter.cs` — constructs `new Meter("RayTree", <assembly-version>)`; implements `IDisposable`; disposes the underlying `Meter`
- [x] 2.2 Declare publisher counters: `raytree.outbox.writes`, `raytree.outbox.messages.published`, `raytree.outbox.messages.failed`, `raytree.outbox.records.cleaned`, `raytree.outbox.stale_unpublished.removed`
- [x] 2.3 Declare publisher histograms (with units): `raytree.outbox.batch.size` (`{messages}`), `raytree.outbox.publish.duration` (`s`), `raytree.outbox.publish.attempts` (`{attempts}`), `raytree.outbox.lag.duration` (`s`), `raytree.outbox.payload.size` (`By`)
- [x] 2.4 Declare subscriber counters: `raytree.subscriber.messages.processed`, `raytree.subscriber.messages.deduplicated`, `raytree.subscriber.messages.skipped`, `raytree.subscriber.handler.failures`
- [x] 2.5 Declare subscriber histograms: `raytree.subscriber.handler.attempts` (`{attempts}`), `raytree.subscriber.processing.duration` (`s`), `raytree.subscriber.lag.duration` (`s`)
- [x] 2.6 Add method `RegisterPendingGauge(Func<IEnumerable<(Type entityType, IOutbox outbox)>>)` that registers an `ObservableGauge<long>` named `raytree.outbox.pending`; on each callback invocation iterate the registered entities and yield `Measurement<long>(count, new KeyValuePair<string,object?>("entity_type", entityType.Name))`
- [x] 2.7 Provide internal helpers for emitting measurements with the standard tag set (`entity_type`, `change_type`, optional `reason`) to keep call sites terse

## 3. Builder Wiring

- [x] 3.1 Add `UseMeter(RayTreeMeter meter)` to `IChangeTrackingBuilder` and `ChangeTrackingBuilder`
- [x] 3.2 Add `UseMeter(RayTreeMeter meter)` to `IChangePublisherBuilder` and `ChangePublisherBuilder`
- [x] 3.3 Add `UseMeter(RayTreeMeter meter)` to `IChangeSubscriberBuilder` and `ChangeSubscriberBuilder`
- [x] 3.4 In each builder's `Build()` / `BuildInternal()`, construct a default `RayTreeMeter` when none was supplied
- [x] 3.5 Pass the resolved `RayTreeMeter` to `ChangePublisher`, `OutboxPublisherService`, and `ChangeSubscriber` via constructor
- [x] 3.6 Make `EntityChangeTracker` own the `RayTreeMeter` lifecycle: dispose it in `EntityChangeTracker.Dispose`
- [x] 3.7 After publisher initialisation, call `meter.RegisterPendingGauge` with a callback that enumerates `ChangePublisher`'s registered entity types and their outboxes

## 4. Instrument `EntityChangeTracker` Writes

- [x] 4.1 In `TrackInsertAsync`, increment `raytree.outbox.writes` with `entity_type` and `change_type="Insert"` after `IOutbox.WriteAsync` returns successfully
- [x] 4.2 In `TrackUpdateAsync`, increment with `change_type="Update"`
- [x] 4.3 In `TrackDeleteAsync`, increment with `change_type="Delete"`

## 5. Instrument `OutboxPublisherService`

- [x] 5.1 In `ProcessBatchAsync`: record `raytree.outbox.batch.size` after retrieving the batch
- [x] 5.2 In `PublishWithRetryAsync`: time each call to `PublishChangeAsync` with a `Stopwatch` and record `raytree.outbox.publish.duration` in seconds per attempt (success or failure)
- [x] 5.3 In `PublishWithRetryAsync`: on success, record `raytree.outbox.publish.attempts` with the total attempt count; increment `raytree.outbox.messages.published`; record `raytree.outbox.lag.duration` as `(DateTime.UtcNow - change.Timestamp).TotalSeconds`
- [x] 5.4 In `PublishWithRetryAsync`: on retry-exhausted failure, increment `raytree.outbox.messages.failed`
- [x] 5.5 In `PublishChangeAsync`: after computing the envelope, record `raytree.outbox.payload.size` with `envelope.Payload.Length`
- [x] 5.6 In `MaybeRunCleanupAsync`: add the returned `deleted` count to `raytree.outbox.records.cleaned`; add `stale` count to `raytree.outbox.stale_unpublished.removed`

## 6. Instrument `ChangeSubscriber`

- [x] 6.1 At the start of `ProcessMessageAsync`: capture `Stopwatch.StartNew()` and `envelope.Timestamp` for later use
- [x] 6.2 On unknown entity type: increment `raytree.subscriber.messages.skipped` with `reason="unknown_type"`
- [x] 6.3 On dedup hit: increment `raytree.subscriber.messages.deduplicated` with `entity_type` and `change_type`
- [x] 6.4 On no-handler / no-matching-handler: increment `raytree.subscriber.messages.skipped` with `reason="no_handler"`
- [x] 6.5 In `InvokeWithRetryAsync`: time each handler call; record `raytree.subscriber.processing.duration` per attempt; on success record `raytree.subscriber.handler.attempts`; on retry-exhausted failure increment `raytree.subscriber.handler.failures`
- [x] 6.6 After successful dispatch in `ProcessMessageAsync`: increment `raytree.subscriber.messages.processed`; record `raytree.subscriber.lag.duration` as `(DateTime.UtcNow - envelope.Timestamp).TotalSeconds`

## 7. Hosting DI Registration (no OTel reference)

- [x] 7.1 In `RayTree.Hosting.AddChangeTracking`, register `RayTreeMeter` as a singleton via DI (resolved by `EntityChangeTracker`) so callers can also inject it for custom instrumentation
- [x] 7.2 Confirm `RayTree.Hosting.csproj` still has **no** `OpenTelemetry.*` package reference

## 8. New `RayTree.OpenTelemetry` Assembly

- [x] 8.1 Add `OpenTelemetry.Api` package pin to `Directory.Packages.props`
- [x] 8.2 Create new project `src/RayTree.OpenTelemetry/RayTree.OpenTelemetry.csproj` targeting `net8.0`; reference `OpenTelemetry.Api`; follow the same `TreatWarningsAsErrors=true`, nullable, and editorconfig conventions as the rest of the solution
- [x] 8.3 Add `RayTree.OpenTelemetry` project to `RayTree.sln`
- [x] 8.4 Create `RayTreeInstrumentation` public static class with `public const string MeterName = "RayTree";`
- [x] 8.5 Create `MeterProviderBuilderExtensions` with `public static MeterProviderBuilder AddRayTreeMetrics(this MeterProviderBuilder builder) => builder.AddMeter(RayTreeInstrumentation.MeterName);`
- [x] 8.6 Add XML doc on `AddRayTreeMetrics` listing the emitted instrument names and recommended bucket boundaries for `*.duration` histograms (e.g., `[0.001, 0.005, 0.01, 0.05, 0.1, 0.5, 1, 5, 10]` seconds)

## 9. New `RayTree.OpenTelemetry.Tests` Project

- [x] 9.1 Create `tests/RayTree.OpenTelemetry.Tests/RayTree.OpenTelemetry.Tests.csproj` following the same test-project conventions as `RayTree.Core.Tests`
- [x] 9.2 Add the test project to `RayTree.sln`
- [x] 9.3 Test: `RayTreeInstrumentation.MeterName == "RayTree"`
- [x] 9.4 Test: `AddRayTreeMetrics` on a `MeterProviderBuilder` registers a meter named `"RayTree"` (use a `MeterProvider` built with `InMemoryExporter` or use reflection on the builder state)

## 10. Core Instrumentation Tests (`RayTree.Core.Tests`)

- [x] 10.1 Test helper: `MeterListener`-backed `TestMetricsCollector` that filters by meter instance (using `UseMeter` with a per-test meter) and exposes recorded measurements per instrument name
- [x] 10.2 Test: `TrackInsertAsync` increments `raytree.outbox.writes` with `change_type="Insert"`
- [x] 10.3 Test: `OutboxPublisherService` increments `raytree.outbox.messages.published` on successful publish
- [x] 10.4 Test: `OutboxPublisherService` increments `raytree.outbox.messages.failed` after all retries exhausted
- [x] 10.5 Test: `OutboxPublisherService` records `raytree.outbox.batch.size` histogram with batch count
- [x] 10.6 Test: `OutboxPublisherService` records `raytree.outbox.publish.attempts = N` when publish succeeds after N-1 retries
- [x] 10.7 Test: `OutboxPublisherService` records `raytree.outbox.lag.duration` ≈ `(now - change.Timestamp)` within tolerance
- [x] 10.8 Test: `OutboxPublisherService` records `raytree.outbox.payload.size` equal to `envelope.Payload.Length`
- [x] 10.9 Test: `ObservableGauge` `raytree.outbox.pending` reports the current pending count from `IOutbox.GetPendingCountAsync` per entity type
- [x] 10.10 Test: `ChangeSubscriber` increments `raytree.subscriber.messages.processed` on successful dispatch
- [x] 10.11 Test: `ChangeSubscriber` increments `raytree.subscriber.messages.deduplicated` on duplicate message
- [x] 10.12 Test: `ChangeSubscriber` records `raytree.subscriber.handler.attempts = N` after N-1 transient failures
- [x] 10.13 Test: `ChangeSubscriber` records `raytree.subscriber.lag.duration` ≈ `(now - envelope.Timestamp)` within tolerance
- [x] 10.14 Test: `ChangeSubscriber` increments `raytree.subscriber.messages.skipped` with correct `reason` tags for `unknown_type` and `no_handler`
- [x] 10.15 Test: With no `MeterListener` attached, runtime services execute the full publish/subscribe path with no exceptions (smoke test — verify no throw)
- [x] 10.16 Verify all `*.duration` instruments have `Unit == "s"`
- [x] 10.17 Verify `raytree.outbox.payload.size` has `Unit == "By"`

## 11. Solution-wide Verification

- [x] 11.1 Verify `RayTree.Core.csproj` has no `OpenTelemetry.*` package reference (direct or transitive)
- [x] 11.2 Verify `RayTree.Hosting.csproj` has no `OpenTelemetry.*` package reference (direct or transitive)
- [x] 11.3 Verify `dotnet build RayTree.sln -c Release` passes with no new warnings
