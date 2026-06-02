## Why

`RayTree.Core` owns a shared `ConnectionRecoveryOptions` record consumed by the Postgres and Kafka plugins. This is the **last remaining piece of config coupling** between Core and the plugins that own a reconnect loop — and it sits awkwardly next to a design decision that already went the other way: the retry *loop* is deliberately **not** shared (each plugin hand-rolls ~20 lines so plugins consume Core via its public API only). The shared options record contradicts that stance: it makes every recovery-capable plugin depend on the exact shape of a Core type, even though the "universal" record is already not universal — RabbitMQ ignores it entirely, and fields like `Factor`/`MaxAttempts` are meaningless to any plugin that doesn't hand-roll an exponential loop. Moving the options next to the loop that consumes them makes the coupling honest and lets each plugin evolve its recovery surface independently.

## What Changes

### Per-place recovery options

- **BREAKING** — Remove `ConnectionRecoveryOptions` from `RayTree.Core` (`src/RayTree.Core/Resilience/`).
- Each plugin that owns a reconnect loop defines its **own** recovery options type in its own assembly, carrying only the fields that loop actually uses:
  - `RayTree.Plugins.PostgreSQL` → `PostgresConnectionRecoveryOptions` (consumed by `NotificationBasedPublisher`'s LISTEN reconnect).
  - `RayTree.Plugins.Kafka` → `KafkaConnectionRecoveryOptions` (consumed by both `KafkaPublisher` and `KafkaConsumer` rebuild loops).
- Repoint the `ConnectionRecovery` property on `NotificationBasedPublisherOptions`, `KafkaPublisherOptions`, and `KafkaConsumerOptions` from the Core type to the plugin-local type. Field names, defaults, and validation semantics are preserved so this is a type-identity break, not a behavioral one.
- **BREAKING** — `RayTree.Hosting` drops the generic shared-type binding: `ChangeTrackingRecoveryKeys` and the two `services.Configure<ConnectionRecoveryOptions>(...)` calls are removed. The host no longer binds a single cross-plugin recovery default; callers configure recovery per plugin in code (or bind their own sections to the plugin-local type). This removes `IServiceProvider` plumbing that was never wired into plugin construction anyway — the bound defaults required callers to merge them manually, so the convenience was largely notional.

### Remove connection-recovery metrics

- **BREAKING** — Remove all four connection-recovery metric instruments from `RayTreeMeter` and the three public facade methods that emit them: `raytree.connection.disconnects`, `raytree.connection.recoveries`, `raytree.connection.recovery.duration`, `raytree.connection.state` (observable gauge), and `RecordConnectionDisconnect` / `RecordConnectionRecovery` / `RegisterConnectionStateGauge` (plus the internal `_connectionStateSources` registry and `ConnectionStateSubscription`). The other `RayTreeMeter` instruments (outbox, subscriber, etc.) are untouched.
- Remove every metric-emission call site across the plugins: `OutboxPublisherService`, `NotificationBasedPublisher`, `KafkaPublisher`, `KafkaConsumer`, `RabbitMqPublisher`, `RabbitMqConsumer`.
- **`RabbitMqConsumer`** subscribed to the SDK recovery events *only* to emit metrics (it has no logger); those event subscriptions and its `RayTreeMeter? meter` constructor parameter are removed entirely.
- **`RabbitMqPublisher`** keeps its `ConnectionShutdownAsync` / `RecoverySucceededAsync` / `ConnectionRecoveryErrorAsync` handlers because they also emit `Warning`/`Information` recovery **logs** — only the `_meter` field, its constructor `RayTreeMeter?` parameter, the state-gauge registration, and the `RecordConnection*` calls are removed. The duration tracking stays (still used in the log message).
- **Behavior and logs are otherwise unchanged.** All reconnect loops (Postgres LISTEN, Kafka rebuild), the `IOutbox` connection-fault classification (`IsConnectionFault` / `ConnectionComponent` / `ConnectionEndpoint`), the `Error→Warning` outbox log demotion, and every recovery log entry are retained. This change removes observability-via-metrics only.
- RabbitMQ still exposes no recovery options (SDK-owned).

## Capabilities

### New Capabilities
<!-- none — recovery behavior is unchanged; only the ownership/location of the config type moves -->

### Modified Capabilities
- `connection-recovery`: (a) the options-shape, options-exposure, and config-binding requirements change — the validated backoff record moves out of Core to per-plugin types and the Hosting binding is removed; (b) the requirements that emit connection metrics change — `NotificationBasedPublisher` reconnect, Kafka publisher/consumer rebuild, "RabbitMQ recovery is observed", "Outbox connection-fault observability", and "Recovery logs are emitted at documented levels" drop all metric clauses while retaining their reconnect/log behavior.
- `opentelemetry-metrics`: the "Connection-recovery instruments are emitted" requirement is removed — those four instruments no longer exist.

## Impact

- **Public API (breaking):**
  - Removed: `RayTree.Core.Resilience.ConnectionRecoveryOptions`, `RayTree.Hosting.ChangeTrackingRecoveryKeys`, and the `ChangeTracking:*:ConnectionRecovery` configuration binding.
  - Removed: `RayTreeMeter.RecordConnectionDisconnect`, `RecordConnectionRecovery`, `RegisterConnectionStateGauge`, and the four `raytree.connection.*` instruments.
  - Removed: the `RayTreeMeter?` constructor parameter on `RabbitMqConsumer` and `RabbitMqPublisher` (and the corresponding meter forwarding in the RabbitMQ builder/subscriber extensions).
  - Added: `PostgresConnectionRecoveryOptions` (PostgreSQL plugin), `KafkaConnectionRecoveryOptions` (Kafka plugin).
  - Changed property types: `ConnectionRecovery` on the three plugin options classes now returns the plugin-local type.
- **Affected packages:** `RayTree.Core` (type + instrument removal), `RayTree.Plugins.PostgreSQL` (new type, drop metric calls), `RayTree.Plugins.Kafka` (new type, drop metric calls), `RayTree.Plugins.RabbitMQ` (drop meter param + consumer event handlers, keep publisher logs), `RayTree.Hosting` (binding removal). No new dependencies.
- **Observability change:** dashboards/alerts built on any `raytree.connection.*` series break — those series cease to exist. Disconnect/recovery visibility is now log-only (Postgres/Kafka retry + recovery logs; RabbitMQ publisher Warning/Information; RabbitMQ consumer becomes silent, as before for logs). The `IOutbox` connection-fault members and the `Error→Warning` log demotion remain.
- **Callers** referencing `ConnectionRecoveryOptions`, the host-bound config sections, or the removed meter facade methods must update. Type migration is mechanical; metric consumers must drop the series.
- **Tests:** delete `RecoveryMetricsTests` and `RabbitMqRecoveryMetricsTests`; strip metric assertions from `KafkaRecoveryMetricsTests`, `OutboxPublisherServiceConnectionFaultTests`, and `NotificationBasedPublisherRecoveryTests` (keep their log/behavior assertions); split `ConnectionRecoveryOptionsTests` into the plugin test projects; delete `ConnectionRecoveryConfigurationTests`. CHANGELOG, CLAUDE.md, AGENTS.md, `docs/opentelemetry-metrics.md`, and plugin READMEs updated.
