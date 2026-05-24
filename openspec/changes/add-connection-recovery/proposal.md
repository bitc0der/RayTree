## Why

RayTree currently has no application-level recovery for broker/database connection loss. The plugins build a single long-lived client at `InitializeAsync` and never rebuild it: `RabbitMqPublisher`/`RabbitMqConsumer` keep one `IConnection`+`IChannel` for life; `KafkaPublisher`/`KafkaConsumer` keep one native handle each; `NotificationBasedPublisher` opens one `NpgsqlConnection`, runs LISTEN, and on loss only flips `_listenerHealthy = false` — it never reopens. Recovery today depends entirely on whatever the underlying client library happens to do (RabbitMQ.Client default `AutomaticRecoveryEnabled`, librdkafka internal reconnect), with no observability, no policy, and no fallback when those defaults fail or are disabled. In long-running services this manifests as silent degradation: the LISTEN fast-path quietly drops to fallback polling forever, a Kafka consumer that hits a fatal native error stays dead until pod restart, a RabbitMQ channel that closes from a `PRECONDITION_FAILED` never recovers because the connection is fine. Operators are forced to restart processes to recover — the very thing the outbox pattern was supposed to make unnecessary.

## What Changes

- Add a unified `IConnectionRecoveryStrategy` abstraction in `RayTree.Core` (exponential backoff with jitter, max attempts, max delay, cancellation-aware) and a `ConnectionRecoveryOptions` record consumed by every plugin.
- `RabbitMqPublisher` / `RabbitMqConsumer`: detect connection-or-channel shutdown via `IConnection.ConnectionShutdownAsync` and `IChannel.ChannelShutdownAsync`, then run the recovery strategy to rebuild connection + channel + topology + consumer registration. Removes reliance on the RabbitMQ.Client built-in `AutomaticRecoveryEnabled` (which we disable to take ownership) so behavior is deterministic across deployments.
- `KafkaPublisher` / `KafkaConsumer`: detect fatal/`AllBrokersDown` errors via the client `Error` handler (and `KafkaException.Error.IsFatal` on the poll thread) and rebuild the native handle through the recovery strategy. The Kafka consumer's single owner thread is preserved.
- `NotificationBasedPublisher`: on LISTEN-connection loss, reconnect the `NpgsqlConnection`, re-issue `LISTEN`, and resume the fast path. The fallback polling loop continues to cover the gap during recovery.
- Emit recovery telemetry: `raytree.connection.disconnects` (counter, tagged `component` / `endpoint`), `raytree.connection.recoveries` (counter, with `outcome=succeeded|exhausted`), `raytree.connection.recovery.duration` (histogram, seconds), `raytree.connection.state` (observable up/down gauge per component). Add to [docs/opentelemetry-metrics.md](docs/opentelemetry-metrics.md).
- Structured logs at documented levels: `Warning` on first detected disconnect, `Information` on each retry attempt with `{AttemptNumber}` / `{Delay}`, `Information` on successful recovery with `{Duration}`, `Error` on exhaustion.
- Recovery is **on by default** with sensible defaults (initial 1 s, factor 2.0, max 30 s, unlimited attempts) and can be tuned or disabled per component through fluent builder options (`UseConnectionRecovery(...)` on each plugin's options) and `appsettings.json` (`ChangeTracking:Publisher:ConnectionRecovery`, `ChangeTracking:Subscriber:ConnectionRecovery`).
- No breaking public-API removals. The plugin option classes gain a new `ConnectionRecovery` property (defaulted), and one new public type (`ConnectionRecoveryOptions`) lives in `RayTree.Core`.

## Capabilities

### New Capabilities
- `connection-recovery`: Detect connection/channel/handle loss in every broker- and database-facing plugin, run a configurable retry policy to re-establish the underlying client, and resume normal operation transparently — with metrics, logs, and per-plugin overrides.

### Modified Capabilities
- `rmq-topology-wait`: Topology probing currently runs only during `InitializeAsync`. Recovery must re-run the same passive-declare probe after a reconnect when `WaitForTopology = true`, so a reconnect during topology churn waits for the topology to reappear instead of failing.
- `kafka-topic-wait`: Same shape as above — `WaitForTopic = true` must re-probe on reconnect so a broker restart that races with topic recreation is handled.
- `opentelemetry-metrics`: Adds four new instruments (`raytree.connection.disconnects`, `raytree.connection.recoveries`, `raytree.connection.recovery.duration`, `raytree.connection.state`) and the bucket-boundary guidance for the duration histogram.
- `structured-logging`: Documents the recovery log placement (`Warning` first detection, `Information` retry attempts and recovery, `Error` exhaustion) and the rule that connection-recovery logging lives in runtime services with non-null loggers — no `NullLoggerFactory` fallbacks.

## Impact

- **Code**: `src/RayTree.Core/Resilience/` (new — `IConnectionRecoveryStrategy`, `ExponentialBackoffRecoveryStrategy`, `ConnectionRecoveryOptions`). `src/RayTree.Plugins.RabbitMQ/RabbitMqPublisher.cs`, `RabbitMqConsumer.cs` (shutdown handlers, rebuild path, disable client auto-recovery). `src/RayTree.Plugins.Kafka/KafkaPublisher.cs`, `KafkaConsumer.cs` (error handler, fatal classification, rebuild on poll thread). `src/RayTree.Plugins.PostgreSQL/Outbox/Notification/NotificationBasedPublisher.cs` (reopen + re-LISTEN in `ListenLoopAsync`). `src/RayTree.Core/Telemetry/RayTreeMeter.cs` (four new instruments + helper methods). `src/RayTree.OpenTelemetry/MeterProviderBuilderExtensions.cs` (no change — instruments share the existing meter). Builder extensions on each plugin to expose `UseConnectionRecovery(...)`.
- **Public API**: Additive only — one new type, one new property on each plugin options class, one new builder method per plugin. No removals; no signature changes.
- **Dependencies**: None added. Uses BCL `System.Diagnostics.Metrics`, existing logger abstractions, and each plugin's existing client SDK.
- **Configuration**: Two new bindable sections (`ChangeTracking:Publisher:ConnectionRecovery`, `ChangeTracking:Subscriber:ConnectionRecovery`) wired through `RayTree.Hosting.AddChangeTracking`.
- **Behavior**: Recovery is on by default. Deployments that intentionally rely on pod restarts for recovery can opt out via `ConnectionRecovery.Enabled = false`.
- **Tests**: New unit tests for the backoff strategy (deterministic via `TimeProvider`); new integration tests per plugin that pause/kill the broker container mid-flow and assert recovery, message continuity, and metric emission.
- **Docs**: Update `CLAUDE.md` plugin descriptions, `AGENTS.md` logging-placement rule (recovery logs are runtime-service logs, non-null logger required), [docs/opentelemetry-metrics.md](docs/opentelemetry-metrics.md), and the per-plugin READMEs.
