## Context

RayTree's plugins each open one long-lived client to their external system at `InitializeAsync` and never rebuild it. Today, recovery from a broker restart, network partition, or fatal native error is delegated entirely to the underlying client library — RabbitMQ.Client's `AutomaticRecoveryEnabled`, librdkafka's internal reconnect, or "the operator will restart the pod" for `NotificationBasedPublisher`. These defaults have three problems:

1. **Opaque to operators.** No metric tells anyone a recovery happened. The LISTEN fast-path can silently degrade to polling forever and nobody notices until lag piles up.
2. **Non-deterministic across deployments.** A team that explicitly disables RabbitMQ.Client auto-recovery for ops reasons (clean shutdown semantics, predictable channel lifecycles) gets a completely different reliability story than one that doesn't.
3. **Partial coverage.** `NotificationBasedPublisher` already *detects* LISTEN connection loss (`_listenerHealthy = false`) but never reconnects — recovery requires a process restart. Kafka fatal errors poison the consumer for the lifetime of the process. RabbitMQ channel-level recovery is murky once `AutomaticRecoveryEnabled` is off.

The fix is a single recovery abstraction in `RayTree.Core` that every plugin uses, with metrics + logs that match the project's existing conventions and an opt-out switch for callers who genuinely want pod-restart semantics.

**Constraints**

- No new transitive dependencies. Recovery uses BCL primitives only (`System.Threading`, `System.Diagnostics.Metrics`, `Microsoft.Extensions.Logging.Abstractions`).
- No breaking public-API changes. Plugin option classes gain one new property each, default-constructed and enabled.
- Logging follows the placement rule: recovery logs are runtime-service logs, non-null `ILogger<T>` required, no `NullLoggerFactory` inside runtime paths. `RabbitMqConsumer`'s no-logger exception stands — its recovery is silent in logs but observable in metrics.
- Metrics follow `RayTreeMeter` conventions: instrument calls are no-ops when no listener is attached; durations are in seconds (`s`); the `"RayTree"` meter name is shared.
- `NotificationBasedPublisher`'s `_listenerHealthy` field and fallback polling loop already exist and SHALL be reused — recovery layers on top, it does not replace them.

**Stakeholders**

- Application teams deploying RayTree in long-running services where pod restarts are expensive.
- Operators consuming the OTel metrics — new instruments must integrate cleanly with the existing `RayTree.OpenTelemetry.AddRayTreeMetrics()` opt-in.
- Plugin authors — the recovery abstraction must be reusable by future plugins (Azure Service Bus, NATS, Pulsar) without modifying core.

## Goals / Non-Goals

**Goals**
- Detect connection/channel/handle loss in every broker- and database-facing plugin (`RabbitMqPublisher`, `RabbitMqConsumer`, `KafkaPublisher`, `KafkaConsumer`, `NotificationBasedPublisher`) and recover transparently.
- Provide a single `IConnectionRecoveryStrategy` abstraction with sensible defaults (exponential backoff, jitter, unlimited attempts, 30 s ceiling) and per-plugin overrides.
- Surface recovery activity through four new metrics and four log levels matching the documented placement rules.
- Re-run plugin-side probes (topology-wait, topic-wait) on reconnect so a broker restart that races with topology changes Just Works.
- Keep recovery configurable from `appsettings.json` via `ChangeTracking:Publisher:ConnectionRecovery` and `ChangeTracking:Subscriber:ConnectionRecovery`.

**Non-Goals**
- Replacing the outbox-publisher retry loop or the subscriber handler retry loop. Those operate at the *message* level; recovery operates at the *connection* level. They compose, but they are different concerns.
- Replacing client-library auto-recovery for callers who want it. The plugins disable `RabbitMQ.Client.AutomaticRecoveryEnabled` so behaviour is deterministic; librdkafka's internal recovery for non-fatal errors is left untouched.
- A circuit breaker. Recovery retries indefinitely by default; teams that want a circuit-breaker shape set `MaxAttempts` and handle the resulting exception in their own host code.
- Redis (`RedisDeduplicationStore`) recovery. StackExchange.Redis already implements first-class reconnection; we lean on it. If that changes, the same abstraction can be applied later.
- Outbox (`PostgreSqlOutbox`) write-time recovery. Outbox writes use short-lived `NpgsqlConnection` instances per call already; Npgsql's connection pool handles transient TCP failures. No state is held across calls that would need rebuilding.

## Decisions

### 1. Single core abstraction, plugin-local integration

We introduce two public types in `RayTree.Core/Resilience/`:

```csharp
public interface IConnectionRecoveryStrategy
{
    Task RunAsync(Func<CancellationToken, Task> attempt, RecoveryContext context, CancellationToken ct);
}

public sealed record ConnectionRecoveryOptions
{
    public bool Enabled { get; init; } = true;
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);
    public double Factor { get; init; } = 2.0;
    public double JitterFraction { get; init; } = 0.2;
    public int? MaxAttempts { get; init; }   // null = unlimited
}

internal readonly record struct RecoveryContext(
    string Component,   // e.g. "rabbitmq.publisher"
    string Endpoint);   // e.g. "broker-1:5672"
```

