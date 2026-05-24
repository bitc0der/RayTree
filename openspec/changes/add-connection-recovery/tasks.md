## 1. Core record + helper + metrics

- [x] 1.1 Add `src/RayTree.Core/Resilience/ConnectionRecoveryOptions.cs` — public record with `Enabled` (default `true`), `InitialDelay` (`1s`), `MaxDelay` (`30s`), `Factor` (`2.0`), `JitterFraction` (`0.2`), `MaxAttempts` (`null`); constructor-side validation throwing `ArgumentOutOfRangeException` for invalid values
- [x] 1.2 ~~Add `src/RayTree.Core/Resilience/ConnectionRetry.cs`~~ **DROPPED.** Per architectural decision: Core does not own the retry loop; each plugin implements its own backoff inline using only public Core APIs. Two plugins need a retry loop (Postgres LISTEN, Kafka rebuild) — two short copies, no shared internal helper, no IVT exposure to plugin assemblies.
- [x] 1.3 Add four new instruments to `src/RayTree.Core/Telemetry/RayTreeMeter.cs`: `raytree.connection.disconnects` (Counter<long>), `raytree.connection.recoveries` (Counter<long>), `raytree.connection.recovery.duration` (Histogram<double>, unit `"s"`), `raytree.connection.state` (ObservableGauge<int>); add **public** facade methods `RecordConnectionDisconnect(component, endpoint)`, `RecordConnectionRecovery(component, endpoint, outcome, durationSeconds)`, `RegisterConnectionStateGauge(component, endpoint, Func<int> getState) → IDisposable` (matching the existing `RecordPublishSuccess` / `RecordPayloadSize` public-facade pattern; instruments stay internal, emission methods are public so plugins consume Core via public API only)
- [x] 1.4 ~~Add `InternalsVisibleTo` entries~~ **DROPPED.** Per architectural principle: plugin assemblies consume Core via its public API only. No new IVT entries are added. The pre-existing `RayTree.Plugins.PostgreSQL` IVT (for `EntityChangeTracker.Publisher` access by `NotificationBasedPublisher`) stands as a separate, pre-existing concern outside this change's scope.

## 2. Tests for core record + helper

- [x] 2.1 `tests/RayTree.Core.Tests/Resilience/ConnectionRecoveryOptionsTests.cs` — defaults match spec; validation throws for invalid `Factor` / `InitialDelay` / `MaxDelay` (must be ≥ `InitialDelay`) / `JitterFraction` / `MaxAttempts`
- [ ] 2.2 ~~Tests for `ConnectionRetry`~~ **DROPPED with 1.2.** The retry loop now lives per-plugin and is tested via that plugin's integration tests (sections 4 and 7) plus any plugin-internal unit tests the plugin author chooses to add.
- [x] 2.3 `tests/RayTree.Core.Tests/Resilience/RecoveryMetricsTests.cs` — inline `CapturingExporter` (existing OTel test pattern); asserts the four instruments are emitted with correct names, units, and tag sets

## 3. Postgres LISTEN reconnect

- [x] 3.1 Add `ConnectionRecovery` property (default `new ConnectionRecoveryOptions()`) to `src/RayTree.Plugins.PostgreSQL/Outbox/Notification/NotificationBasedPublisherOptions.cs`
- [x] 3.2 In `NotificationBasedPublisher`, add `private static bool IsConnectionFault(Exception ex)` — delegates to the shared `PostgresFault.IsConnectionFault` helper (see task 4a.2).
- [x] 3.3 Add `ReconnectAsync(CancellationToken ct)` method to `NotificationBasedPublisher` containing an inline exponential-backoff loop bounded by `_options.ConnectionRecovery`. The loop: disposes the broken connection, opens a fresh `NpgsqlConnection`, re-attaches the `Notification` event handler, issues `LISTEN {ChannelName}`, swaps `_connection`. Emits `RayTreeMeter.RecordConnectionRecovery` with `outcome="succeeded"` or `"exhausted"`. The loop lives in this plugin — no Core helper is involved.
- [x] 3.4 Modify `ListenLoopAsync` catch block: catch `Exception ex when (IsConnectionFault(ex))` → flip `_listenerHealthy = false`, call `ReconnectAsync`, then continue the loop (which flips `_listenerHealthy = true` on the next successful `WaitAsync`); existing non-connection-fault behaviour unchanged
- [x] 3.5 Register the `raytree.connection.state` gauge for `("postgres.notification", _options.ChannelName)` in the constructor

