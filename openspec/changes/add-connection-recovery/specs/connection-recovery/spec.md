## ADDED Requirements

### Requirement: Unified recovery strategy abstraction
`RayTree.Core` SHALL expose a public `IConnectionRecoveryStrategy` interface and a default `ExponentialBackoffRecoveryStrategy` implementation. The strategy SHALL accept a delegate that performs one recovery attempt and SHALL invoke that delegate repeatedly until it returns successfully, the configured retry budget is exhausted, or the `CancellationToken` is cancelled. Inter-attempt delays SHALL be computed as `min(InitialDelay × Factor^(attempt-1), MaxDelay)` with `±JitterFraction` random jitter applied to each delay independently.

#### Scenario: First attempt runs immediately
- **WHEN** the strategy is invoked with a non-cancelled token
- **THEN** the supplied attempt delegate SHALL be invoked once before any delay is applied.

#### Scenario: Successful attempt stops the loop
- **WHEN** an attempt completes without throwing
- **THEN** the strategy SHALL return immediately without scheduling further attempts.

#### Scenario: Exponential backoff between attempts
- **WHEN** the first attempt throws and the strategy was configured with `InitialDelay = 1s`, `Factor = 2.0`, `MaxDelay = 10s`, `JitterFraction = 0`
- **THEN** the delay before the second attempt SHALL be 1s, before the third 2s, before the fourth 4s, before the fifth 8s, and 10s for every subsequent attempt (capped at `MaxDelay`).

#### Scenario: Jitter is applied per attempt
- **WHEN** `JitterFraction = 0.2` is configured
- **THEN** each scheduled inter-attempt delay SHALL be drawn uniformly from `[delay × 0.8, delay × 1.2]`, computed independently per attempt.

#### Scenario: Cancellation aborts the loop
- **WHEN** the cancellation token is cancelled while the strategy is sleeping between attempts or before invoking an attempt
- **THEN** the strategy SHALL throw `OperationCanceledException` and SHALL NOT invoke the attempt delegate again.

#### Scenario: Retry budget exhaustion rethrows the last exception
- **WHEN** `MaxAttempts` is non-null and every attempt up to that count throws
- **THEN** the strategy SHALL rethrow the most recent exception after the final attempt.

### Requirement: Recovery options shape
`RayTree.Core` SHALL expose a public `ConnectionRecoveryOptions` record with the fields `Enabled` (bool, default `true`), `InitialDelay` (TimeSpan, default `1s`), `MaxDelay` (TimeSpan, default `30s`), `Factor` (double, default `2.0`), `JitterFraction` (double, default `0.2`), and `MaxAttempts` (int?, default `null` — unlimited). The record SHALL validate its values at construction: `InitialDelay > TimeSpan.Zero`, `MaxDelay >= InitialDelay`, `Factor >= 1.0`, `0 <= JitterFraction <= 1`, and `MaxAttempts == null || MaxAttempts > 0`. Invalid values SHALL throw `ArgumentOutOfRangeException`.

#### Scenario: Defaults match documented values
- **WHEN** `new ConnectionRecoveryOptions()` is constructed with no overrides
- **THEN** the field values SHALL be exactly `Enabled = true`, `InitialDelay = 1s`, `MaxDelay = 30s`, `Factor = 2.0`, `JitterFraction = 0.2`, `MaxAttempts = null`.

#### Scenario: Invalid factor is rejected
- **WHEN** `Factor = 0.5` is supplied
- **THEN** construction SHALL throw `ArgumentOutOfRangeException` with `paramName = "Factor"`.

#### Scenario: Disabled options short-circuit recovery
- **WHEN** `Enabled = false` is supplied and a plugin observes a connection loss
- **THEN** the plugin SHALL NOT invoke `IConnectionRecoveryStrategy` and SHALL surface the underlying disconnect on the next operation (matching pre-change behaviour).

### Requirement: RabbitMQ publisher recovers connection and channel
`RabbitMqPublisher` SHALL register handlers on `IConnection.ConnectionShutdownAsync` and `IChannel.ChannelShutdownAsync` and, on shutdown that is not caller-initiated, SHALL run `IConnectionRecoveryStrategy` to rebuild the connection, rebuild the channel, and (when `DeclareExchange = true`) re-declare the exchange. While recovery is in progress, calls to `PublishAsync` SHALL await recovery completion before issuing the underlying `BasicPublishAsync`. The publisher SHALL disable the RabbitMQ.Client library's `AutomaticRecoveryEnabled` so behaviour is deterministic and owned by RayTree.

