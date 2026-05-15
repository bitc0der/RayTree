## 1. Dependencies and Package Versions

- [ ] 1.1 Add explicit `Microsoft.Extensions.Diagnostics` version pin to `Directory.Packages.props`
- [ ] 1.2 Add `Microsoft.Extensions.Diagnostics` package reference to `src/RayTree.Core/RayTree.Core.csproj`
- [ ] 1.3 Add `Microsoft.Extensions.Diagnostics` package reference to `src/RayTree.Hosting/RayTree.Hosting.csproj`

## 2. Core Metrics Class

- [ ] 2.1 Create `src/RayTree.Core/Telemetry/RayTreeMeter.cs` — wraps a `Meter("RayTree")` created from `IMeterFactory`; declares all instruments as readonly fields; exposes them as internal properties for use by runtime services
- [ ] 2.2 Define publisher instruments on `RayTreeMeter`: `raytree.outbox.messages.published` (Counter), `raytree.outbox.messages.failed` (Counter), `raytree.outbox.batch.size` (Histogram), `raytree.outbox.publish.duration` (Histogram, unit ms), `raytree.outbox.records.cleaned` (Counter), `raytree.outbox.stale_unpublished.removed` (Counter)
- [ ] 2.3 Define subscriber instruments on `RayTreeMeter`: `raytree.subscriber.messages.processed` (Counter), `raytree.subscriber.messages.deduplicated` (Counter), `raytree.subscriber.messages.skipped` (Counter), `raytree.subscriber.handler.failures` (Counter), `raytree.subscriber.handler.retries` (Counter), `raytree.subscriber.processing.duration` (Histogram, unit ms)
- [ ] 2.4 Implement `IDisposable` on `RayTreeMeter` to dispose the underlying `Meter`

## 3. Builder Wiring

- [ ] 3.1 Add `IMeterFactory? meterFactory = null` parameter to `ChangeTrackingBuilder` constructor; normalize null to `NullMeterFactory.Instance`; store as `_meterFactory`
- [ ] 3.2 Add `IMeterFactory? meterFactory = null` parameter to `ChangePublisherBuilder` constructor; normalize and store
- [ ] 3.3 Add `IMeterFactory? meterFactory = null` parameter to `ChangeSubscriberBuilder` constructor; normalize and store
- [ ] 3.4 Pass `_meterFactory` through `ChangeTrackingBuilder.BuildInternal()` when constructing `ChangePublisher` and `ChangeSubscriber`
- [ ] 3.5 Add `IMeterFactory meterFactory` parameter to `ChangePublisher` constructor; construct `RayTreeMeter` from it; pass meter to each `OutboxPublisherService` it creates
- [ ] 3.6 Add `RayTreeMeter meter` parameter to `OutboxPublisherService` constructor
- [ ] 3.7 Add `RayTreeMeter meter` parameter to `ChangeSubscriber` constructor

## 4. Instrument `OutboxPublisherService`

- [ ] 4.1 In `ProcessBatchAsync`: record `raytree.outbox.batch.size` after retrieving the batch
- [ ] 4.2 In `PublishWithRetryAsync`: start a `Stopwatch` before the retry loop; record `raytree.outbox.publish.duration` and increment `raytree.outbox.messages.published` (with `change_type` tag) on success; increment `raytree.outbox.messages.failed` when retries are exhausted
- [ ] 4.3 In `MaybeRunCleanupAsync`: add `deleted` count to `raytree.outbox.records.cleaned` after successful cleanup; add `stale` count to `raytree.outbox.stale_unpublished.removed`

## 5. Instrument `ChangeSubscriber`

- [ ] 5.1 In `ProcessMessageAsync`: increment `raytree.subscriber.messages.skipped` (reason `"unknown_type"`) when entity type cannot be resolved
- [ ] 5.2 In `ProcessMessageAsync`: increment `raytree.subscriber.messages.deduplicated` on dedup hit
- [ ] 5.3 In `ProcessMessageAsync`: increment `raytree.subscriber.messages.skipped` (reason `"no_handler"`) when no handlers match
- [ ] 5.4 In `ProcessMessageAsync`: start a `Stopwatch` before handler dispatch; record `raytree.subscriber.processing.duration` and increment `raytree.subscriber.messages.processed` on successful completion
- [ ] 5.5 In `InvokeWithRetryAsync`: increment `raytree.subscriber.handler.retries` on each retry attempt; increment `raytree.subscriber.handler.failures` when retries are exhausted and not `SkipOnFailure`

## 6. Hosting Integration

- [ ] 6.1 Add `AddRayTreeMetrics(this IMetricsBuilder builder)` extension method in `src/RayTree.Hosting` that calls `builder.AddMeter("RayTree")`
- [ ] 6.2 Update `AddChangeTracking` in `src/RayTree.Hosting` to resolve `IMeterFactory` from `IServiceProvider` and pass it to `ChangeTrackingBuilder`

## 7. Tests

- [ ] 7.1 Add unit test: `OutboxPublisherService` increments `raytree.outbox.messages.published` counter on successful publish (using `MeterListener`)
- [ ] 7.2 Add unit test: `OutboxPublisherService` increments `raytree.outbox.messages.failed` counter after all retries exhausted
- [ ] 7.3 Add unit test: `OutboxPublisherService` records `raytree.outbox.batch.size` histogram with batch count
- [ ] 7.4 Add unit test: `ChangeSubscriber` increments `raytree.subscriber.messages.processed` on successful dispatch
- [ ] 7.5 Add unit test: `ChangeSubscriber` increments `raytree.subscriber.messages.deduplicated` on duplicate message
- [ ] 7.6 Add unit test: `ChangeSubscriber` increments `raytree.subscriber.handler.retries` on transient handler failure
- [ ] 7.7 Add unit test: no instruments fire when `NullMeterFactory` is used (no `MeterListener` receives measurements)
- [ ] 7.8 Verify `dotnet build RayTree.sln -c Release` passes with no new warnings