## 4. Tests for Postgres reconnect

- [ ] 4.1 `tests/RayTree.Plugins.PostgreSQL.Tests/NotificationBasedPublisherRecoveryTests.cs` ([NonParallelizable], Testcontainers) — restart the Postgres container mid-stream; assert: a notification published before the restart and one published after both arrive at the consumer; fallback polling delivers any record written during the gap; `raytree.connection.disconnects{component="postgres.notification"}` ≥ 1 and `raytree.connection.recoveries{outcome="succeeded"}` ≥ 1
- [ ] 4.2 Negative test: kill the Postgres container permanently with `MaxAttempts = 2`; assert `raytree.connection.recoveries{outcome="exhausted"}` is recorded and `ListenLoopAsync` exits cleanly
- [x] 4.3 Unit test for `IsConnectionFault` classifier — implemented as `PostgresFaultTests` (see 4b.4); shared with the outbox-observability tests since both paths delegate to the same `PostgresFault` static class.

## 4a. Outbox connection-fault observability (no retry code)

- [ ] 4a.1 Add three default-implemented members to `src/RayTree.Core/Plugins/Outbox/IOutbox.cs`: `bool IsConnectionFault(Exception ex) => false;`, `string? ConnectionComponent => null;`, `string? ConnectionEndpoint => null;`
- [ ] 4a.2 Extract the Postgres connection-fault classifier into an internal `static class PostgresFault` (e.g. `src/RayTree.Plugins.PostgreSQL/Internal/PostgresFault.cs`) with one method: `public static bool IsConnectionFault(Exception ex)` matching the spec's SqlState/exception list; have both `NotificationBasedPublisher.IsConnectionFault` and `PostgreSqlOutbox.IsConnectionFault` delegate to it
- [ ] 4a.3 In `PostgreSqlOutbox<TEntity>`, override `IsConnectionFault` (delegate to `PostgresFault`), `ConnectionComponent` (`"postgres.outbox"`), and `ConnectionEndpoint` (parse `Host:Port` once from `_options.ConnectionString` in the constructor and cache)
- [ ] 4a.4 In `src/RayTree.Core/Tracking/OutboxPublisherService.cs`, add private fields `_outboxUnhealthy` (bool) and `_firstUnhealthyAt` (DateTime). Update the existing batch-error catch block: if `_outbox.IsConnectionFault(ex) && _outbox.ConnectionComponent is not null`, log `Warning` (not `Error`), emit `RayTreeMeter.RecordConnectionDisconnect` once per transition, set `_outboxUnhealthy = true` and `_firstUnhealthyAt = DateTime.UtcNow`. On the next successful batch when `_outboxUnhealthy = true`, emit `RecordConnectionRecovery(outcome: "succeeded", duration: now - _firstUnhealthyAt)`, log `Information`, clear the flag
- [ ] 4a.5 In `src/RayTree.Plugins.PostgreSQL/Outbox/Notification/NotificationBasedPublisher.cs`, in `FallbackPollingLoopAsync`'s existing catch, apply the same pattern keyed on the failing outbox (per entity type) via a `ConcurrentDictionary<Type, (bool unhealthy, DateTime firstFailureAt)>`; emit metrics + Warning log with the outbox's `ConnectionComponent`/`ConnectionEndpoint`
- [ ] 4a.6 Verify `InMemoryOutbox` and any other existing `IOutbox` implementations compile unchanged (inherit no-op defaults)

## 4b. Tests for outbox observability

