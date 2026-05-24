## Context

RayTree's plugins each open one long-lived client to their external system at `InitializeAsync`. The question this change answers is: when that client breaks, what happens? Today the answer varies:

- **RabbitMQ.Client** has `AutomaticRecoveryEnabled = true` by default — it rebuilds connection, channel, topology, consumers transparently. ✅
- **Confluent.Kafka** (`librdkafka`) recovers from non-fatal errors internally. For *fatal* errors (`Error.IsFatal = true`) the native handle is dead and stays dead. ❌
- **`NotificationBasedPublisher`** sets `_listenerHealthy = false` on LISTEN loss but never reopens the connection — the fast path silently degrades to fallback polling for the life of the process. ❌
- **Npgsql connection pool** handles transient TCP failures on short-lived outbox writes. ✅
- **`StackExchange.Redis.ConnectionMultiplexer`** reconnects with full subscription state. ✅

Three out of five are already correct. Two have real bugs. The earlier draft of this design proposed a unified lifecycle abstraction (`IExternalResource<TClient>` + `ManagedResource<TClient>` + per-plugin adapters + readiness gating + `ReportFailure`) that would have covered all five uniformly. That was rejected — abstracting over three call-sites that need zero code adds ceremony with no payoff.

**Constraints**

- No new dependencies. BCL primitives only.
- No breaking public-API changes. Option records gain one new property each; no signatures change.
- Logging follows the placement rule: recovery logs are runtime-service logs, non-null `ILogger<T>`. `RabbitMqConsumer`'s no-logger exception stands.
- Metrics follow `RayTreeMeter` conventions: no-op when no listener is attached; durations in seconds.
- `NotificationBasedPublisher`'s existing `_listenerHealthy` flag and fallback polling loop are reused — recovery layers on top, it does not replace them.

**Stakeholders**

- Application teams running RayTree in long-running services where the Postgres LISTEN bug or a Kafka fatal-error poison-pill manifests as silent degradation.
- Operators consuming OTel metrics — they want one uniform surface (`raytree.connection.*`) across every connection-bearing plugin regardless of who owns the actual recovery code.

## Goals / Non-Goals

**Goals**
- Fix the Postgres LISTEN reconnect bug.
- Fix the Kafka fatal-error gap (publisher + consumer).
- Emit four metric instruments uniformly across all five connection-bearing plugins so operators see one observable surface.
- Add documented log entries at consistent levels.
- Keep recovery configurable via `appsettings.json` (`ChangeTracking:Publisher:ConnectionRecovery`, `ChangeTracking:Subscriber:ConnectionRecovery`) for the two plugins that own retry policy (Postgres, Kafka).
- Make the outbox-publisher background loop's existing implicit retry (loop, sleep, try again) observable as a connection event when the failure is a Postgres connection fault — without adding any new retry code at that layer.