`ExponentialBackoffRecoveryStrategy` is the default implementation. It owns metric emission (`raytree.connection.disconnects` on entry, `raytree.connection.recoveries` + `raytree.connection.recovery.duration` on exit, both tagged with `outcome`) and logging at the documented levels. Plugins do not duplicate this work — they just supply the attempt delegate.

**Alternative considered**: `Polly`. Rejected because (a) adding a transitive dependency to `RayTree.Core` violates the OTel-isolation principle ("apps that don't opt in get zero closure"), (b) Polly's resiliency-pipeline mental model is overkill for one retry shape, (c) we'd still need our own metric/log wrapper, so we'd ship Polly *plus* a thin facade.

**Alternative considered**: a per-plugin retry loop hand-written in each plugin. Rejected because it duplicates the backoff math five times, makes consistency across plugins a code-review concern instead of a compile-time one, and forces five test suites instead of one for the strategy.

### 2. Recovery is on by default; opt out, not opt in

`ConnectionRecoveryOptions.Enabled = true` is the default. The existing behaviour (no recovery, surface disconnect on next operation) is reachable via `Enabled = false`. Justification: long-running services almost always want recovery, the failure mode of "silently degraded for hours" is far worse than the failure mode of "retried for 5 seconds, then degraded for hours" (which a recovery log + metric makes visible), and a default-off feature that requires every plugin to opt in repeats the silent-degradation problem we are trying to fix.

The opt-out path matters for callers running in `Kubernetes` deployments that intentionally crash the pod on broker disconnect to force a clean restart from a known-good state. That is a legitimate choice — they set `Enabled = false`.

### 3. Disable client-library auto-recovery in the RabbitMQ plugin

`RabbitMqPublisher` and `RabbitMqConsumer` set `ConnectionFactory.AutomaticRecoveryEnabled = false` and `TopologyRecoveryEnabled = false`. With both libraries running their own recovery at once, the behaviour is non-deterministic: who rebuilds the channel? Which side re-declares topology? Whose retry timing wins?

Owning recovery end-to-end gives us a single timing source, a single metric stream, deterministic shutdown semantics (no rogue background reconnect while we are mid-dispose), and an integration point for the topology-wait probe that already exists.

**Risk**: Teams that *want* the RabbitMQ.Client built-in recovery (e.g. because they trust it more than ours initially) lose that option. **Mitigation**: `ConnectionRecovery.Enabled = false` falls back to the original "single connection, fails on disconnect" behaviour and lets them wrap their own recovery; for steady-state we accept this trade-off because deterministic behaviour matters more than offering two recovery implementations.

### 4. Kafka recovery fires only on fatal errors

`KafkaPublisher` and `KafkaConsumer` ask librdkafka via `SetErrorHandler` whether an error is fatal. Non-fatal errors (broker timeout, leader change, transient transport) are recovered by librdkafka internally with no help from us — wrapping those in our retry loop would cause double recovery and worse behaviour than either side alone. Only when `Error.IsFatal == true` do we tear down the handle and rebuild.

For the consumer, fatal `KafkaException` thrown from `Consume` on the dedicated poll thread is the second trigger. Recovery runs *on the poll thread* — we already have the rule "all Kafka client operations on one thread", and recovery is no exception.

### 5. PostgreSQL LISTEN reconnect reuses the fallback polling loop as a safety net

`NotificationBasedPublisher` already has the right shape: a `_listenerHealthy` flag, a fallback polling loop, and a "drain on first tick" mechanism. Recovery adds one piece: when `_listenerHealthy = false` is set, the listen loop runs `IConnectionRecoveryStrategy` to open a fresh `NpgsqlConnection`, issue `LISTEN {ChannelName}`, swap `_connection`, and flip `_listenerHealthy = true`.

The fallback polling loop continues running during recovery — it is the safety net that processes any record written between the disconnect and the LISTEN reissue. No record is lost; only the latency profile degrades.

**Alternative considered**: cancel the fallback during reconnect to avoid double-processing. Rejected — the existing `TryClaimForPublishingAsync` already prevents double-publish via row-level atomic claim. Two publishers racing on the same record results in one winner and one debug log; correctness is unaffected.

### 6. Re-run topology/topic probes on reconnect

When a plugin probes external topology at `InitializeAsync` (`WaitForTopology = true`, `WaitForTopic = true`), the same probe must rerun on reconnect. A broker restart that races with topology recreation is the dominant case where this matters: the connection comes back before the topology does, and without the reprobe we issue `BasicConsumeAsync` against a not-yet-redeclared queue and immediately disconnect again — a tight crash loop.

The reprobe uses the same options (`TopologyWaitInterval`, `TopicWaitInterval`, `TopologyWaitTimeout`, `TopicWaitTimeout`). A timeout during reconnect propagates as a recovery failure (the strategy records `outcome = "exhausted"`); the wait layer does not need to know about recovery.

### 7. Configuration shape: two top-level sections, per-plugin override

