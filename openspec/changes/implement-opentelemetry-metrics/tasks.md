## 1. Outbox Abstraction Extension

- [ ] 1.1 Add `Task<long> GetPendingCountAsync(Type entityType, CancellationToken)` to `IOutbox`
- [ ] 1.2 Implement `GetPendingCountAsync` in `InMemoryOutbox` (count entries with `Published == false` matching entity type)
- [ ] 1.3 Implement `GetPendingCountAsync` in `PostgreSqlOutbox<TEntity>` as `SELECT count(*) FROM <table> WHERE published = FALSE` (uses existing `idx_*_outbox_unpublished` partial index)
- [ ] 1.4 Add unit/integration tests for `GetPendingCountAsync` on both implementations

## 2. Core Metrics Class

- [ ] 2.1 Create `src/RayTree.Core/Telemetry/RayTreeMeter.cs` — constructs `new Meter("RayTree", <assembly-version>)`; implements `IDisposable`; disposes the underlying `Meter`
- [ ] 2.2 Declare publisher counters: `raytree.outbox.writes`, `raytree.outbox.messages.published`, `raytree.outbox.messages.failed`, `raytree.outbox.records.cleaned`, `raytree.outbox.stale_unpublished.removed`
- [ ] 2.3 Declare publisher histograms (with units): `raytree.outbox.batch.size` (`{messages}`), `raytree.outbox.publish.duration` (`s`), `raytree.outbox.publish.attempts` (`{attempts}`), `raytree.outbox.lag.duration` (`s`), `raytree.outbox.payload.size` (`By`)
- [ ] 2.4 Declare subscriber counters: `raytree.subscriber.messages.processed`, `raytree.subscriber.messages.deduplicated`, `raytree.subscriber.messages.skipped`, `raytree.subscriber.handler.failures`
- [ ] 2.5 Declare subscriber histograms: `raytree.subscriber.handler.attempts` (`{attempts}`), `raytree.subscriber.processing.duration` (`s`), `raytree.subscriber.lag.duration` (`s`)
- [ ] 2.6 Add method `RegisterPendingGauge(Func<IEnumerable<(Type entityType, IOutbox outbox)>>)` that registers an `ObservableGauge<long>` named `raytree.outbox.pending`; on each callback invocation iterate the registered entities and yield `Measurement<long>(count, new KeyValuePair<string,object?>("entity_type", entityType.Name))`
- [ ] 2.7 Provide internal helpers for emitting measurements with the standard tag set (`entity_type`, `change_type`, optional `reason`) to keep call sites terse

## 3. Builder Wiring

- [ ] 3.1 Add `UseMeter(RayTreeMeter meter)` to `IChangeTrackingBuilder` and `ChangeTrackingBuilder`
- [ ] 3.2 Add `UseMeter(RayTreeMeter meter)` to `IChangePublisherBuilder` and `ChangePublisherBuilder`
- [ ] 3.3 Add `UseMeter(RayTreeMeter meter)` to `IChangeSubscriberBuilder` and `ChangeSubscriberBuilder`
- [ ] 3.4 In each builder's `Build()` / `BuildInternal()`, construct a default `RayTreeMeter` when none was supplied
- [ ] 3.5 Pass the resolved `RayTreeMeter` to `ChangePublisher`, `OutboxPublisherService`, and `ChangeSubscriber` via constructor
- [ ] 3.6 Make `EntityChangeTracker` own the `RayTreeMeter` lifecycle: dispose it in `EntityChangeTracker.Dispose`
- [ ] 3.7 After publisher initialisation, call `meter.RegisterPendingGauge` with a callback that enumerates `ChangePublisher`'s registered entity types and their outboxes

## 4. Instrument `EntityChangeTracker` Writes

- [ ] 4.1 In `TrackInsertAsync`, increment `raytree.outbox.writes` with `entity_type` and `change_type="Insert"` after `IOutbox.WriteAsync` returns successfully
- [ ] 4.2 In `TrackUpdateAsync`, increment with `change_type="Update"`
- [ ] 4.3 In `TrackDeleteAsync`, increment with `change_type="Delete"`

## 5. Instrument `OutboxPublisherService`

- [ ] 5.1 In `ProcessBatchAsync`: record `raytree.outbox.batch.size` after retrieving the batch
- [ ] 5.2 In `PublishWithRetryAsync`: time each call to `PublishChangeAsync` with a `Stopwatch` and record `raytree.outbox.publish.duration` in seconds per attempt (success or failure)
- [ ] 5.3 In `PublishWithRetryAsync`: on success, record `raytree.outbox.publish.attempts` with the total attempt count; increment `raytree.outbox.messages.published`; record `raytree.outbox.lag.duration` as `(DateTime.UtcNow - change.Timestamp).TotalSeconds`
- [ ] 5.4 In `PublishWithRetryAsync`: on retry-exhausted failure, increment `raytree.outbox.messages.failed`
- [ ] 5.5 In `PublishChangeAsync`: after computing the envelope, record `raytree.outbox.payload.size` with `envelope.Payload.Length`
- [ ] 5.6 In `MaybeRunCleanupAsync`: add the returned `deleted` count to `raytree.outbox.records.cleaned`; add `stale` count to `raytree.outbox.stale_unpublished.removed`