**Non-Goals**
- No unified lifecycle abstraction (`IExternalResource`, `ManagedResource`, adapters). See **Decision 1** below.
- No replacement of RabbitMQ.Client's `AutomaticRecoveryEnabled`. The library does the right thing.
- No `RedisDeduplicationStore` instrumentation. `ConnectionMultiplexer` recovers transparently and emits its own events for consumers who want them.
- No circuit breaker. Recovery retries indefinitely by default; teams that want a circuit-breaker shape set `MaxAttempts`.
- **No write-path retry inside the library.** `PostgreSqlOutbox.WriteAsync` (called from `TrackInsertAsync`/EF interceptor inside the caller's transaction) and `PostgreSqlRepository` writes SHALL continue to throw to the caller on connection fault. Auto-retry at this layer would break atomicity — the outbox write is inside the caller's transaction, which encloses business-table writes the library cannot see; retrying just the outbox call would leave the transaction in an inconsistent state. The caller (or their EF Core transaction-retry strategy) owns this contract. See **Decision 8** below.

## Decisions

### 1. No unified lifecycle abstraction

**Decision**: drop the `IExternalResource<TClient>` / `ManagedResource<TClient>` design. Each plugin handles its own recovery (or doesn't, when the SDK already does).

**Why**:
- Five plugins; three need zero code (RabbitMQ, Redis, Npgsql pool); two need ~25 lines each (Postgres LISTEN, Kafka rebuild). Abstracting over two call-sites is ceremony, not reuse — the project's `AGENTS.md` says "three similar lines is better than a premature abstraction."
- The two call-sites that need code aren't actually similar enough to share much: Postgres reconnect is a `while` loop around `WaitAsync`; Kafka publisher rebuild is "dispose the producer, let the existing lazy-build path rebuild"; Kafka consumer rebuild is "catch on the poll thread, rebuild on that same thread, redrain the post-handler channel." Different shapes, different concerns.
- The earlier draft's `ManagedResource<TClient>` added: a state-management gate, a readiness `TaskCompletionSource`, an `EnsureReadyAsync` contract every operation has to remember to call, an `IExternalResourceAdapter<TClient>` interface with four methods per plugin, a `ReportFailure` second entry point, a classifier per plugin. That's significant new public surface to learn for a feature whose actual implementation is ~50 lines of recovery code.

**Trade-off**: future plugins (NATS, Pulsar, Azure Service Bus) will each write their own recovery code if they need it. We accept this — the alternative is a load-bearing abstraction designed for hypothetical future plugins, which `AGENTS.md` also explicitly warns against ("Don't design for hypothetical future requirements").

### 2. One internal helper for the backoff loop

**Decision**: extract one `internal static class ConnectionRetry` in `RayTree.Core/Resilience/` with a single `RunAsync(Func<CancellationToken, Task> attempt, ConnectionRecoveryOptions options, string component, string endpoint, RayTreeMeter meter, ILogger logger, CancellationToken ct)` method. Used by Postgres and Kafka — two call-sites — but the same backoff math, the same metric/log emission, the same cancellation semantics. Two copies of the loop is the borderline; one helper is fine.

**Why**: this is the line between "useful helper" and "abstraction." A static method taking everything it needs as parameters has no surface area to learn — it's plumbing, not architecture. `RayTreeMeter` already follows the same shape (it's a class that owns instruments and exposes static-ish helpers via instance methods).

**Alternative considered**: inline the loop twice. Rejected — the metric emission has six measurement points (disconnect counter, success counter+duration, exhaustion counter+duration, state gauge updates) that would otherwise drift between the two copies.

### 3. RabbitMQ: observe, don't own

**Decision**: keep `AutomaticRecoveryEnabled = true` and `TopologyRecoveryEnabled = true`. Subscribe to `ConnectionShutdownAsync`, `RecoverySucceededAsync`, and `ConnectionRecoveryErrorAsync` events to emit RayTree's metrics and logs.

**Why**:
- The library's recovery is battle-tested across millions of deployments. We have no plausible improvement to make.
- The earlier draft proposed disabling auto-recovery and owning the rebuild ourselves so RayTree could "be deterministic about timing." That's a real property but a small one — the library's recovery is already deterministic enough; the metrics + logs we wanted are achievable purely through the existing event hooks.
- Disabling auto-recovery would also force us to re-implement topology re-declaration, consumer re-registration, channel rebuild ordering — all of which the library does well today.

**Trade-off**: the recovery's *timing* is controlled by `ConnectionFactory.NetworkRecoveryInterval` (library default 5s), not by RayTree's `ConnectionRecoveryOptions`. Callers who need to tune RabbitMQ-side timing construct a `ConnectionFactory` explicitly. This is documented but not exposed through `RabbitMqPublisherOptions` — adding two more knobs for one library default is not worth the surface-area cost.

### 4. Kafka: rebuild only on fatal errors

**Decision**: subscribe to `IProducerBuilder.SetErrorHandler` and `IConsumerBuilder.SetErrorHandler`; act only when `e.Error.IsFatal == true`. For the consumer, also catch fatal `KafkaException` thrown from `Consume` on the poll thread.

**Why**: librdkafka classifies errors and recovers non-fatal ones internally. Wrapping non-fatal errors in our own retry would double-recover and produce worse behaviour than either side alone. The fatal-error path is exactly where librdkafka stops trying and the handle stays dead — that's our gap to fill.

**Implementation detail**: the publisher rebuild is trivial — set the cached `_producer` field to `null` in the error handler, and the existing `GetProducerAsync` lazy-build path (which already runs the topic-wait probe when enabled) handles the rest on the next `PublishAsync`. The consumer rebuild must run on the dedicated poll thread because `Confluent.Kafka` requires it; we do the dispose + rebuild loop inline inside the existing `Task.Run` thread.

### 5. Postgres LISTEN: reconnect inline, fallback polling unchanged

**Decision**: when `ListenLoopAsync` catches a connection-fault exception, run an inline `while` loop that disposes the broken connection, opens a fresh one, re-attaches the `Notification` handler, issues `LISTEN`, and resumes — bounded by `ConnectionRecoveryOptions`. The fallback polling loop is left alone.

**Why**:
- The bug today is purely "no reconnect code exists." Adding 25 lines fixes it.
- `_listenerHealthy = false` is already the correct degraded-state signal; the fallback polling loop already runs while it's set; `TryClaimForPublishingAsync` already prevents double-publish races with the fallback. The infrastructure is in place — we just need to flip `_listenerHealthy` back to `true` after the reconnect succeeds.

### 6. Classifier is a per-plugin static method

**Decision**: each plugin that owns retry code (Postgres, Kafka) ships a `private static bool IsConnectionFault(Exception)` switch expression. No interface, no shared classifier type.

**Why**: the classifiers are short (Postgres's is 6 lines; Kafka's is one `Error.IsFatal` check), and the exception types are plugin-specific (`NpgsqlException`/`PostgresException` vs `KafkaException`). An `IExceptionClassifier` interface would have one implementation per plugin and zero shared logic. Compile-time visibility is the only thing we'd gain, and the call sites already make the relationship obvious.

### 7. Observe outbox connection faults via three default-implemented `IOutbox` members

**Decision**: add three optional members to `IOutbox` — `bool IsConnectionFault(Exception)` (default `false`), `string? ConnectionComponent` (default `null`), `string? ConnectionEndpoint` (default `null`). `OutboxPublisherService.ProcessBatchAsync` and `NotificationBasedPublisher.FallbackPollingLoopAsync` consult them in their existing batch-error catch blocks: on classification-true + non-null component, emit the disconnect metric, demote `Error → Warning`, track a per-loop `_unhealthy` flag. On first subsequent success, emit recovery metric + duration + `Information` log.

**Why this shape**:
- `OutboxPublisherService` is generic over `IOutbox` — it cannot hard-code `NpgsqlException` or component names. The contract belongs on the interface.
- Default implementations on the interface (C# 8+, which we already use) mean `InMemoryOutbox` and any third-party `IOutbox` need zero changes. No breaking API.
- No new retry loop is added — the existing polling cadence is the retry. We just *observe* what was already happening.
- `_outboxUnhealthy` is per-service, not per-outbox-plus-component. Multiple entity types sharing the same Postgres endpoint flap together; one disconnect event per service-instance per transition is the right cardinality.
- `NotificationBasedPublisher`'s fallback polling iterates multiple outboxes (one per entity type) — there we key the state dictionary by entity type so each outbox's transitions are tracked independently. Slightly more bookkeeping, but the fallback loop semantics demand it.

**Alternative considered**: have `PostgreSqlOutbox` wrap each of its own methods to emit metrics internally, never touching `OutboxPublisherService`. Rejected — it duplicates state tracking (each entity type's outbox would track its own unhealthy flag), and `OutboxPublisherService` would still need to demote logs separately. Centralising at the consumer is one piece of bookkeeping, not N.

**Alternative considered**: add a separate `IConnectionFaultClassifier` interface that `PostgreSqlOutbox` implements as a second interface, with `OutboxPublisherService` checking via `is`-pattern. Rejected — the indirection adds a type for no readability gain; default-implemented members on `IOutbox` make the optionality obvious at the consumer site.

### 8. Write paths throw, read paths observe

**Decision**: the asymmetric treatment of read-side vs write-side Postgres faults is deliberate.

- **Write side** (`PostgreSqlOutbox.WriteAsync`, `PostgreSqlRepository.InsertAsync/UpdateAsync/DeleteAsync`): exceptions propagate to the caller. No retry, no metrics, no log demotion. These calls execute inside the caller's transaction (most often via `EntityChangeInterceptor` inside `SaveChangesAsync`), and the caller owns atomicity. The right place for retry is the caller's unit-of-work / DbContext / business transaction — not the library.
- **Read side** (`OutboxPublisherService.ProcessBatchAsync`, `NotificationBasedPublisher.FallbackPollingLoopAsync`): exceptions are caught, the loop sleeps + retries on the next tick (existing behaviour). We add metrics and log demotion *on top of* the existing retry — not new retry code.

This asymmetry should be explicit in `CLAUDE.md` so future contributors don't try to "complete" the design by wrapping the write paths.

### 9. RabbitMQ options do not expose `ConnectionRecovery`

**Decision**: only `NotificationBasedPublisherOptions`, `KafkaPublisherOptions`, and `KafkaConsumerOptions` gain a `ConnectionRecovery` property. RabbitMQ options do not.

**Why**: there's no retry loop on the RabbitMQ side that `ConnectionRecoveryOptions` would configure — the library owns timing. Exposing a property that does nothing is a footgun. Callers who want to tune RabbitMQ recovery use the SDK's `ConnectionFactory` knobs (`NetworkRecoveryInterval`, etc.).

## Risks / Trade-offs

- **[Risk] The Kafka rebuild loop on the poll thread blocks consumption.**
  → Mitigation: this is the correct behaviour — there is no point consuming when the consumer is dead. Pending deferred-ack messages are dropped (their `ConsumeResult` references the disposed consumer); the broker redelivers on the new consumer's join via standard at-least-once semantics.

- **[Risk] Postgres reconnect could race the fallback polling loop and double-process a record.**
  → Mitigation: the existing `IOutbox.TryClaimForPublishingAsync` is already the contract that prevents this. Two callers racing on the same record → one wins, one returns `false` and logs `Debug`. No new code needed.

- **[Risk] RabbitMQ.Client's `RecoverySucceededAsync` event firing semantics may differ from what we expect.**
  → Mitigation: integration test that restarts the broker container mid-publish and asserts both `disconnects` and `recoveries{outcome="succeeded"}` are recorded. If the event semantics surprise us, we adjust the hook before merging.

- **[Trade-off] No `ConnectionRecoveryOptions` for RabbitMQ means callers cannot tune RabbitMQ timing through RayTree's configuration surface.**
  → Acceptable — they tune `ConnectionFactory.NetworkRecoveryInterval` through the existing `RabbitMqPublisherOptions.ConnectionFactoryConfigurator` (if not exposed today, we add it as a separate small change). Mixing RayTree-owned options with SDK-owned options under one property name would be more confusing than helpful.

- **[Trade-off] Three retry loops stack: connection recovery (Postgres/Kafka), outbox publisher retry (`MaxRetryCount`), subscriber handler retry (`MaxRetries`).**
  → Each addresses a different failure mode (connection / message / handler). They compose cleanly; documentation in `CLAUDE.md` will call out the ordering. Operators tune one or the other based on the symptom.

## Migration Plan

1. **Land the core record + helper.** `ConnectionRecoveryOptions` + `ConnectionRetry` static class + four new `RayTreeMeter` instruments. No behaviour change yet.
2. **Patch Postgres.** Add `ReconnectAsync` to `NotificationBasedPublisher`. Add `IsConnectionFault` classifier. Integration test against a Testcontainers Postgres that we restart mid-stream.
3. **Patch Kafka.** Add error-handler-disposes-producer to `KafkaPublisher`. Add poll-thread rebuild to `KafkaConsumer`. Integration tests against a Testcontainers Kafka with simulated fatal error.
4. **Hook RabbitMQ events.** Subscribe to the three event types in publisher + consumer; emit metrics + logs. No recovery code. Integration test asserts metric emission on broker restart (recovery itself is performed by the library).
5. **Wire configuration.** Bind `ChangeTracking:Publisher:ConnectionRecovery` and `ChangeTracking:Subscriber:ConnectionRecovery` in `RayTree.Hosting`.
6. **Document.** Update `CLAUDE.md`, [docs/opentelemetry-metrics.md](docs/opentelemetry-metrics.md), per-plugin READMEs.

**Rollback**: per-plugin `ConnectionRecovery.Enabled = false` in `appsettings.json` disables the new recovery code (Postgres LISTEN reverts to "set `_listenerHealthy = false` and never reconnect" pre-change behaviour; Kafka reverts to "fatal error kills the handle"). RabbitMQ event hooks have no behaviour rollback because they emit metrics only — no harm if observers ignore them.

## Open Questions

- Should `raytree.connection.state` be per `(component, endpoint)` or per `component` only? Current plan: per `(component, endpoint)` so multi-broker deployments are observable. Confirm with operator review.
- Do we want `ConnectionRecoveryOptions.OnExhausted` callback (or an event) so hosts can react to "we gave up"? Deferring — observability via metrics + logs is enough for v1; callbacks add surface area for an edge case.
- Should `ConnectionRetry.RunAsync` accept a `TimeProvider` for testability, or is the `[NonParallelizable]` integration-test path sufficient? Current plan: yes, `TimeProvider` parameter (defaults to `TimeProvider.System`) so unit tests can use `FakeTimeProvider` deterministically.