`ChangeTracking:Publisher:ConnectionRecovery` and `ChangeTracking:Subscriber:ConnectionRecovery` bind once via `IOptions<ConnectionRecoveryOptions>` and are applied as the default for any plugin whose `ConnectionRecovery` property is unchanged from the parameterless-constructor default. Explicit per-plugin overrides win.

**Alternative considered**: per-plugin sections in config (`ChangeTracking:Publisher:RabbitMQ:ConnectionRecovery`, etc.). Rejected — the dominant use case is "the whole service should retry the same way"; per-plugin override lives in code where it can be expressed concisely.

## Risks / Trade-offs

- **[Risk] Recovery retries indefinitely by default could mask a permanent failure.**
  → Mitigation: `Warning` log on first detection plus `Information` per retry attempt makes the situation visible. `raytree.connection.disconnects` and `raytree.connection.state` make it alertable. Teams that want hard failure set `MaxAttempts`.

- **[Risk] Disabling RabbitMQ.Client auto-recovery is a behaviour change for callers.**
  → Mitigation: the integration tests exercise broker-restart scenarios end-to-end, and the per-plugin README is updated. Callers who genuinely want the library's auto-recovery flip `ConnectionRecovery.Enabled = false`; the underlying `ConnectionFactory.AutomaticRecoveryEnabled` is then re-enabled via the existing options surface for that one case (added as a clearly-documented escape hatch).

- **[Risk] Kafka recovery on the poll thread blocks consumption while reconnecting.**
  → Mitigation: this is the correct behaviour — there is no point consuming when the consumer is being rebuilt. The post-handler channel for deferred-ack messages is drained and stale `ConsumeResult`s discarded; the broker handles redelivery via the standard at-least-once contract.

- **[Risk] Stale RabbitMQ delivery tags racing recovery cause spurious broker errors.**
  → Mitigation: each `MessageEnvelope` captures the `IChannel` reference at the moment of delivery (via the existing `RabbitMqEnvelopeMetadata` accessor). `AcknowledgeAsync`/`NegativeAcknowledgeAsync` compare against the current channel and silently no-op on mismatch — AMQP semantics already redeliver on channel close, so no action is the correct action.

- **[Trade-off] Recovery + outbox retry + handler retry stack three retry loops.**
  → Each layer addresses a different failure mode (connection / message / handler) and they compose cleanly. We document the ordering in `CLAUDE.md` and call it out in the integration tests so operators know which timer to tune for which symptom.

- **[Trade-off] Adding four metrics and four log lines per plugin is meaningful runtime overhead during a recovery storm.**
  → Mitigation: instrument calls are silent no-ops when no listener is attached (existing `RayTreeMeter` guarantee). Logging is guarded by `ILogger.IsEnabled` checks. Worst case is one allocation per retry attempt during recovery — acceptable for a feature whose entire purpose is making rare events visible.

## Migration Plan

1. **Ship dark.** Land `IConnectionRecoveryStrategy` and `ConnectionRecoveryOptions` in `RayTree.Core` with `Enabled = false` as the default for the first preview release. Existing callers see no behaviour change.
2. **Wire plugins.** Add the `ConnectionRecovery` property to each plugin's options class, plumb it through the constructor, integrate with shutdown handlers / error callbacks. Each plugin gets its own focused integration test (Testcontainers, broker pause/restart, assert continuity + metrics).
3. **Flip default.** In the next release after preview, change the property default to `Enabled = true`. Release notes call this out as the only behaviour change.
4. **Document.** Update `CLAUDE.md`, [docs/opentelemetry-metrics.md](docs/opentelemetry-metrics.md), the per-plugin READMEs, and the structured-logging spec entries.

**Rollback**: callers can disable recovery globally via `appsettings.json` (`"ChangeTracking:Publisher:ConnectionRecovery": { "Enabled": false }` and the subscriber counterpart) without redeploying code. Per-plugin rollback is one fluent-builder line. The new metrics and log entries cause no harm when no listener is attached; rollback does not require removing them.

## Open Questions

- Should we expose a public `IConnectionRecoveryStrategy` extension point so callers can plug in their own strategy (Polly-backed, jittered-Fibonacci, fixed delays, etc.)? Current plan: yes — the interface is public, and `ChangeTrackingBuilder.UseConnectionRecoveryStrategy(IConnectionRecoveryStrategy)` allows replacement at the tracker level. Confirm during code review.
- Should the `raytree.connection.state` gauge be per `(component, endpoint)` pair or per `component` only? Current plan: per `(component, endpoint)` so multi-broker deployments are observable. Confirm with operator review.
- Should `NotificationBasedPublisher` recovery participate in the same options surface, or have its own? Current plan: same surface (`NotificationBasedPublisherOptions.ConnectionRecovery`), bound from `ChangeTracking:Publisher:ConnectionRecovery` like every other publisher.
- Should we add a `raytree.connection.last_reconnect_age` observable gauge (seconds since last successful reconnect, per component)? Useful for "is anything currently flapping" dashboards. Deferring until we have operator feedback on the four core instruments.
