## 1. Core abstraction

- [ ] 1.1 Add `src/RayTree.Core/Resilience/ConnectionRecoveryOptions.cs` (public record, defaults `Enabled=true`, `InitialDelay=1s`, `MaxDelay=30s`, `Factor=2.0`, `JitterFraction=0.2`, `MaxAttempts=null`) with constructor-side validation throwing `ArgumentOutOfRangeException` for invalid values
- [ ] 1.2 Add `src/RayTree.Core/Resilience/IConnectionRecoveryStrategy.cs` (public interface, single `RunAsync(Func<CancellationToken,Task> attempt, RecoveryContext context, CancellationToken ct)` method)
- [ ] 1.3 Add `src/RayTree.Core/Resilience/RecoveryContext.cs` (internal readonly record struct with `Component`, `Endpoint`)
- [ ] 1.4 Add `src/RayTree.Core/Resilience/ExponentialBackoffRecoveryStrategy.cs` — `internal sealed class`, takes `ConnectionRecoveryOptions`, `RayTreeMeter`, `ILogger`, `TimeProvider`; computes `min(InitialDelay * Factor^(attempt-1), MaxDelay)` with uniform `±JitterFraction` jitter per attempt; emits metrics and logs at documented levels
- [ ] 1.5 Wire `RayTreeMeter` to expose four new instruments: `raytree.connection.disconnects` (Counter<long>), `raytree.connection.recoveries` (Counter<long>), `raytree.connection.recovery.duration` (Histogram<double>, unit `"s"`), and `raytree.connection.state` (ObservableGauge<int>); add internal helper methods `RecordDisconnect(component, endpoint)`, `RecordRecoverySuccess(...)`, `RecordRecoveryExhausted(...)`, and `RegisterConnectionStateGauge(Func<IEnumerable<(string, string, int)>>)` mirroring the existing pending-gauge shape
- [ ] 1.6 Add `InternalsVisibleTo` entry in `RayTree.Core.csproj` for `RayTree.Plugins.RabbitMQ`, `RayTree.Plugins.Kafka`, `RayTree.Plugins.PostgreSQL` so plugins can instantiate `ExponentialBackoffRecoveryStrategy` and call the meter helpers
- [ ] 1.7 Add `ChangeTrackingBuilder.UseConnectionRecoveryStrategy(IConnectionRecoveryStrategy)` (and corresponding interface method) so callers can supply a custom strategy; default uses `ExponentialBackoffRecoveryStrategy`
- [ ] 1.8 Bind `ConnectionRecoveryOptions` from `ChangeTracking:Publisher:ConnectionRecovery` and `ChangeTracking:Subscriber:ConnectionRecovery` in `RayTree.Hosting.AddChangeTracking`; expose two `IOptions<ConnectionRecoveryOptions>` registrations distinguished by key

## 2. Tests for core abstraction

- [ ] 2.1 Add `tests/RayTree.Core.Tests/Resilience/ConnectionRecoveryOptionsTests.cs` — defaults, validation throws for invalid `Factor`/`InitialDelay`/`MaxDelay`/`JitterFraction`/`MaxAttempts`
- [ ] 2.2 Add `tests/RayTree.Core.Tests/Resilience/ExponentialBackoffRecoveryStrategyTests.cs` — uses `FakeTimeProvider` to assert: first attempt runs immediately; backoff sequence `1s, 2s, 4s, 8s, 10s, 10s, …` when `MaxDelay=10s` and `JitterFraction=0`; jitter range is `[delay*0.8, delay*1.2]` for `JitterFraction=0.2`; cancellation between attempts and during delay; `MaxAttempts` exhaustion rethrows last exception; `Enabled=false` is enforced by callers (strategy still runs when constructed — the disable check lives at plugin level)
- [ ] 2.3 Add `tests/RayTree.Core.Tests/Resilience/RecoveryMetricsTests.cs` using inline `CapturingExporter` pattern from existing OTel tests — asserts `raytree.connection.disconnects` increments, `raytree.connection.recoveries` records both `outcome="succeeded"` and `outcome="exhausted"`, duration histogram records elapsed seconds, all carry `component`/`endpoint` tags

## 3. RabbitMQ plugin integration

