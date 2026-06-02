## Context

Connection recovery (PR archived 2026-05-24) introduced two deliberately asymmetric decisions:

1. **Retry logic is per-plugin.** Each plugin that owns a reconnect loop (Postgres LISTEN, Kafka publisher/consumer) hand-rolls ~20 lines of exponential-backoff math. There is **no** shared `ConnectionRetry` helper in Core — extracting one was explicitly rejected because it would force `InternalsVisibleTo` exposure (plugins reaching into Core internals) or a public-API commitment to one retry shape. The exception classifiers (`PostgresFault.IsConnectionFault`, Kafka's `Error.IsFatal` check) are likewise per-plugin.

2. **Config shape is shared.** A single `ConnectionRecoveryOptions` record lives in `RayTree.Core.Resilience` and is referenced by `NotificationBasedPublisherOptions`, `KafkaPublisherOptions`, and `KafkaConsumerOptions`. `RayTree.Hosting` binds it generically from `ChangeTracking:{Publisher,Subscriber}:ConnectionRecovery` into named options via `ChangeTrackingRecoveryKeys`.

The asymmetry is the problem. The shared record is the *only* remaining config coupling from Core to recovery-capable plugins, and it sits directly on top of duplicated loops that consume it. It also leaks: RabbitMQ never references it (SDK owns recovery), and `Factor`/`MaxAttempts`/`InitialDelay` are only meaningful to a hand-rolled exponential loop — a future library-managed plugin would inherit fields that silently do nothing. The host-bound default is also notional: `AddChangeTracking` binds the record into named options, but nothing auto-injects it into plugin construction — callers must resolve `IOptionsMonitor<ConnectionRecoveryOptions>.Get(key)` and merge by hand (documented in `ChangeTrackingRecoveryKeys`). So the "shared default" buys almost nothing in practice.

This change moves the config next to the loop that consumes it, completing decision (1)'s logic: if the loop is per-plugin, the loop's tuning record should be too.

## Goals / Non-Goals

**Goals:**
- Remove `ConnectionRecoveryOptions` from `RayTree.Core`. Give each recovery-owning plugin its own options type carrying only the fields its loop uses: `PostgresConnectionRecoveryOptions` (PostgreSQL), `KafkaConnectionRecoveryOptions` (Kafka).
- Repoint the three plugin options classes' `ConnectionRecovery` property to the plugin-local type, preserving field names, defaults, and validation semantics (type-identity move, not behavioral).
- Remove the Hosting generic binding (`ChangeTrackingRecoveryKeys` + the two `Configure<ConnectionRecoveryOptions>` calls), since the shared type it bound no longer exists and the merge was manual anyway.
- Remove the connection-recovery **metrics** entirely: all four `raytree.connection.*` instruments, the three `RayTreeMeter` facade methods, the internal state-gauge registry, and every emission call site. The other `RayTreeMeter` instruments stay; `RayTreeMeter` itself stays a required constructor dependency where it already was (it still serves outbox/subscriber metrics).
- Preserve all recovery *behavior* and *logs*: reconnect loops, exception classifiers, the `IOutbox` connection-fault members, the `Error→Warning` outbox log demotion, and every recovery log entry are untouched.

**Non-Goals:**
- Changing the retry/backoff math, the exception classifiers, or any log levels/messages (beyond deleting the now-orphaned metric calls inside otherwise-unchanged log handlers).
- Removing recovery *behavior* or recovery *logs*. Only metric emission is removed.
- Adding per-plugin configuration binding in Hosting (callers bind their own sections, or configure in code).
- Extracting a shared backoff helper or value type. (See Decisions — explicitly rejected to avoid re-introducing the coupling we're removing.)
- Removing other `RayTreeMeter` instruments or the meter itself.

## Decisions

### 1. Two plugin-local options types, not one shared and not one per consumer

`RayTree.Plugins.PostgreSQL` defines `PostgresConnectionRecoveryOptions`; `RayTree.Plugins.Kafka` defines `KafkaConnectionRecoveryOptions`. Both are near-verbatim copies of the current `ConnectionRecoveryOptions` (same six members, same per-field init guards, same `Validate()` cross-field check).

- **Why two and not three** (publisher vs consumer in Kafka): both Kafka loops have identical tuning needs and live in the same assembly. One `KafkaConnectionRecoveryOptions` shared by `KafkaPublisherOptions` and `KafkaConsumerOptions` keeps the Kafka surface DRY *within its own boundary* — that's intra-assembly reuse, not cross-package coupling, so it's free.
- **Why copy the validation** rather than share a Core helper: the project already accepted this exact trade for the retry loop ("two short copies is cheaper than `InternalsVisibleTo` or a public-API retry-shape commitment"). The validated options record is ~90 lines of pure, stable data with no plugin-specific branching; duplicating it across two assemblies is the same honest cost as the duplicated loops it pairs with. **Alternative considered:** a shared public `BackoffSchedule` value type in Core. Rejected *for this change* — it re-introduces the Core→plugin type dependency we're removing, just under a different name. If a third recovery-owning plugin ever lands and a true common shape is proven, extract then (YAGNI).

### 2. Keep field names and defaults identical

`Enabled=true`, `InitialDelay=1s`, `MaxDelay=30s`, `Factor=2.0`, `JitterFraction=0.2`, `MaxAttempts=null`. Validation: `InitialDelay>0`, `MaxDelay>=InitialDelay` (via `Validate()`), `Factor>=1`, `0<=JitterFraction<=1`, `MaxAttempts==null||>0`. Identical names mean the appsettings *keys* a caller binds don't change — only the section's parent and the bound CLR type do. Identical defaults mean no behavioral drift for callers who never set the options.

### 3. Remove Hosting binding outright (no replacement plumbing)

Delete `ChangeTrackingRecoveryKeys` and the two `services.Configure<ConnectionRecoveryOptions>(...)` calls in `ServiceCollectionExtensions`. Callers who want config-driven recovery set it on the plugin options directly in the `UseKafka`/`UsePostgreSqlOutbox` configure lambda, optionally reading their own bound section. **Why no per-plugin binding:** wiring bound defaults into plugin construction would require `IServiceProvider` plumbing through every plugin builder-extension signature — out of scope when this change was archived, and still out of scope. The manual-merge pattern the old keys documented had no automatic effect; removing it loses no working behavior.

### 4. Remove the connection metrics, keep the logs

The connection-recovery metrics were the *second* piece of Core that plugins reach into (via the `RayTreeMeter` facade). Removing them alongside the config completes the decoupling: after this change, recovery is observed through logs only, and plugins touch Core's recovery surface not at all.

- **All four instruments go** (`disconnects`, `recoveries`, `recovery.duration`, `state`) plus `RecordConnectionDisconnect` / `RecordConnectionRecovery` / `RegisterConnectionStateGauge`, the `_connectionStateSources` list, the `_connectionStateGate` lock, the `ObserveConnectionStates` callback, and the `ConnectionStateSubscription` nested type.
- **Logs are the surviving signal.** Postgres/Kafka already log retry attempts, recovery, and exhaustion at `Information`/`Error`; RabbitMQ publisher logs `Warning` on shutdown and `Information` on recovery. None of that changes. The `Error→Warning` outbox-batch demotion (driven by `IOutbox.IsConnectionFault`) also stays — it is a logging behavior, not a metric one, and the classifier members it depends on remain.
- **RabbitMQ is asymmetric and must be handled as two cases:**
  - *Publisher* keeps all three event handlers — they emit the recovery logs. Strip only the `_meter` field, the constructor `RayTreeMeter?` parameter, the gauge registration, and the two `RecordConnection*` calls. The `_lastShutdownAt` duration tracking stays (the recovery log prints `{Duration}`).
  - *Consumer* has **no logger**; its event subscriptions existed solely to feed metrics. Remove the `ConnectionShutdownAsync`/`RecoverySucceededAsync` subscriptions, their handlers, the gauge registration, and the `RayTreeMeter? meter` constructor parameter outright. The consumer stops observing recovery entirely — which is acceptable because it never produced any operator-visible signal except the metrics now being removed.
- **`OutboxPublisherService` / `NotificationBasedPublisher`** keep their `_outboxUnhealthy` / per-outbox unhealthy-state tracking because it still gates the log demotion and the single "outbox connection recovered" `Information` log. Only the `RecordConnectionDisconnect` / `RecordConnectionRecovery` calls are deleted from those paths. `RayTreeMeter` remains a required constructor parameter on both (still used for outbox metrics).
- **Builder/extension plumbing:** the RabbitMQ builder and subscriber extensions stop forwarding a `RayTreeMeter` into the publisher/consumer constructors. Wherever the meter was passed only for connection metrics, the argument is dropped.

**Alternative considered:** keep the instruments but stop emitting (no-op). Rejected — dead instruments on the public meter mislead operators into thinking the series exist; a clean removal is honest and shrinks `RayTreeMeter`. **Alternative considered:** move the metrics per-plugin (each plugin owns its own meter). Rejected — the user's intent is removal, not relocation, and per-plugin meters would fragment the single `"RayTree"` meter that the OTel integration deliberately exposes.

### 5. Narrow RayTreeMeter's public emit surface to internal

Removing the connection facade exposes a latent simplification. `RayTreeMeter` had three categories of public method: the connection facade (now removed), and the publisher-side emit/register methods (`RecordPublishSuccess`, `RecordPublishFailure`, `RecordPayloadSize`, `RecordBatchSize`, `RegisterPendingGauge`). The connection facade was public **out of necessity** — Kafka and RabbitMQ plugins have no `InternalsVisibleTo` and could only reach it through the public API. The other five were public only by association; their actual callers are:

- Core itself (`OutboxPublisherService`, `ChangeSubscriber`, `EntityChangeTracker`, `ChangeTrackingBuilder`), and
- `NotificationBasedPublisher` in `RayTree.Plugins.PostgreSQL` (the NOTIFY fast-path emits the same publish metrics) — which **is** an `InternalsVisibleTo`-privileged assembly (`RayTree.Core.csproj` grants it, alongside `RayTree.EntityFrameworkCore` and the test projects).

So all five can become `internal` with no loss of reachability: every caller already sees Core internals. Kafka/RabbitMQ never call them (their publish metrics are emitted by Core's `OutboxPublisherService` on their behalf). The net result is that **`RayTreeMeter` exposes no public way to emit a metric** — its public surface is `MeterName`, the two constructors, `DefaultPendingCacheTtl`, and `Dispose()`. It becomes a construct-it / let-Core-fill-it / observe-via-OTel type. This is the correct shape: metric *emission* is a Core-internal implementation concern; metric *observation* is the public contract, and that lives in `RayTree.OpenTelemetry` (`AddRayTreeMetrics`) and the `"RayTree"` meter name, both untouched.

**Why fold it in now rather than later:** the visibility change is only *safe and obvious* once the connection facade is gone — while that facade existed, "all emit methods are internal" was false, so the narrowing had no clean line to draw. Doing both in one change leaves the type in a coherent end state instead of a half-public one. **Alternative considered:** leave them public for hypothetical caller "custom instrumentation." Rejected — these methods take RayTree-internal shapes (`ChangeType`, entity `Type`, lag seconds) that only Core can meaningfully populate; they were never a usable public extension point. A caller wanting custom metrics uses their own `Meter`, not RayTree's emit methods.

### 6. Test relocation

`ConnectionRecoveryOptionsTests` (validation/defaults) splits into the PostgreSQL and Kafka test projects, asserting against the plugin-local type. `ConnectionRecoveryConfigurationTests` (Hosting binding) is removed — the binding it tested is gone. `NotificationBasedPublisherRecoveryTests` and the Kafka recovery tests update the referenced type name only.

## Risks / Trade-offs

- **[Loss of metric-based recovery observability]** → Deliberate, per the request. Operators relying on `raytree.connection.*` dashboards/alerts lose them; mitigation is the retained log signal (recovery logs at `Warning`/`Information`/`Error` with `{Component}`/`{Endpoint}`/`{Duration}`). Call this out prominently in CHANGELOG as a breaking observability change, since it fails silently — a metric that stops existing produces no error, just a flat-lined chart. The RabbitMQ *consumer* becomes entirely unobservable for recovery (no logger, no metrics); accept, as it matches its pre-existing no-logger posture.
- **[Breaking change for callers referencing the type or the bound config sections]** → Migration is mechanical: rename `ConnectionRecoveryOptions` → `PostgresConnectionRecoveryOptions` / `KafkaConnectionRecoveryOptions` at the use site; move `ChangeTracking:Publisher:ConnectionRecovery` appsettings under the plugin's own options binding (or set in code). Field names are unchanged, so JSON key paths below the section parent are unaffected. Documented in CHANGELOG with a before/after snippet.
- **[Removing the meter param from RabbitMQ constructors is a source break for direct constructor callers]** → The param was optional (`RayTreeMeter? meter = null`); most callers go through the builder extensions and are unaffected. Direct constructor callers drop the argument. Documented in CHANGELOG.
- **[Validation logic now lives in two files; a future fix could be applied to one and missed in the other]** → Accept, consistent with the existing duplicated-loop decision. The records are stable (no churn since introduction). A shared unit-test helper asserting the invariant set can run against both types to catch drift without sharing production code.
- **[Loss of a single cross-plugin "recovery default" knob]** → Accept. The knob was manual-merge-only and never auto-applied; no caller relied on it taking effect without writing the merge themselves. Per-plugin configuration is more explicit and matches how callers already tune brokers individually.
- **[Two new public types increase the plugin API surface]** → Net public surface is roughly flat: one Core type + one Hosting type removed, two plugin types added. Each new type lives in the assembly that owns its behavior, which is the point.
