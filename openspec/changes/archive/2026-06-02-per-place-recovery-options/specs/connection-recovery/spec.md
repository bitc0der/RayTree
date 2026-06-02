## MODIFIED Requirements

### Requirement: Connection recovery options shape
Each plugin that owns a connection-recovery loop SHALL define its **own** public recovery-options type in its own assembly; `RayTree.Core` SHALL NOT define a shared recovery-options type. `RayTree.Plugins.PostgreSQL` SHALL expose `PostgresConnectionRecoveryOptions`; `RayTree.Plugins.Kafka` SHALL expose `KafkaConnectionRecoveryOptions`. Each type SHALL carry the fields `Enabled` (bool, default `true`), `InitialDelay` (TimeSpan, default `1s`), `MaxDelay` (TimeSpan, default `30s`), `Factor` (double, default `2.0`), `JitterFraction` (double, default `0.2`), and `MaxAttempts` (int?, default `null` — unlimited). Each type SHALL validate per-field invariants at init time — `InitialDelay > TimeSpan.Zero`, `Factor >= 1.0`, `0 <= JitterFraction <= 1`, and `MaxAttempts == null || MaxAttempts > 0` — throwing `ArgumentOutOfRangeException`, and SHALL expose a `Validate()` method enforcing the cross-field invariant `MaxDelay >= InitialDelay` (also throwing `ArgumentOutOfRangeException`), which the consuming plugin calls before entering its retry loop.

#### Scenario: Defaults match documented values
- **WHEN** `new PostgresConnectionRecoveryOptions()` or `new KafkaConnectionRecoveryOptions()` is constructed with no overrides
- **THEN** the field values SHALL be exactly `Enabled = true`, `InitialDelay = 1s`, `MaxDelay = 30s`, `Factor = 2.0`, `JitterFraction = 0.2`, `MaxAttempts = null`.

#### Scenario: Invalid factor is rejected
- **WHEN** `Factor = 0.5` is supplied to either plugin-local options type
- **THEN** construction SHALL throw `ArgumentOutOfRangeException` with `paramName = "Factor"`.

#### Scenario: Cross-field invariant is rejected on Validate
- **WHEN** an options instance with `MaxDelay < InitialDelay` is constructed AND `Validate()` is called
- **THEN** `Validate()` SHALL throw `ArgumentOutOfRangeException` with `paramName = "MaxDelay"`.

#### Scenario: Core exposes no shared recovery-options type
- **WHEN** a caller inspects `RayTree.Core` (e.g. via reflection or autocomplete)
- **THEN** no `ConnectionRecoveryOptions` type SHALL be present in the `RayTree.Core.Resilience` namespace
- **AND** the connection-metric facade (`RayTreeMeter.RecordConnectionDisconnect` / `RecordConnectionRecovery` / `RegisterConnectionStateGauge`) SHALL NOT be present — those methods are removed along with the `raytree.connection.*` instruments.

#### Scenario: Disabled options short-circuit recovery
- **WHEN** `Enabled = false` AND the owning Postgres or Kafka plugin observes a connection-fault exception
- **THEN** the plugin SHALL surface the exception on the next caller-facing call (or, for the Kafka consumer poll thread, on the next iteration) — no rebuild attempt SHALL be made.

### Requirement: Recovery options exposure on plugin options classes
`NotificationBasedPublisherOptions` SHALL expose a `ConnectionRecovery` property of type `PostgresConnectionRecoveryOptions` initialised to `new PostgresConnectionRecoveryOptions()`. `KafkaPublisherOptions` and `KafkaConsumerOptions` SHALL each expose a `ConnectionRecovery` property of type `KafkaConnectionRecoveryOptions` initialised to `new KafkaConnectionRecoveryOptions()`. `RabbitMqPublisherOptions` and `RabbitMqConsumerOptions` SHALL NOT expose this property — RabbitMQ recovery is owned by the SDK and is not configurable through RayTree.

#### Scenario: Default property value is enabled
- **WHEN** `NotificationBasedPublisherOptions`, `KafkaPublisherOptions`, or `KafkaConsumerOptions` is constructed via its parameterless constructor
- **THEN** `ConnectionRecovery.Enabled` SHALL be `true` and the other fields SHALL match the documented defaults.

#### Scenario: Property type is the plugin-local options type
- **WHEN** a caller reads `KafkaPublisherOptions.ConnectionRecovery`
- **THEN** the value SHALL be a `KafkaConnectionRecoveryOptions` instance
- **AND** reading `NotificationBasedPublisherOptions.ConnectionRecovery` SHALL yield a `PostgresConnectionRecoveryOptions` instance.