- [ ] 4b.1 `tests/RayTree.Core.Tests/OutboxPublisherServiceTests` — unit test with a mocked `IOutbox` that throws a synthetic exception, `IsConnectionFault = true`, `ConnectionComponent = "postgres.outbox"`; assert disconnect metric is emitted once across multiple failed batches, recovery metric is emitted on first success, log levels are correct (`Warning` then `Information`, not `Error`)
- [ ] 4b.2 Same test with `IsConnectionFault = false` — assert `Error` log path is unchanged and no connection metric is emitted
- [ ] 4b.3 Same test with `ConnectionComponent = null` — assert `Error` path is preserved (no metric) even when `IsConnectionFault = true`
- [x] 4b.4 `tests/RayTree.Plugins.PostgreSQL.Tests/PostgresFaultTests.cs` — unit test the static classifier against each documented SqlState and exception type
- [ ] 4b.5 Add a scenario to `NotificationBasedPublisherRecoveryTests` (integration) covering the fallback polling path — restart Postgres while NOTIFY is healthy but force an outbox-read path to fail (e.g. via a brief partition); assert `disconnects{component="postgres.outbox"}` is emitted independently of the `postgres.notification` metric

## 5. Kafka publisher rebuild

- [ ] 5.1 Add `ConnectionRecovery` property (default `new ConnectionRecoveryOptions()`) to `src/RayTree.Plugins.Kafka/KafkaPublisherOptions.cs`
- [ ] 5.2 In `KafkaPublisher.BuildProducer` (the internal helper called from `GetProducerAsync`), call `.SetErrorHandler((_, e) => { if (e.IsFatal) DisposeFatalProducer(e); })`; `DisposeFatalProducer` disposes the cached `_producer`, sets it to `null` under the existing `_buildSemaphore`, emits `raytree.connection.disconnects{component="kafka.publisher", endpoint=BootstrapServers}`, and logs `Warning`
- [ ] 5.3 In `KafkaPublisher`, add an inline exponential-backoff loop (in the existing `GetProducerAsync` lazy build path when triggered after a fatal-error dispose) bounded by `_options.ConnectionRecovery`; on success record `raytree.connection.recoveries{outcome="succeeded"}`. The loop lives in this plugin — no Core helper is involved.
- [ ] 5.4 Confirm the existing `WaitForTopic` probe is re-run inside the rebuilt path (it already is — `GetProducerAsync` calls it) and add a covering test

## 6. Kafka consumer rebuild on poll thread