## 6. Instrument `ChangeSubscriber`

- [ ] 6.1 At the start of `ProcessMessageAsync`: capture `Stopwatch.StartNew()` and `envelope.Timestamp` for later use
- [ ] 6.2 On unknown entity type: increment `raytree.subscriber.messages.skipped` with `reason="unknown_type"`
- [ ] 6.3 On dedup hit: increment `raytree.subscriber.messages.deduplicated` with `entity_type` and `change_type`
- [ ] 6.4 On no-handler / no-matching-handler: increment `raytree.subscriber.messages.skipped` with `reason="no_handler"`
- [ ] 6.5 In `InvokeWithRetryAsync`: time each handler call; record `raytree.subscriber.processing.duration` per attempt; on success record `raytree.subscriber.handler.attempts`; on retry-exhausted failure increment `raytree.subscriber.handler.failures`
- [ ] 6.6 After successful dispatch in `ProcessMessageAsync`: increment `raytree.subscriber.messages.processed`; record `raytree.subscriber.lag.duration` as `(DateTime.UtcNow - envelope.Timestamp).TotalSeconds`

## 7. Hosting Integration

- [ ] 7.1 Add `OpenTelemetry.Api` package pin to `Directory.Packages.props`
- [ ] 7.2 Add `OpenTelemetry.Api` reference to `src/RayTree.Hosting/RayTree.Hosting.csproj`
- [ ] 7.3 Add `AddRayTreeMetrics(this MeterProviderBuilder builder)` extension that calls `builder.AddMeter("RayTree")`
- [ ] 7.4 In `AddChangeTracking`, register `RayTreeMeter` as a singleton via DI (resolved by `EntityChangeTracker`) so callers can also inject it for custom instrumentation

## 8. Tests

- [ ] 8.1 Test helper: `MeterListener`-backed `TestMetricsCollector` that filters by meter instance (using `UseMeter` with a per-test meter) and exposes recorded measurements per instrument name
- [ ] 8.2 Test: `TrackInsertAsync` increments `raytree.outbox.writes` with `change_type="Insert"`
- [ ] 8.3 Test: `OutboxPublisherService` increments `raytree.outbox.messages.published` on successful publish
- [ ] 8.4 Test: `OutboxPublisherService` increments `raytree.outbox.messages.failed` after all retries exhausted
- [ ] 8.5 Test: `OutboxPublisherService` records `raytree.outbox.batch.size` histogram with batch count
- [ ] 8.6 Test: `OutboxPublisherService` records `raytree.outbox.publish.attempts = N` when publish succeeds after N-1 retries
- [ ] 8.7 Test: `OutboxPublisherService` records `raytree.outbox.lag.duration` ≈ `(now - change.Timestamp)` within tolerance
- [ ] 8.8 Test: `OutboxPublisherService` records `raytree.outbox.payload.size` equal to `envelope.Payload.Length`
- [ ] 8.9 Test: `ObservableGauge` `raytree.outbox.pending` reports the current pending count from `IOutbox.GetPendingCountAsync` per entity type
- [ ] 8.10 Test: `ChangeSubscriber` increments `raytree.subscriber.messages.processed` on successful dispatch
- [ ] 8.11 Test: `ChangeSubscriber` increments `raytree.subscriber.messages.deduplicated` on duplicate message
- [ ] 8.12 Test: `ChangeSubscriber` records `raytree.subscriber.handler.attempts = N` after N-1 transient failures
- [ ] 8.13 Test: `ChangeSubscriber` records `raytree.subscriber.lag.duration` ≈ `(now - envelope.Timestamp)` within tolerance
- [ ] 8.14 Test: `ChangeSubscriber` increments `raytree.subscriber.messages.skipped` with correct `reason` tags for `unknown_type` and `no_handler`
- [ ] 8.15 Test: With no `MeterListener` attached, runtime services execute the full publish/subscribe path with no exceptions and no allocations attributable to metrics (smoke test only — verify no throw)
- [ ] 8.16 Verify all `*.duration` instruments have `Unit == "s"`
- [ ] 8.17 Verify `raytree.outbox.payload.size` has `Unit == "By"`
- [ ] 8.18 Verify `dotnet build RayTree.sln -c Release` passes with no new warnings