- [ ] 3.1 In `RabbitMqPublisherOptions` and `RabbitMqConsumerOptions`, add `ConnectionRecovery` property (default `new ConnectionRecoveryOptions()`); add a clearly-documented `UseClientLibraryRecovery` escape hatch (default `false`) that, when `true` AND `ConnectionRecovery.Enabled = false`, re-enables `ConnectionFactory.AutomaticRecoveryEnabled`
- [ ] 3.2 In `RabbitMqPublisher.InitializeAsync`, set `factory.AutomaticRecoveryEnabled = false` and `factory.TopologyRecoveryEnabled = false` unless the escape hatch is set; subscribe to `_connection.ConnectionShutdownAsync` and `_channel.ChannelShutdownAsync`; gate the handler on `_disposed` so caller-initiated dispose does not trigger recovery
- [ ] 3.3 In `RabbitMqPublisher`, add an internal `RecoverAsync` method that invokes `IConnectionRecoveryStrategy` with an attempt delegate that rebuilds connection + channel + (when `DeclareExchange = true`) the exchange, and re-runs the topology-wait probe when `WaitForTopology = true`; gate `PublishAsync` on a `TaskCompletionSource<bool>` exposed by recovery so concurrent callers await the rebuild
- [ ] 3.4 In `RabbitMqConsumer.InitializeAsync`, the same shutdown-handler wiring; add `RecoverAsync` that rebuilds connection + channel + queue (when `DeclareQueue = true`) + bindings + reissues `BasicConsumeAsync`; re-run topology-wait probe when `WaitForTopology = true`
- [ ] 3.5 In `RabbitMqConsumer.AcknowledgeAsync` / `NegativeAcknowledgeAsync`, capture the `IChannel` reference at delivery time inside `RabbitMqEnvelopeMetadata`; on ack/nack, compare against current channel and silently no-op on mismatch (stale-tag handling)
- [ ] 3.6 `RabbitMqConsumer` recovery uses `logger: null` (the existing no-logger exception stands); metrics emission still works via the shared strategy + meter
- [ ] 3.7 Register the connection-state gauge once per (`RabbitMqPublisher` / `RabbitMqConsumer`) instance with the broker host as `endpoint`

## 4. Tests for RabbitMQ recovery

- [ ] 4.1 `tests/RayTree.Plugins.RabbitMQ.Tests/RabbitMqPublisherRecoveryTests.cs` (integration; Testcontainers `[NonParallelizable]`): pause the broker container mid-publish, resume, assert next `PublishAsync` succeeds, `raytree.connection.disconnects` = 1, `raytree.connection.recoveries{outcome="succeeded"}` = 1
- [ ] 4.2 `RabbitMqConsumerRecoveryTests.cs` — restart the broker container while a consumer is running, send a message after restart, assert it is delivered and processed without process restart
- [ ] 4.3 Negative test: `MaxAttempts = 1`, kill the broker permanently, assert `outcome = "exhausted"` is recorded and the exception bubbles
- [ ] 4.4 Stale-delivery-tag test: send a message, force a reconnect before the handler acks, then ack — asserts no exception is thrown and the broker redelivers
- [ ] 4.5 Escape-hatch test: `ConnectionRecovery.Enabled = false` + `UseClientLibraryRecovery = true` — assert `ConnectionFactory.AutomaticRecoveryEnabled` is `true` on the resulting factory

## 5. Kafka plugin integration

- [ ] 5.1 In `KafkaPublisherOptions` and `KafkaConsumerOptions`, add `ConnectionRecovery` property (default `new ConnectionRecoveryOptions()`)
- [ ] 5.2 In `KafkaPublisher`, register `IProducerBuilder.SetErrorHandler` callback; on `e.Error.IsFatal == true`, dispose the current producer and invoke `IConnectionRecoveryStrategy` with an attempt delegate that rebuilds via `GetProducerAsync` (including re-running the topic-wait probe when `WaitForTopic = true`); concurrent `PublishAsync` callers continue to await the existing `_buildSemaphore`
- [ ] 5.3 In `KafkaConsumer`'s poll thread loop, catch `KafkaException` with `Error.IsFatal == true`, dispose the current consumer on the same thread, invoke `IConnectionRecoveryStrategy` on that thread, rebuild via the existing init path (re-run topic-wait probe when `WaitForTopic = true`), reissue `Subscribe`, drain the post-handler channel discarding stale `ConsumeResult`s before resuming
- [ ] 5.4 Register the connection-state gauge once per (`KafkaPublisher` / `KafkaConsumer`) instance with `BootstrapServers` as `endpoint`

## 6. Tests for Kafka recovery