#### Scenario: RabbitMQ options do not expose ConnectionRecovery
- **WHEN** a caller inspects `RabbitMqPublisherOptions` / `RabbitMqConsumerOptions` via reflection or autocomplete
- **THEN** no `ConnectionRecovery` property SHALL be present
- **AND** the SDK's recovery options (e.g. `NetworkRecoveryInterval`) remain accessible through the underlying `ConnectionFactory` only if the caller constructs one explicitly.

### Requirement: PostgreSQL NotificationBasedPublisher reconnects LISTEN
`NotificationBasedPublisher` SHALL, upon catching an exception from `NpgsqlConnection.WaitAsync` for which `IsConnectionFault(ex)` returns `true`, dispose the broken connection and run an inline exponential-backoff reconnect loop bounded by `NotificationBasedPublisherOptions.ConnectionRecovery`. The loop SHALL open a fresh `NpgsqlConnection`, re-attach the `Notification` event handler, issue `LISTEN {ChannelName}`, and resume `WaitAsync` against the new connection. The fallback polling loop SHALL continue running throughout, providing the safety net for records written during reconnect.

`IsConnectionFault` for the Postgres plugin SHALL return `true` for: `NpgsqlException` with `IsTransient = true`; `NpgsqlException` whose inner exception is `SocketException` or `IOException`; `PostgresException` with `SqlState` in `{"57P01", "57P02", "57P03"}` (admin_shutdown, crash_shutdown, cannot_connect_now); and `ObjectDisposedException`. All other exceptions SHALL propagate without triggering reconnect.

#### Scenario: LISTEN connection drop triggers reconnect
- **WHEN** `WaitAsync` throws an exception that `IsConnectionFault` classifies as a connection fault
- **THEN** the loop SHALL dispose the broken connection, open a fresh `NpgsqlConnection`, issue `LISTEN {ChannelName}`, and resume `WaitAsync` against the new connection.

#### Scenario: Fallback polling continues during reconnect
- **WHEN** the LISTEN connection is being rebuilt
- **THEN** `FallbackPollingLoopAsync` SHALL continue processing unpublished records at `FallbackPollingInterval` cadence until `_listenerHealthy` returns to `true`.

#### Scenario: Non-connection exception propagates
- **WHEN** `WaitAsync` throws an exception that `IsConnectionFault` returns `false` for (e.g. an unexpected internal error)
- **THEN** the exception SHALL propagate out of `ListenLoopAsync` — no reconnect SHALL be attempted.

#### Scenario: Recovery is logged at Information
- **WHEN** reconnect completes successfully
- **THEN** the existing `Information` "LISTEN connection on {ChannelName} recovered" log SHALL be emitted (unchanged from current behaviour)
- **AND** no connection metric SHALL be recorded (the `raytree.connection.*` instruments no longer exist).

### Requirement: Kafka publisher rebuilds on fatal error
`KafkaPublisher` SHALL register `IProducerBuilder.SetErrorHandler` during producer construction. When the handler receives an error with `Error.IsFatal = true`, the publisher SHALL dispose the current `IProducer` and null out the cached reference. The next `PublishAsync` call SHALL re-enter the existing lazy `GetProducerAsync` build path — which re-runs the `WaitForTopic` probe when enabled — bounded by `KafkaPublisherOptions.ConnectionRecovery`. Non-fatal errors SHALL NOT trigger rebuild; librdkafka recovers those internally. No connection metric SHALL be emitted (the `raytree.connection.*` instruments no longer exist).

#### Scenario: Fatal error disposes the producer
- **WHEN** the error handler receives an error with `Error.IsFatal = true`
- **THEN** the cached `IProducer` SHALL be disposed and the cached reference SHALL be set to `null`
- **AND** no connection metric SHALL be recorded.

#### Scenario: Next publish rebuilds via existing path
- **WHEN** a subsequent `PublishAsync` is invoked after a fatal-error dispose
- **THEN** the existing `GetProducerAsync` path SHALL build a fresh producer
- **AND** the probe SHALL re-run when `WaitForTopic = true`.

#### Scenario: Non-fatal error is ignored
- **WHEN** the error handler receives a non-fatal error
- **THEN** the publisher SHALL log at `Warning` and SHALL NOT dispose the producer.

