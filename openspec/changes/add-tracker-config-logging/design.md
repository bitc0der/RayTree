## Context

The existing `structured-logging` capability covers runtime events emitted by `OutboxPublisherService`, `ChangeSubscriber`, and `ChangeTrackingHostedService`. It does **not** cover the configuration/build phase. Today, when a caller wires up `ChangeTrackingBuilder` (directly or via `AddChangeTracking`), nothing is logged about which plugins were registered, which entity types were configured, or which defaults were applied. Operators bringing up a new service can only confirm correct wiring by waiting for the first runtime event (a publish, a consumer ACK, an error). Misconfigurations such as a forgotten `UseOutbox`, a wrong consumer factory, or a serializer/compressor mismatch surface as cryptic runtime failures.

The builder layer already receives an `ILoggerFactory` (`NullLoggerFactory` by default; the host's factory under DI) and forwards it to the publisher and subscriber builders. That same factory can produce a `ILogger<ChangeTrackingBuilder>` and a `ILogger<EntityChangeTracker>` at zero extra wiring cost.

## Goals / Non-Goals

**Goals:**
- Emit a structured `Information` log for every `Use*` builder call so the log stream documents how the tracker was wired up.
- Emit one `Information` summary log at `Build()` containing the configured entity types and the list of registered plugins.
- Emit `Information` start / complete logs from `EntityChangeTracker.InitializeAsync`, with `Debug` sub-step logs for publisher and consumer initialization.
- Preserve the existing opt-in semantics: `NullLoggerFactory` produces zero output and zero allocations on the hot path.
- Make the new output additive — no public-API changes; no test or sample needs to be modified to keep working.

**Non-Goals:**
- Changing the existing runtime-event log requirements in the `structured-logging` spec.
- Adding metrics or traces for configuration events (already covered by `opentelemetry-metrics`).
- Logging at `Trace` level — `Information` for top-level events and `Debug` for sub-steps is sufficient.
- Reformatting or rewriting existing log messages.

## Decisions

### Decision 1: Logger acquired once in the builder, never per-call
The builder resolves `ILogger<ChangeTrackingBuilder>` once in the constructor and caches it in a field. Every `Use*` and `ForEntity` method calls `_log.LogInformation(...)` on that cached instance. Matches the existing pattern in `OutboxPublisherService` and `ChangeSubscriber`.

### Decision 2: Build summary as a single structured event, not a multi-line dump
The build summary log entry uses `LogInformation("ChangeTracker built. EntityTypes={EntityTypes} Plugins={Plugins} HasCustomMeter={HasCustomMeter} HasCustomDeduplicationStore={HasCustomDeduplicationStore} HasCustomLoggerFactory={HasCustomLoggerFactory}", ...)` so that structured log sinks (Seq, Elastic, OTel logs) can index each property individually.

**Rationale:** Multi-line `LogInformation` payloads cannot be filtered or aggregated. A single structured event with rich properties is the idiomatic .NET logging pattern.

**Alternative considered:** Several `LogInformation` calls (one per plugin slot). Rejected — produces excessive noise at `Information` and forces operators to reconstruct the picture from several lines.

### Decision 3: Per-entity overrides log at `Debug`, not `Information`
The per-entity configuration delegate may apply many overrides; logging each at `Information` would drown out the more important global registrations. Instead, `ForEntity` logs the entity type at `Information` and each override (UseOutbox/UseSerializer/OnInsert/...) at `Debug`.

**Rationale:** Matches the existing convention in `OutboxPublisherService` (start/stop at `Information`; per-record events at `Debug`).

**Alternative considered:** Single summary log per entity type. Rejected — loses the ability to spot a forgotten override during diagnosis.

### Decision 4: Initialization logs live on `EntityChangeTracker`, not `ChangeTrackingBuilder`
`InitializeAsync` is a method on `EntityChangeTracker`. The tracker already has access to the `ILoggerFactory` (passed through `ChangePublisher` and `ChangeSubscriber`) and can resolve `ILogger<EntityChangeTracker>` once in its constructor.

**Rationale:** Keeps the builder concerned only with *configuration* events; runtime/lifecycle events stay with the tracker. The split mirrors the proposal/specs split (build summary → builder; init lifecycle → tracker).

**Alternative considered:** Have the builder log around the `InitializeAsync` call inside `Build()` / `BuildAsync()`. Rejected — would only cover sync `Build()`, not `tracker.InitializeAsync()` called via DI through the hosted service.

### Decision 5: Guard every logging call with `IsEnabled`
Every new log call (builder `Use*` Information logs, per-entity Debug logs, build-summary log, init lifecycle logs) is wrapped in `if (_log.IsEnabled(<level>))` to avoid `params object?[]` allocation and value-type boxing under `NullLoggerFactory`. `NullLogger.IsEnabled` returns `false` for all levels, so the body is fully skipped.

**Rationale:** The build-summary log in particular constructs a `{Plugins}` anonymous structure and an `{EntityTypes}` array; skipping that under `NullLoggerFactory` makes the "zero allocations" claim in the test (task 5.5) literally true.

### Decision 6: `EntityBuilder<TEntity>` constructor gains a logger parameter
`EntityBuilder<TEntity>` is `internal sealed` with a constructor taking `(ChangePublisherBuilder, ChangeSubscriberBuilder)`. Extending it to also accept `ILogger<ChangeTrackingBuilder>` is an internal-only signature change with no public API impact. `ChangeTrackingBuilder.ForEntity<TEntity>` passes its cached logger when constructing the inner builder; the same logger is forwarded to `SharedHandlerBuilder<TEntity>` and `IsolatedHandlerBuilder<TEntity>` so per-entity overrides log at `Debug` from a consistent category.

### Decision 7: DI-context log lives on `ChangeTrackingHostedService.StartAsync`, not the DI factory delegate
`AddChangeTracking` captures `configuration != null` into a small DI-registered record (or sets a field on the hosted service via DI). `ChangeTrackingHostedService.StartAsync` (which runs exactly once per host instance) emits the `Information` "ChangeTracking starting" log with `{ConfigurationBound}`.

**Rationale:** Logging from inside the singleton factory delegate appears one-shot but is not guaranteed if the lifetime is ever changed or if the tracker is resolved early during a concurrent startup. The hosted-service `StartAsync` is the only place the framework guarantees a single invocation per host. This also keeps the existing hosted-service lifecycle log (consumer start / graceful stop) and the new DI-context log co-located.

## Risks / Trade-offs

- **Risk: log volume during startup spikes** → Mitigation: top-level Use* calls log at `Information` (small, bounded — one per call); per-entity overrides log at `Debug` (filterable). A typical service with 5 entity types and global plugins emits roughly 10–20 `Information` lines at startup, which is acceptable for one-shot bring-up events.
- **Risk: existing assertions on log output break** → Mitigation: the existing `structured-logging` requirements are not modified, only extended. Existing test fixtures that capture logs by exact message will still pass; tests that count `Information` entries over a build cycle will need to update — these tests are internal to `RayTree.Core.Tests` and can be updated in the same change.
- **Risk: structured-property name churn** → Mitigation: property names (`{EntityType}`, `{Plugin}`, `{EntityTypes}`, `{Plugins}`, `{HasCustomMeter}`, etc.) are documented in the spec scenarios so downstream parsers have a stable contract.
- **Trade-off: a small amount of allocation on the build path** → Acceptable: build is a one-time event per process. Hot-path runtime logging is unaffected.

## Migration Plan

1. Implement builder-side logging in `ChangeTrackingBuilder`.
2. Implement initialization logging in `EntityChangeTracker.InitializeAsync`.
3. Implement DI registration log in `ServiceCollectionExtensions.AddChangeTracking`.
4. Add unit tests in `tests/RayTree.Core.Tests` using an in-memory `ILoggerProvider` to capture and assert on entries.
5. Update `CLAUDE.md` to describe the new configuration-time logs alongside the existing logging-placement rule.
6. No data migration. No public-API change. No rollback needed — the feature is additive and silent under `NullLoggerFactory`.

## Open Questions

- Should the build summary include the global `OutboxPublisherOptions` and `SubscriberOptions` values? **Decision: No** — these can be large and noisy; if operators need them they can log the options themselves via the host. The summary lists only plugin type names and capability flags.
- Should per-entity override `Debug` logs include the override target type (e.g. the IOutbox concrete type) or just the override slot name? **Decision: include both** — `{EntityType}`, `{Override}` (slot), `{Plugin}` (type name).