- [ ] 6.1 Add `ConnectionRecovery` property (default `new ConnectionRecoveryOptions()`) to `src/RayTree.Plugins.Kafka/KafkaConsumerOptions.cs`
- [ ] 6.2 In `KafkaConsumer`'s poll thread loop, wrap `Consume` in `try`/`catch (KafkaException ex) when (ex.Error.IsFatal)`; on catch: dispose the current `IConsumer`, emit `raytree.connection.disconnects`, run an inline exponential-backoff loop on the same poll thread bounded by `_options.ConnectionRecovery` (each attempt builds a new `IConsumer` via the existing init helper which re-runs the topic-wait probe, calls `Subscribe`); on success update internal reference, emit `recoveries{outcome="succeeded"}`, resume polling. The loop lives in this plugin — no Core helper is involved.
- [ ] 6.3 Drain the post-handler channel: any pending `Commit`/`SeekBack` actions whose `ConsumeResult` was issued by the disposed consumer SHALL be discarded without throwing (compare the `ConsumeResult.Topic`'s associated consumer reference at action time)

## 7. Tests for Kafka recovery

- [ ] 7.1 `tests/RayTree.Plugins.Kafka.Tests/KafkaPublisherRecoveryTests.cs` ([NonParallelizable], Testcontainers) — simulate a fatal error (test hook on the error handler, or restart the broker container with the publisher mid-flow); assert the producer is rebuilt and the next `PublishAsync` succeeds; assert metrics
- [ ] 7.2 `tests/RayTree.Plugins.Kafka.Tests/KafkaConsumerRecoveryTests.cs` — same shape on the consumer side; assert deferred-ack channel is drained safely; assert topic-wait probe re-runs when `WaitForTopic = true`
- [ ] 7.3 Non-fatal-error test: force a non-fatal error and assert no rebuild occurs (`raytree.connection.recoveries` not incremented)

## 8. RabbitMQ event hooks (no recovery code)

- [ ] 8.1 In `RabbitMqPublisher.InitializeAsync`, subscribe to `_connection.ConnectionShutdownAsync` (emit `disconnects` + log `Warning` when `Initiator != Application`), `_connection.RecoverySucceededAsync` (emit `recoveries{outcome="succeeded"}` + duration since the most recent shutdown + log `Information`), and `_connection.ConnectionRecoveryErrorAsync` (log `Information` only — no metric)
- [ ] 8.2 In `RabbitMqConsumer.InitializeAsync`, subscribe to the same three events; emit metrics with `component = "rabbitmq.consumer"`; **do not** emit logs (existing no-logger exception for `RabbitMqConsumer` stands)
- [ ] 8.3 Track per-instance `DateTime _lastShutdownAt` so duration can be computed when `RecoverySucceededAsync` fires
- [ ] 8.4 Register the `raytree.connection.state` gauge for both `rabbitmq.publisher` and `rabbitmq.consumer` keyed on `"{HostName}:{Port}"` — flip to `0` on `ConnectionShutdownAsync` (non-application), back to `1` on `RecoverySucceededAsync`
- [ ] 8.5 Do not add `ConnectionRecovery` property to `RabbitMqPublisherOptions` or `RabbitMqConsumerOptions`; do not disable `AutomaticRecoveryEnabled` / `TopologyRecoveryEnabled`

## 9. Tests for RabbitMQ hooks

- [ ] 9.1 `tests/RayTree.Plugins.RabbitMQ.Tests/RabbitMqRecoveryMetricsTests.cs` ([NonParallelizable], Testcontainers) — restart the broker container, assert `disconnects` and `recoveries{outcome="succeeded"}` are emitted with `component = "rabbitmq.publisher"` / `"rabbitmq.consumer"`
- [ ] 9.2 Application-shutdown test: invoke `DisposeAsync` cleanly and assert no `disconnects` are recorded (initiator is application)

## 10. Hosting + configuration wiring

- [ ] 10.1 In `src/RayTree.Hosting/ServiceCollectionExtensions.AddChangeTracking`, bind `ChangeTracking:Publisher:ConnectionRecovery` and `ChangeTracking:Subscriber:ConnectionRecovery` to `IOptions<ConnectionRecoveryOptions>` registrations distinguished by key
- [ ] 10.2 In the builder layer, when a plugin's `ConnectionRecovery` equals the parameterless default, swap it for the bound publisher/subscriber default; explicit overrides win
- [ ] 10.3 `tests/RayTree.Hosting.Tests/ConnectionRecoveryConfigurationTests.cs` — bind from in-memory configuration source, assert resolved options reach the plugin

## 11. Docs

- [ ] 11.1 Update `CLAUDE.md` plugin descriptions for `NotificationBasedPublisher`, `KafkaPublisher`, `KafkaConsumer`, `RabbitMqPublisher`, `RabbitMqConsumer` with the new `ConnectionRecovery` option (where applicable), the metric instruments, and the fact that RabbitMQ recovery is performed by the SDK
- [ ] 11.2 Update [docs/opentelemetry-metrics.md](docs/opentelemetry-metrics.md) with the four new instruments, tag semantics, and suggested histogram bucket boundaries (`[0.1, 0.5, 1, 2, 5, 10, 30, 60, 120]` s)
- [ ] 11.3 Update `AGENTS.md` logging-placement rule to call out that connection-recovery logs are runtime-service logs with non-null `ILogger<T>` (and that `RabbitMqConsumer` is silent for logs but observable in metrics)
- [ ] 11.4 Update `src/RayTree.Plugins.RabbitMQ/README.md` "Broker connection drops" row to reflect that the SDK recovers automatically and RayTree observes it
- [ ] 11.5 Update `src/RayTree.Plugins.Kafka/README.md` and `src/RayTree.Plugins.PostgreSQL/README.md` (if it exists) with the new recovery sections

## 12. CI

- [ ] 12.1 Confirm `.github/workflows/ci.yml` integration-test matrix passes — no new project added (tests live in existing per-plugin test projects)
- [ ] 12.2 Ensure all broker-restart integration tests are `[NonParallelizable]` and use unique topic/queue/channel names per test
