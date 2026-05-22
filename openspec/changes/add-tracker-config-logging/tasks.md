## 1. Builder configuration logging

- [ ] 1.1 Add a cached `ILogger<ChangeTrackingBuilder>` field on `ChangeTrackingBuilder`, initialised from `_loggerFactory` in the constructor.
- [ ] 1.2 Emit an `Information` log from each of `UseOutbox`, `UsePublisher`, `UseSerializer`, `UseCompressor`, `UseRepository`, `UseDeduplicationStore`, `UseMeter`, `UsePublisherOptions`, and `UseSubscriberOptions` recording the registered plugin's CLR type name (or options class name) as a structured `{Plugin}` property.
- [ ] 1.3 In `ForEntity<TEntity>`, emit an `Information` log with `{EntityType}` before invoking the configure delegate.
- [ ] 1.4 Extend `EntityBuilder<TEntity>`'s constructor (currently `(ChangePublisherBuilder, ChangeSubscriberBuilder)`) to also accept `ILogger<ChangeTrackingBuilder>`; update `ChangeTrackingBuilder.ForEntity<TEntity>` to pass its cached logger. Forward the same logger to `SharedHandlerBuilder<TEntity>` and `IsolatedHandlerBuilder<TEntity>`.
- [ ] 1.5 In the post-fork builders, emit `Debug` logs for each per-entity override (`UseOutbox`, `UsePublisher`, `UseSerializer`, `UseCompressor`, `UseRepository`, `UseConsumer`, `UseConsumerFactory`, `UseSubscriberOptions`, `OnInsert`/`OnUpdate`/`OnDelete`/`OnChange`) with structured `{EntityType}`, `{Override}`, and `{Plugin}` properties.
- [ ] 1.6 Guard every new log call (Information and Debug) with `if (_log.IsEnabled(<level>))` to ensure zero allocation under `NullLoggerFactory` (see design Decision 5).

## 2. Build summary log

- [ ] 2.1 In `ChangeTrackingBuilder.BuildInternal`, after wiring the publisher and subscriber, emit a single `Information` log (guarded by `IsEnabled(LogLevel.Information)`) with structured properties `{EntityTypes}` (string[] of configured entity-type names), `{Plugins}` (an anonymous structure naming the global outbox, publisher, serializer, compressor CLR types or `"<none>"`), `{HasCustomMeter}`, `{HasCustomDeduplicationStore}`, and `{HasCustomLoggerFactory}` (bool). Read the entity-type and plugin information directly from the builder's existing private dictionaries — do NOT introduce new accessor methods on `ChangePublisherBuilder` / `ChangeSubscriberBuilder`.
- [ ] 2.2 Track whether the caller supplied an `ILoggerFactory` via a new internal flag on `ChangeTrackingBuilder` so `{HasCustomLoggerFactory}` is correctly reported.
- [ ] 2.3 Emit a `Debug` log when `BuildInternal` creates a default `RayTreeMeter` because `UseMeter` was not called, stating the meter is owned by the tracker.

## 3. Tracker initialization logging

- [ ] 3.1 Add an `ILoggerFactory` parameter to `EntityChangeTracker`'s internal constructor (passed through from `ChangeTrackingBuilder.BuildInternal`). Resolve `ILogger<EntityChangeTracker>` from it in the constructor and store in a field.
- [ ] 3.2 In `EntityChangeTracker.InitializeAsync`, log an `Information` "tracker initialization started" entry before any sub-step.
- [ ] 3.3 After publisher initialization completes (call to `ChangePublisher.InitializeAsync`), log a `Debug` "publisher initialized" entry with `{EntityTypeCount}` structured property.
- [ ] 3.4 After consumer connections initialize, log a `Debug` "consumers initialized" entry with `{ConsumerCount}` structured property.
- [ ] 3.5 On success, log an `Information` "tracker initialization completed" entry.
- [ ] 3.6 Do NOT add a catch-log-rethrow wrapper around the whole `InitializeAsync` body — the publisher/subscriber/plugin layers already log their own `Error` entries on initialization failures, and a tracker-level catch would double-log. Instead, log a single `Warning` "tracker initialization aborted" (no exception payload) immediately before any exception propagates, so operators can see the abort point without losing the inner error context.

## 4. DI startup logging

- [ ] 4.1 In `ServiceCollectionExtensions.AddChangeTracking`, capture `configuration != null` into a small internal record/options class registered as a singleton (e.g. `ChangeTrackingDiContext { bool ConfigurationBound }`).
- [ ] 4.2 Inject `ChangeTrackingDiContext` into `ChangeTrackingHostedService`. In its `StartAsync`, emit a single `Information` "ChangeTracking starting" log with `{ConfigurationBound}`. This guarantees one-shot firing per host instance (hosted services start exactly once) and avoids the singleton-factory pitfall where a future lifetime change or concurrent resolution could double-log.

## 5. Tests

- [ ] 5.1 Add `tests/RayTree.Core.Tests/Logging/ConfigurationLoggingTests.cs` with an in-memory `ILoggerProvider` capturing log entries.
- [ ] 5.2 Test: each `Use*` call emits exactly one `Information` entry with `{Plugin}` equal to the expected CLR type name.
- [ ] 5.3 Test: `ForEntity<Order>` emits one `Information` entry with `{EntityType}="Order"` and `Debug` entries per applied override.
- [ ] 5.4 Test: `Build()` emits the build summary log with all required structured properties; verify `{Plugins}` reports `"<none>"` for unregistered slots.
- [ ] 5.5 Test: building with `NullLoggerFactory` produces zero log entries.
- [ ] 5.6 Test: `EntityChangeTracker.InitializeAsync` logs start, sub-steps (`Debug`), and completion in order; on a forced sub-step failure, a `Warning` "tracker initialization aborted" is logged before the exception propagates (no `Error` payload — that's the inner service's responsibility).
- [ ] 5.7 Test: `ChangeTrackingHostedService.StartAsync` emits exactly one `Information` "ChangeTracking starting" log per host instance, with `{ConfigurationBound}` matching how `AddChangeTracking` was called.
- [ ] 5.8 Update any existing log-counting tests in `tests/RayTree.Core.Tests` to account for new `Information` entries (or filter their assertions by message/category).

## 6. Documentation

- [ ] 6.1 Update `CLAUDE.md` "Logging placement rule" section to mention that `ChangeTrackingBuilder` and `EntityChangeTracker.InitializeAsync` now emit configuration and lifecycle logs.
- [ ] 6.2 Add a short "Configuration logs" subsection to `CLAUDE.md` (or a docs/logging.md if one exists) listing the structured property names introduced here (`{Plugin}`, `{EntityType}`, `{EntityTypes}`, `{Plugins}`, `{HasCustomMeter}`, `{HasCustomDeduplicationStore}`, `{HasCustomLoggerFactory}`, `{Override}`, `{ConfigurationBound}`) so operators have a single reference for log-parsing.

## 7. Verification

- [ ] 7.1 Run `dotnet build RayTree.slnx -c Release` and confirm no warnings (TreatWarningsAsErrors is on).
- [ ] 7.2 Run `dotnet test tests/RayTree.Core.Tests` and confirm all tests pass.
- [ ] 7.3 Run `openspec status --change add-tracker-config-logging` and confirm the change is marked complete.