- [ ] 6.1 `tests/RayTree.Plugins.Kafka.Tests/KafkaPublisherRecoveryTests.cs` — integration test that simulates a fatal error via test hook (broker container restart with `MaxAttempts = unlimited` to force the path); assert metric emission and recovery completion
- [ ] 6.2 `KafkaConsumerRecoveryTests.cs` — same shape on the consumer side; assert deferred-ack channel is drained safely
- [ ] 6.3 Non-fatal-error test: force a non-fatal error and assert no rebuild occurs (`raytree.connection.recoveries` not incremented)
- [ ] 6.4 Topic-wait reprobe test: with `WaitForTopic = true`, restart the broker, delete the topic, recreate it; assert publisher waits and resumes

## 7. PostgreSQL NotificationBasedPublisher integration

- [ ] 7.1 In `NotificationBasedPublisherOptions`, add `ConnectionRecovery` property (default `new ConnectionRecoveryOptions()`)
- [ ] 7.2 In `NotificationBasedPublisher.ListenLoopAsync`, when the catch block flips `_listenerHealthy = false`, invoke `IConnectionRecoveryStrategy` with an attempt delegate that: opens a new `NpgsqlConnection`, calls `LISTEN {ChannelName}`, swaps `_connection` (disposing the old one), and sets `_listenerHealthy = true`; resume `WaitAsync` against the new connection
- [ ] 7.3 Verify `FallbackPollingLoopAsync` continues running while reconnect is in progress (current behaviour already covers this — make it explicit in a comment)
- [ ] 7.4 Register the connection-state gauge with `ChannelName` as `endpoint`

## 8. Tests for PostgreSQL LISTEN recovery

- [ ] 8.1 `tests/RayTree.Plugins.PostgreSQL.Tests/NotificationBasedPublisherRecoveryTests.cs` — integration test using Testcontainers; restart the Postgres container mid-stream, assert: notifications resume on the new connection; fallback polling delivered any record written during the gap; `raytree.connection.recoveries{outcome="succeeded"}` records the cycle

## 9. Hosting + configuration wiring

- [ ] 9.1 In `ServiceCollectionExtensions.AddChangeTracking`, bind both `ChangeTracking:Publisher:ConnectionRecovery` and `ChangeTracking:Subscriber:ConnectionRecovery` and pass them to the builder for use as the default-when-unset for each plugin option
- [ ] 9.2 In the builder layer, when a plugin's `ConnectionRecovery` equals the parameterless default, swap it for the bound publisher-or-subscriber default; explicit overrides win
- [ ] 9.3 Add `tests/RayTree.Hosting.Tests/ConnectionRecoveryConfigurationTests.cs` — bind from in-memory configuration source, assert resolved options reach the plugin

## 10. Docs

- [ ] 10.1 Update `CLAUDE.md` plugin descriptions for `RabbitMqPublisher`, `RabbitMqConsumer`, `KafkaPublisher`, `KafkaConsumer`, `NotificationBasedPublisher` with the new `ConnectionRecovery` option, the disable-of-client-auto-recovery for RabbitMQ, and the escape hatch
- [ ] 10.2 Update [docs/opentelemetry-metrics.md](docs/opentelemetry-metrics.md) with the four new instruments, tag semantics, and suggested histogram bucket boundaries (e.g. `0.1, 0.5, 1, 2, 5, 10, 30, 60, 120` s)
- [ ] 10.3 Update `AGENTS.md` logging-placement rule to call out that connection-recovery logs are runtime-service logs with non-null `ILogger<T>` (and that `RabbitMqConsumer` is silent for logs but observable in metrics)
- [ ] 10.4 Update `src/RayTree.Plugins.RabbitMQ/README.md` to describe the new behaviour replacing the existing "A new connection must be established (typically by recreating the consumer)" line
- [ ] 10.5 Update `src/RayTree.Plugins.Kafka/README.md` and the `RayTree.Plugins.PostgreSQL` README with the recovery section
- [ ] 10.6 Add an example to `samples/` (or update the existing Postgres / Kafka / RabbitMQ samples) showing `appsettings.json` recovery configuration

## 11. CI

- [ ] 11.1 Confirm `.github/workflows/ci.yml` integration-test matrix still passes (no new project to add — recovery tests live in the existing per-plugin test projects)
- [ ] 11.2 Ensure broker-restart integration tests are tagged `[NonParallelizable]` and use unique topic/queue names per test to avoid cross-test contamination