#### Scenario: Publisher rebuilds after broker restart
- **WHEN** the broker disconnects and reconnects (e.g. RabbitMQ container restart) while the publisher is idle
- **THEN** the publisher SHALL rebuild the connection and channel through `IConnectionRecoveryStrategy`
- **AND** SHALL re-declare the exchange when `DeclareExchange = true`
- **AND** the next `PublishAsync` call SHALL succeed against the new channel without the caller observing the disconnect (other than backoff latency).

#### Scenario: PublishAsync during recovery awaits completion
- **WHEN** a `PublishAsync` call is made while recovery is in progress
- **THEN** the call SHALL await recovery's completion (bounded only by the caller's `CancellationToken`) and SHALL then issue the publish against the new channel.

#### Scenario: Caller-initiated dispose does not trigger recovery
- **WHEN** `RabbitMqPublisher.Dispose` is invoked and triggers connection shutdown
- **THEN** the shutdown handler SHALL detect the disposed state via the existing `_disposed` guard and SHALL NOT invoke the recovery strategy.

### Requirement: RabbitMQ consumer recovers connection, channel, and consumer registration
`RabbitMqConsumer` SHALL register handlers on `IConnection.ConnectionShutdownAsync` and `IChannel.ChannelShutdownAsync` and, on non-caller-initiated shutdown, SHALL run `IConnectionRecoveryStrategy` to rebuild the connection, rebuild the channel, re-declare the queue (when `DeclareQueue = true`), re-bind to the exchange (when `ExchangeName` is non-empty), and re-issue `BasicConsumeAsync`. Pending `AckAfterHandler` delivery-tag state captured in unacknowledged `MessageEnvelope` instances SHALL be invalidated — subsequent `AcknowledgeAsync`/`NegativeAcknowledgeAsync` calls referencing a pre-recovery delivery tag SHALL be silent no-ops (delivery tags are scoped to a channel and the broker will redeliver via the standard at-least-once contract). The consumer SHALL disable the RabbitMQ.Client library's `AutomaticRecoveryEnabled`.

#### Scenario: Consumer rebuilds after broker restart
- **WHEN** the broker disconnects and reconnects while the consumer is running
- **THEN** the consumer SHALL rebuild the connection, channel, topology, and consumer registration via the recovery strategy
- **AND** SHALL resume delivery from the queue on the new channel without requiring process restart.

#### Scenario: Stale delivery tag ack is a no-op
- **WHEN** a handler attempts to ack a delivery tag captured before a recovery cycle
- **THEN** `AcknowledgeAsync`/`NegativeAcknowledgeAsync` SHALL detect the channel-mismatch and return without throwing
- **AND** SHALL NOT issue any broker call (the broker will redeliver after channel close per AMQP semantics).

### Requirement: Kafka publisher recovers native handle on fatal error
`KafkaPublisher` SHALL register the `IProducerBuilder.SetErrorHandler` callback and, on errors whose `Error.IsFatal == true`, SHALL run `IConnectionRecoveryStrategy` to rebuild the underlying `IProducer<string, byte[]>`. Concurrent `PublishAsync` callers SHALL await the rebuild via the existing `_buildSemaphore` mechanism. Non-fatal errors SHALL NOT trigger rebuild — librdkafka recovers those internally.

#### Scenario: Fatal error triggers rebuild
- **WHEN** the error handler receives a `KafkaException` with `Error.IsFatal = true`
- **THEN** the publisher SHALL dispose the current `IProducer` and rebuild a new one through `IConnectionRecoveryStrategy`.

#### Scenario: Non-fatal error is not rebuilt
- **WHEN** the error handler receives a non-fatal error (e.g. transient broker timeout)
- **THEN** the publisher SHALL log at `Warning` and SHALL NOT rebuild the producer.

#### Scenario: Rebuild re-runs topic-wait probe when enabled
- **WHEN** the publisher was constructed with `WaitForTopic = true` and a rebuild occurs
- **THEN** the rebuild path SHALL re-run the topic-wait probe before the new producer is exposed to callers.