### Requirement: RabbitMQ recovery is observed via logs, not metrics
The RabbitMQ publisher and consumer SHALL rely on `RabbitMQ.Client.AutomaticRecoveryEnabled = true` and `TopologyRecoveryEnabled = true` (the library defaults) for the actual recovery mechanism. RayTree SHALL NOT disable these flags.

The `RabbitMqPublisher` SHALL subscribe to the SDK's `ConnectionShutdownAsync`, `RecoverySucceededAsync`, and `ConnectionRecoveryErrorAsync` events to emit the documented log entries (`Warning` on non-application shutdown, `Information` on recovery with elapsed `{Duration}`, `Information` per failed internal recovery attempt). It SHALL NOT emit any connection metric and SHALL NOT accept a `RayTreeMeter` constructor parameter.

The `RabbitMqConsumer` has no logger and therefore SHALL NOT subscribe to any recovery event: with the connection metrics removed, those subscriptions produced no operator-visible signal. The consumer SHALL NOT accept a `RayTreeMeter` constructor parameter.

#### Scenario: Publisher logs a Warning on non-application shutdown
- **WHEN** the `RabbitMqPublisher`'s `IConnection` raises `ConnectionShutdownAsync` with `Initiator != Application`
- **THEN** a `Warning` log SHALL be emitted with the shutdown reason, `{Endpoint}`, `{ReplyCode}`, and `{ReplyText}`
- **AND** no metric SHALL be recorded.

#### Scenario: Publisher logs Information on recovery
- **WHEN** the publisher's `IConnection` raises `RecoverySucceededAsync`
- **THEN** an `Information` log SHALL be emitted with `{Endpoint}` and `{Duration}` (seconds since the most recent non-application shutdown)
- **AND** no metric SHALL be recorded.

#### Scenario: Application-initiated shutdown is silent
- **WHEN** `RabbitMqPublisher.DisposeAsync` is invoked and the resulting shutdown event has `Initiator = Application`
- **THEN** no warning SHALL be logged and no metric SHALL be recorded.

#### Scenario: Consumer does not observe recovery
- **WHEN** the `RabbitMqConsumer`'s connection shuts down and later recovers
- **THEN** the consumer SHALL emit neither a log entry nor a metric
- **AND** the consumer constructor SHALL expose no `RayTreeMeter` parameter.

### Requirement: Outbox connection-fault observability
`IOutbox` SHALL expose three default-implemented members so consumers of an arbitrary `IOutbox` can classify transient connection faults without retry code:

- `bool IsConnectionFault(Exception ex)` — default `false`. Concrete implementations override to classify connection-level exceptions versus application-level exceptions.
- `string? ConnectionComponent { get; }` — default `null`. Returns a stable component identifier used as a structured-log property, or `null` when the outbox has no observable external connection.
- `string? ConnectionEndpoint { get; }` — default `null`. Returns the endpoint identifier used as a structured-log property.

`PostgreSqlOutbox<TEntity>` SHALL override these: `IsConnectionFault` returns `true` for `NpgsqlException { IsTransient: true }`, `NpgsqlException` with `SocketException`/`IOException` inner, `PostgresException` with `SqlState` in `{"57P01", "57P02", "57P03", "08000", "08003", "08006", "08001", "08004", "08007"}`, and `ObjectDisposedException`. `ConnectionComponent` returns `"postgres.outbox"`. `ConnectionEndpoint` returns `"{Host}:{Port}"`. The classifier used by `PostgreSqlOutbox` SHALL be the same `static bool PostgresFault.IsConnectionFault(Exception)` used by `NotificationBasedPublisher`.

`OutboxPublisherService.ProcessBatchAsync`'s batch-error catch block SHALL consult `_outbox.IsConnectionFault(ex)` and `_outbox.ConnectionComponent`. When the classifier returns `true` AND `ConnectionComponent` is non-null, the failure SHALL be logged at `Warning` instead of `Error`, with `{Component}` and `{Endpoint}` structured properties, and a per-service `_outboxUnhealthy` flag SHALL be set. On the first subsequent batch that completes without throwing, an `Information` "outbox connection recovered" log SHALL be emitted with `{Duration}` (wall-clock seconds since the first failure) and the flag SHALL be cleared. When the classifier returns `false` OR `ConnectionComponent` is `null`, the existing `Error` log path SHALL be preserved unchanged. No connection metric SHALL be emitted in any of these paths. No retry code is added — the existing polling cadence is the retry.

`NotificationBasedPublisher.FallbackPollingLoopAsync` SHALL apply the same `Warning`/`Information` log pattern per outbox, keyed by entity type in a `ConcurrentDictionary<Type, bool>`, with no metric emission.

