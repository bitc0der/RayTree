## Why

Today the tracker emits log output for runtime events (outbox poll errors, publish retries, subscriber retries, hosted service lifecycle) but the **configuration and build phase is virtually silent**. When operators stand up a new service or change the wiring, they have no visible evidence of what plugins were registered, which entity types were configured, or what defaults were applied. Misconfigurations (forgotten outbox, wrong consumer factory, mismatched serializer) only surface much later as runtime errors that are hard to trace back to the build step. Adding structured configuration-time logs makes the tracker's setup observable and dramatically shortens diagnosis time during bring-up.

## What Changes

- `ChangeTrackingBuilder` emits structured `Information` logs for each top-level configuration call (`UseOutbox`, `UsePublisher`, `UseSerializer`, `UseCompressor`, `UseRepository`, `UseDeduplicationStore`, `UseMeter`, `UsePublisherOptions`, `UseSubscriberOptions`) recording which plugin type was registered.
- `ChangeTrackingBuilder.ForEntity<TEntity>` emits an `Information` log naming the configured entity type and (at `Debug`) the per-entity plugin overrides applied inside the configure delegate.
- `BuildInternal` / `Build` / `BuildAsync` log a single `Information` "tracker built" summary line including: configured entity types, whether a custom meter / dedup store / logger factory was supplied, and the registered outbox/publisher/serializer/compressor plugin type names.
- `EntityChangeTracker.InitializeAsync` logs `Information` at start and completion of initialization (publisher initialization + consumer connection initialization), plus `Debug` per-step events (`outbox initialized`, `publisher initialized`, `consumer initialized`).
- Validation / defaulting decisions inside the builder (e.g. "no meter supplied, creating default `RayTreeMeter`"; "no logger factory supplied, falling back to `NullLoggerFactory`") emit `Debug` logs so operators can confirm which defaults are in effect.
- The existing `ChangeTrackingBuilder` constructor's `ILoggerFactory` is now also used to create an internal `ILogger<ChangeTrackingBuilder>`; under DI (`AddChangeTracking`) it is automatically the host's factory, so no caller changes are required to get the new output.
- Tests cover: each builder method emits exactly one configuration log; the build summary contains the expected structured properties; `NullLoggerFactory` produces zero allocations / output.

## Capabilities

### New Capabilities
_None_

### Modified Capabilities
- `structured-logging`: extends the spec with new requirements covering builder configuration logs, build summary log, and initialization lifecycle logs.

## Impact

- **Affected code**:
  - `src/RayTree.Core/Tracking/ChangeTrackingBuilder.cs` — acquire `ILogger<ChangeTrackingBuilder>` from the injected factory and emit logs from each `Use*` / `ForEntity` / `BuildInternal` method.
  - `src/RayTree.Core/Tracking/EntityChangeTracker.cs` — acquire `ILogger<EntityChangeTracker>` (already has one for runtime via publisher/subscriber; add a tracker-level logger or reuse) and emit init start/complete logs from `InitializeAsync`.
  - `src/RayTree.Core/Distribution/ChangePublisherBuilder.cs` and `src/RayTree.Core/Handling/ChangeSubscriberBuilder.cs` — optional `Debug` logs when per-entity overrides are stored.
- **APIs**: no public surface change. New logs are additive and respect the existing `ILoggerFactory` opt-in (silent under `NullLoggerFactory`).
- **Dependencies**: none — uses `Microsoft.Extensions.Logging.Abstractions` already referenced.
- **Tests**: new test fixtures in `tests/RayTree.Core.Tests` using an in-memory `ILoggerProvider` to capture log entries and assert structured properties / counts.