### Requirement: Kafka consumer recovers on fatal error
`KafkaConsumer` SHALL detect `KafkaException` instances thrown from `Consume` whose `Error.IsFatal == true` on its dedicated poll thread, dispose the current `IConsumer`, and run `IConnectionRecoveryStrategy` on the same thread to rebuild the consumer, re-subscribe to the topic, and resume polling. The internal post-handler channel for deferred-ack messages SHALL be drained and any pending `Commit`/`SeekBack` actions referencing the disposed consumer SHALL be discarded (the broker will redeliver per the at-least-once contract on the new consumer's join).

#### Scenario: Fatal exception on the poll thread triggers rebuild
- **WHEN** `Consume` throws `KafkaException` with `Error.IsFatal = true`
- **THEN** the poll thread SHALL dispose the current consumer and rebuild a new one via `IConnectionRecoveryStrategy` before resuming polling.

#### Scenario: Rebuild re-runs topic-wait probe when enabled
- **WHEN** the consumer was constructed with `WaitForTopic = true` and a rebuild occurs
- **THEN** the rebuild path SHALL re-run the topic-wait probe before `Subscribe` is called on the new consumer.

#### Scenario: Pending deferred ack against disposed consumer is dropped
- **WHEN** a handler completes for a `MessageEnvelope` whose `ConsumeResult` was issued by the now-disposed consumer
- **THEN** the post-handler drain SHALL detect the mismatch and SHALL NOT invoke `Commit` or `Seek` against the new consumer.

### Requirement: PostgreSQL NotificationBasedPublisher reconnects LISTEN
`NotificationBasedPublisher.ListenLoopAsync` SHALL, upon detecting that the LISTEN `NpgsqlConnection` has been lost, run `IConnectionRecoveryStrategy` to open a new connection, re-issue `LISTEN {ChannelName}`, and replace the held `_connection` reference. The fallback polling loop SHALL continue to run while the LISTEN connection is being rebuilt so that no records are lost during recovery. On successful reconnect, `_listenerHealthy` SHALL be set to `true` and the recovery log SHALL be emitted at `Information`.

#### Scenario: LISTEN connection drop triggers reconnect
- **WHEN** `_connection.WaitAsync` throws because the underlying TCP connection was lost
- **THEN** the loop SHALL run the recovery strategy to open a fresh `NpgsqlConnection`, issue `LISTEN`, swap it in, and resume `WaitAsync` against the new connection.

#### Scenario: Fallback polling continues during reconnect
- **WHEN** the LISTEN connection is being rebuilt
- **THEN** `FallbackPollingLoopAsync` SHALL continue processing unpublished records at `FallbackPollingInterval` cadence until `_listenerHealthy` returns to `true`.

#### Scenario: Recovery is logged at Information
- **WHEN** the recovery strategy succeeds in re-establishing LISTEN
- **THEN** an `Information` log SHALL be emitted indicating the channel name and elapsed time, matching the existing log message text.

### Requirement: Plugin builder methods expose `UseConnectionRecovery`
Each plugin options class participating in connection recovery (`RabbitMqPublisherOptions`, `RabbitMqConsumerOptions`, `KafkaPublisherOptions`, `KafkaConsumerOptions`, `NotificationBasedPublisherOptions`) SHALL expose a `ConnectionRecovery` property of type `ConnectionRecoveryOptions` initialised to a default-constructed value. Each plugin's fluent builder extension SHALL accept an `Action<ConnectionRecoveryOptions>?` configurator (or equivalent) so callers can tune recovery per plugin without constructing options manually.

#### Scenario: Default property value is enabled
- **WHEN** any of the listed options classes is constructed via its parameterless constructor
- **THEN** `ConnectionRecovery.Enabled` SHALL be `true` and the other fields SHALL match the documented defaults.

#### Scenario: Fluent override is honoured
- **WHEN** a caller chains `UseRabbitMq(o => { o.ConnectionRecovery = o.ConnectionRecovery with { MaxAttempts = 5 }; })`
- **THEN** the resulting `RabbitMqPublisher` SHALL use a strategy bounded to 5 attempts and surface the disconnect on the 6th retry.

### Requirement: Recovery metrics are emitted
The system SHALL emit four `System.Diagnostics.Metrics` instruments on the `"RayTree"` meter for every plugin that participates in connection recovery: `raytree.connection.disconnects` (counter, tagged with `component` and `endpoint`), `raytree.connection.recoveries` (counter, tagged with `component`, `endpoint`, and `outcome` where `outcome ∈ {"succeeded", "exhausted"}`), `raytree.connection.recovery.duration` (histogram, unit `s`, tagged with `component`, `endpoint`, and `outcome`), and `raytree.connection.state` (observable up/down gauge with values `1` (connected) or `0` (disconnected), tagged with `component` and `endpoint`).

`component` SHALL be one of `"rabbitmq.publisher"`, `"rabbitmq.consumer"`, `"kafka.publisher"`, `"kafka.consumer"`, `"postgres.notification"`. `endpoint` SHALL be a stable identifier of the connection target (e.g. the broker host or the LISTEN channel name) drawn from plugin configuration — never from caller-supplied request data.

#### Scenario: Disconnect increments counter
- **WHEN** a plugin detects connection or channel loss
- **THEN** `raytree.connection.disconnects` SHALL be incremented by 1 with the corresponding `component` and `endpoint` tags.

#### Scenario: Successful recovery records duration with outcome="succeeded"
- **WHEN** a recovery cycle completes successfully
- **THEN** `raytree.connection.recoveries` SHALL be incremented by 1 and `raytree.connection.recovery.duration` SHALL record the elapsed seconds, both with `outcome = "succeeded"`.

#### Scenario: Exhausted recovery records duration with outcome="exhausted"
- **WHEN** recovery exhausts `MaxAttempts` without success
- **THEN** `raytree.connection.recoveries` SHALL be incremented by 1 and `raytree.connection.recovery.duration` SHALL record the elapsed seconds, both with `outcome = "exhausted"`.

#### Scenario: State gauge reflects current connectivity
- **WHEN** an OTel `MeterProvider` collects metrics
- **THEN** `raytree.connection.state` SHALL emit `1` for each healthy component and `0` for each component currently inside a recovery cycle.

### Requirement: Recovery is logged at documented levels
For every component participating in recovery, the plugin SHALL emit the following log entries through its existing `ILogger<T>`:

- First detection of disconnect: `Warning`, including the underlying exception, `{Component}`, and `{Endpoint}`.
- Each retry attempt: `Information`, with `{AttemptNumber}` and `{Delay}` (the actually scheduled delay including jitter).
- Successful recovery: `Information`, with `{AttemptCount}` and `{Duration}` in seconds.
- Exhausted attempts: `Error`, with the most recent exception, `{Component}`, `{Endpoint}`, and `{AttemptCount}`.

Recovery log entries SHALL be emitted via the runtime-service logger (non-null) — no `NullLoggerFactory` fallback inside the runtime path. The exception to the logging-placement rule for `RabbitMqConsumer` (no logger) SHALL stand: when no logger is available, recovery SHALL still run correctly but SHALL produce no log entries.

#### Scenario: First detection logged at Warning
- **WHEN** a plugin detects a disconnect for the first time in a recovery cycle
- **THEN** a `Warning` log SHALL be emitted including the underlying exception and component identifiers.

#### Scenario: Retry attempt logged at Information
- **WHEN** the recovery strategy is about to schedule the Nth retry attempt
- **THEN** an `Information` log SHALL be emitted with `{AttemptNumber} = N` and `{Delay}` equal to the actually-scheduled delay (including jitter).

#### Scenario: Exhausted attempts logged at Error
- **WHEN** `MaxAttempts` is exceeded
- **THEN** an `Error` log SHALL be emitted with the most recent exception immediately before the recovery cycle terminates.

### Requirement: Recovery is configurable via `appsettings.json`
`RayTree.Hosting.AddChangeTracking` SHALL bind `ConnectionRecoveryOptions` instances from configuration sections `ChangeTracking:Publisher:ConnectionRecovery` and `ChangeTracking:Subscriber:ConnectionRecovery`. The bound values SHALL be applied as the default for any plugin whose own options do not explicitly override `ConnectionRecovery`. Explicit per-plugin overrides SHALL win over the bound default.

#### Scenario: Bound section sets the default
- **WHEN** `appsettings.json` contains `"ChangeTracking:Publisher:ConnectionRecovery": { "MaxAttempts": 10 }`
- **AND** a publisher plugin is registered without an explicit `ConnectionRecovery` override
- **THEN** that plugin's recovery strategy SHALL be bounded to 10 attempts.

#### Scenario: Per-plugin override wins
- **WHEN** the same configuration is set AND a Kafka publisher is registered with `UseKafka(o => { o.ConnectionRecovery = new ConnectionRecoveryOptions { MaxAttempts = 3 }; })`
- **THEN** the Kafka publisher SHALL use 3 attempts and the rest of the publishers SHALL use 10.