#### Scenario: Outbox publisher log demotion on connection fault
- **WHEN** `OutboxPublisherService.ProcessBatchAsync` catches an exception and `_outbox.IsConnectionFault(ex)` returns `true` AND `ConnectionComponent` is non-null
- **THEN** the failure SHALL be logged at `Warning` (not `Error`) with `{Component}` and `{Endpoint}`
- **AND** no connection metric SHALL be emitted.

#### Scenario: Outbox publisher recovery log on first success after failure
- **WHEN** `OutboxPublisherService.ProcessBatchAsync` completes without throwing AND the service was previously unhealthy
- **THEN** an `Information` "outbox connection recovered" log SHALL be emitted with `{Duration}`
- **AND** the `_outboxUnhealthy` flag SHALL be cleared
- **AND** no connection metric SHALL be emitted.

#### Scenario: Non-connection-fault exception preserves existing Error log
- **WHEN** `OutboxPublisherService.ProcessBatchAsync` catches an exception for which `_outbox.IsConnectionFault(ex)` returns `false`
- **THEN** the existing `Error` log path SHALL be unchanged.

#### Scenario: Write-path exceptions still propagate to the caller
- **WHEN** a caller invokes `EntityChangeTracker.TrackInsertAsync` and the underlying `PostgreSqlOutbox.WriteAsync` throws a connection-fault exception
- **THEN** the exception SHALL propagate to the caller unchanged
- **AND** no automatic retry SHALL be performed by the library at the write path.

### Requirement: Recovery logs are emitted at documented levels
Each plugin that participates in connection recovery and owns an `ILogger<T>` SHALL emit the following log entries:

- **First detection of disconnect per recovery cycle**: `Warning`, with the underlying exception (where available), `{Component}`, and `{Endpoint}`.
- **Each retry attempt** (Postgres and Kafka only — the two plugins owning their own retry loop): `Information`, with `{Component}`, `{Endpoint}`, `{AttemptNumber}`, and `{Delay}` (the actually scheduled delay including jitter, in seconds).
- **Successful recovery**: `Information`, with `{Component}`, `{Endpoint}`, `{AttemptCount}` (for Postgres/Kafka), and `{Duration}` (wall-clock seconds elapsed since the first detection in this cycle).
- **Exhausted attempts** (Postgres and Kafka only): `Error`, with the most recent exception, `{Component}`, `{Endpoint}`, and `{AttemptCount}`.

No connection metric SHALL accompany any of these logs — the `raytree.connection.*` instruments no longer exist. `RabbitMqConsumer` has no `ILogger` field and SHALL produce neither logs nor metrics for connection recovery.

#### Scenario: First disconnect emits Warning with exception
- **WHEN** a logger-owning participating plugin observes the first disconnect of a recovery cycle (Kafka error handler fires, Postgres `WaitAsync` throws a connection fault, RabbitMQ publisher `ConnectionShutdownAsync` fires non-application)
- **THEN** a `Warning` log SHALL be emitted with `{Component}` and `{Endpoint}` and the underlying exception attached where available.

#### Scenario: Retry attempt emits Information with delay
- **WHEN** Postgres or Kafka schedules the Nth retry attempt
- **THEN** an `Information` log SHALL be emitted with `{AttemptNumber} = N` and `{Delay}` equal to the actually-scheduled delay (including jitter) in seconds.

#### Scenario: RabbitMqConsumer recovery is fully silent
- **WHEN** a recovery cycle runs for `RabbitMqConsumer`
- **THEN** no log entries SHALL be emitted
- **AND** no connection metric SHALL be recorded.

## REMOVED Requirements

### Requirement: Recovery options bound from configuration
**Reason**: The shared `ConnectionRecoveryOptions` type that this binding produced no longer exists; recovery options are now plugin-local. The bound named-options defaults were never auto-injected into plugin construction — callers had to resolve `IOptionsMonitor<ConnectionRecoveryOptions>.Get(key)` and merge manually — so the binding provided no automatic behavior to preserve.

**Migration**: Configure recovery per plugin in the plugin's `Use*` configure lambda. To drive it from configuration, bind your own section to the plugin-local type and assign it, e.g. `UseKafka(o => o.ConnectionRecovery = config.GetSection("ChangeTracking:Kafka:ConnectionRecovery").Get<KafkaConnectionRecoveryOptions>() ?? new())`. The `ChangeTrackingRecoveryKeys` constants and the `ChangeTracking:{Publisher,Subscriber}:ConnectionRecovery` host-bound sections are removed.
