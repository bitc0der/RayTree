## ADDED Requirements

### Requirement: Connection recovery options shape
`RayTree.Core` SHALL expose a public `ConnectionRecoveryOptions` record with fields `Enabled` (bool, default `true`), `InitialDelay` (TimeSpan, default `1s`), `MaxDelay` (TimeSpan, default `30s`), `Factor` (double, default `2.0`), `JitterFraction` (double, default `0.2`), and `MaxAttempts` (int?, default `null` — unlimited). The record SHALL validate its values at construction: `InitialDelay > TimeSpan.Zero`, `MaxDelay >= InitialDelay`, `Factor >= 1.0`, `0 <= JitterFraction <= 1`, and `MaxAttempts == null || MaxAttempts > 0`. Invalid values SHALL throw `ArgumentOutOfRangeException`.

#### Scenario: Defaults match documented values
- **WHEN** `new ConnectionRecoveryOptions()` is constructed with no overrides
- **THEN** the field values SHALL be exactly `Enabled = true`, `InitialDelay = 1s`, `MaxDelay = 30s`, `Factor = 2.0`, `JitterFraction = 0.2`, `MaxAttempts = null`.

#### Scenario: Invalid factor is rejected
- **WHEN** `Factor = 0.5` is supplied
- **THEN** construction SHALL throw `ArgumentOutOfRangeException` with `paramName = "Factor"`.

#### Scenario: Disabled options short-circuit recovery
- **WHEN** `Enabled = false` AND a Postgres or Kafka plugin observes a connection-fault exception
- **THEN** the plugin SHALL surface the exception on the next caller-facing call (or, for the Kafka consumer poll thread, on the next iteration) — no rebuild attempt SHALL be made.

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
- **THEN** the existing `Information` "LISTEN connection on {ChannelName} recovered" log SHALL be emitted (unchanged from current behaviour), accompanied by the `raytree.connection.recoveries{outcome="succeeded"}` metric and duration recording.

### Requirement: Kafka publisher rebuilds on fatal error
`KafkaPublisher` SHALL register `IProducerBuilder.SetErrorHandler` during producer construction. When the handler receives an error with `Error.IsFatal = true`, the publisher SHALL dispose the current `IProducer`, null out the cached reference, and emit the disconnect metric. The next `PublishAsync` call SHALL re-enter the existing lazy `GetProducerAsync` build path — which re-runs the `WaitForTopic` probe when enabled — bounded by `KafkaPublisherOptions.ConnectionRecovery`. Non-fatal errors SHALL NOT trigger rebuild; librdkafka recovers those internally.

#### Scenario: Fatal error disposes the producer
- **WHEN** the error handler receives an error with `Error.IsFatal = true`
- **THEN** the cached `IProducer` SHALL be disposed and the cached reference SHALL be set to `null`.

#### Scenario: Next publish rebuilds via existing path
- **WHEN** a subsequent `PublishAsync` is invoked after a fatal-error dispose
- **THEN** the existing `GetProducerAsync` path SHALL build a fresh producer
- **AND** the probe SHALL re-run when `WaitForTopic = true`.

#### Scenario: Non-fatal error is ignored
- **WHEN** the error handler receives a non-fatal error
- **THEN** the publisher SHALL log at `Warning` and SHALL NOT dispose the producer.

### Requirement: Kafka consumer rebuilds on fatal error on the poll thread
`KafkaConsumer`'s dedicated poll thread SHALL catch `KafkaException` thrown from `Consume` whose `Error.IsFatal == true`, dispose the current `IConsumer` on that thread, and run an inline exponential-backoff rebuild loop bounded by `KafkaConsumerOptions.ConnectionRecovery`. The rebuild SHALL build a new `IConsumer`, re-run the topic-wait probe (when `WaitForTopic = true`), call `Subscribe`, and resume polling. The internal post-handler channel for deferred-ack messages SHALL be drained and any pending `Commit`/`SeekBack` actions referencing the disposed consumer SHALL be discarded — the broker will redeliver via the standard at-least-once contract once the new consumer joins the group.

#### Scenario: Fatal exception on poll thread triggers rebuild
- **WHEN** `Consume` throws `KafkaException` with `Error.IsFatal = true`
- **THEN** the poll thread SHALL dispose the current consumer and rebuild a new one on the same thread before resuming polling.

#### Scenario: Pending deferred ack against disposed consumer is dropped
- **WHEN** a handler completes for a `MessageEnvelope` whose `ConsumeResult` was issued by the now-disposed consumer
- **THEN** the post-handler drain SHALL detect the mismatch and SHALL NOT invoke `Commit` or `Seek` against the new consumer.

#### Scenario: Non-fatal Consume errors are unchanged
- **WHEN** `Consume` returns a `ConsumeResult` carrying a non-fatal error (e.g. partition-level transient)
- **THEN** the existing behaviour SHALL apply — no rebuild, librdkafka recovers internally.

### Requirement: RabbitMQ recovery is observed, not implemented
The RabbitMQ publisher and consumer SHALL rely on `RabbitMQ.Client.AutomaticRecoveryEnabled = true` and `TopologyRecoveryEnabled = true` (the library defaults) for the actual recovery mechanism. RayTree SHALL NOT disable these flags. The publisher and consumer SHALL subscribe to the SDK's `ConnectionShutdownAsync`, `RecoverySucceededAsync`, and `ConnectionRecoveryErrorAsync` events to emit the `raytree.connection.disconnects` / `raytree.connection.recoveries` / `raytree.connection.recovery.duration` metrics and the documented log entries.

#### Scenario: ConnectionShutdownAsync records a disconnect
- **WHEN** the `RabbitMqPublisher`'s `IConnection` raises `ConnectionShutdownAsync` with `Initiator != Application`
- **THEN** `raytree.connection.disconnects` SHALL be incremented with `component = "rabbitmq.publisher"` and `endpoint = "{HostName}:{Port}"`
- **AND** a `Warning` log SHALL be emitted with the shutdown reason.

#### Scenario: RecoverySucceededAsync records a recovery
- **WHEN** the `IConnection` raises `RecoverySucceededAsync`
- **THEN** `raytree.connection.recoveries{outcome="succeeded"}` SHALL be incremented
- **AND** `raytree.connection.recovery.duration` SHALL record the wall-clock seconds elapsed since the most recent `ConnectionShutdownAsync` for this component
- **AND** an `Information` log SHALL be emitted.

#### Scenario: ConnectionRecoveryErrorAsync logs at Information
- **WHEN** the `IConnection` raises `ConnectionRecoveryErrorAsync` (a single library-internal retry failed; the library will keep trying)
- **THEN** an `Information` log SHALL be emitted with the exception
- **AND** no metric SHALL be incremented (only the *outcome* of the overall recovery cycle is metered, not per-internal-attempt).

#### Scenario: Application-initiated shutdown is not counted
- **WHEN** `RabbitMqPublisher.DisposeAsync` is invoked and the resulting shutdown event has `Initiator = Application`
- **THEN** no disconnect metric SHALL be recorded and no warning SHALL be logged.

### Requirement: Outbox connection-fault observability
`IOutbox` SHALL expose three default-implemented members so consumers of an arbitrary `IOutbox` can observe transient connection faults without retry code:

- `bool IsConnectionFault(Exception ex)` — default `false`. Concrete implementations override to classify connection-level exceptions (network drop, broker shutdown, transient transport) versus application-level exceptions (constraint violation, malformed SQL, business-rule rejection).
- `string? ConnectionComponent { get; }` — default `null`. Returns the `component` tag value to use for connection metrics, or `null` when the outbox has no observable external connection (e.g. `InMemoryOutbox`).
- `string? ConnectionEndpoint { get; }` — default `null`. Returns the `endpoint` tag value.

`PostgreSqlOutbox<TEntity>` SHALL override these: `IsConnectionFault` returns `true` for `NpgsqlException { IsTransient: true }`, `NpgsqlException` with `SocketException`/`IOException` inner, `PostgresException` with `SqlState` in `{"57P01", "57P02", "57P03", "08000", "08003", "08006", "08001", "08004", "08007"}` (admin/crash shutdown, cannot_connect_now, and the `08xxx` connection_exception family), and `ObjectDisposedException`. `ConnectionComponent` returns `"postgres.outbox"`. `ConnectionEndpoint` returns `"{Host}:{Port}"` parsed once from the connection string.

The classifier used by `PostgreSqlOutbox` SHALL be the same set as `NotificationBasedPublisher`'s classifier — both delegate to an internal `static bool PostgresFault.IsConnectionFault(Exception)` so the two stay in sync.

`OutboxPublisherService.ProcessBatchAsync`'s existing batch-error catch block SHALL consult `_outbox.IsConnectionFault(ex)` and `_outbox.ConnectionComponent`. When the classifier returns `true` AND `ConnectionComponent` is non-null:

1. Emit `raytree.connection.disconnects{component=ConnectionComponent, endpoint=ConnectionEndpoint}` exactly once per transition into the unhealthy state (tracked via a per-service `_outboxUnhealthy` flag).
2. Log the exception at `Warning` instead of `Error`, with `{Component}` and `{Endpoint}` structured properties.
3. On the first subsequent batch that completes without throwing, emit `raytree.connection.recoveries{outcome="succeeded"}` plus `raytree.connection.recovery.duration` (wall-clock seconds since the first failure), log `Information` "outbox connection recovered", and clear `_outboxUnhealthy`.

When the classifier returns `false` OR `ConnectionComponent` is `null`, the existing `Error` log path is preserved unchanged. No retry code is added — the existing polling cadence is the retry.

`NotificationBasedPublisher.FallbackPollingLoopAsync` SHALL apply the same pattern: in the existing `ProcessUnpublishedChangesAsync` per-outbox iteration, when an `IOutbox` call throws and `_outbox.IsConnectionFault(ex)` returns `true` AND `_outbox.ConnectionComponent` is non-null, emit the disconnect metric and log `Warning`; on next success per outbox, emit the recovery metric. Per-outbox `_unhealthy` state is tracked in a `ConcurrentDictionary<Type, bool>` keyed by entity type.

#### Scenario: Outbox publisher disconnect metric is emitted once per transition
- **WHEN** `OutboxPublisherService.ProcessBatchAsync` catches a connection-fault exception for the first time after a healthy period
- **THEN** `raytree.connection.disconnects{component="postgres.outbox", endpoint="…"}` SHALL be incremented by 1
- **AND** subsequent consecutive failed batches SHALL NOT increment the counter again (the service is already unhealthy).

#### Scenario: Outbox publisher log demotion on connection fault
- **WHEN** `OutboxPublisherService.ProcessBatchAsync` catches an exception and `_outbox.IsConnectionFault(ex)` returns `true`
- **THEN** the failure SHALL be logged at `Warning` (not the usual `Error`) with `{Component}` and `{Endpoint}` structured properties.

#### Scenario: Outbox publisher recovery metric is emitted on first success after failure
- **WHEN** `OutboxPublisherService.ProcessBatchAsync` completes without throwing AND the service was previously unhealthy
- **THEN** `raytree.connection.recoveries{outcome="succeeded"}` SHALL be incremented by 1
- **AND** `raytree.connection.recovery.duration` SHALL record the wall-clock seconds elapsed since the first failure
- **AND** an `Information` log "outbox connection recovered" SHALL be emitted with `{Duration}`
- **AND** the internal `_outboxUnhealthy` flag SHALL be cleared.

#### Scenario: Non-connection-fault exception preserves existing Error log
- **WHEN** `OutboxPublisherService.ProcessBatchAsync` catches an exception for which `_outbox.IsConnectionFault(ex)` returns `false`
- **THEN** the existing `Error` log path SHALL be unchanged
- **AND** no connection metric SHALL be emitted.

#### Scenario: Outbox without ConnectionComponent is unobserved
- **WHEN** an `IOutbox` implementation leaves `ConnectionComponent` at its default `null` (e.g. `InMemoryOutbox`)
- **THEN** `OutboxPublisherService` SHALL fall through to the existing `Error` log path even when `IsConnectionFault` is true
- **AND** no connection metric SHALL be emitted.

#### Scenario: NotificationBasedPublisher fallback polling emits per-outbox metrics
- **WHEN** `NotificationBasedPublisher.ProcessUnpublishedChangesAsync` calls into an `IOutbox` whose `IsConnectionFault(ex)` returns `true` for the thrown exception
- **THEN** the disconnect/recovery metric and `Warning`/`Information` log pattern SHALL be applied per outbox (keyed by entity type) using the same transition semantics as `OutboxPublisherService`.

#### Scenario: Write-path exceptions still propagate to the caller
- **WHEN** a caller invokes `EntityChangeTracker.TrackInsertAsync` and the underlying `PostgreSqlOutbox.WriteAsync` throws a connection-fault exception
- **THEN** the exception SHALL propagate to the caller unchanged
- **AND** no automatic retry SHALL be performed by the library at the write path (the caller's transaction context owns retry semantics).

### Requirement: Recovery options exposure on plugin options classes
`NotificationBasedPublisherOptions`, `KafkaPublisherOptions`, and `KafkaConsumerOptions` SHALL each expose a `ConnectionRecovery` property of type `ConnectionRecoveryOptions` initialised to `new ConnectionRecoveryOptions()`. `RabbitMqPublisherOptions` and `RabbitMqConsumerOptions` SHALL NOT expose this property — RabbitMQ recovery is owned by the SDK and is not configurable through RayTree.

#### Scenario: Default property value is enabled
- **WHEN** any of the three listed options classes is constructed via its parameterless constructor
- **THEN** `ConnectionRecovery.Enabled` SHALL be `true` and the other fields SHALL match the documented defaults.

#### Scenario: RabbitMQ options do not expose ConnectionRecovery
- **WHEN** a caller inspects `RabbitMqPublisherOptions` / `RabbitMqConsumerOptions` via reflection or autocomplete
- **THEN** no `ConnectionRecovery` property SHALL be present
- **AND** the SDK's recovery options (e.g. `NetworkRecoveryInterval`) remain accessible through the underlying `ConnectionFactory` only if the caller constructs one explicitly.

### Requirement: Recovery options bound from configuration
`RayTree.Hosting.AddChangeTracking` SHALL bind `ConnectionRecoveryOptions` instances from configuration sections `ChangeTracking:Publisher:ConnectionRecovery` and `ChangeTracking:Subscriber:ConnectionRecovery`. The bound values SHALL be applied as the default for any plugin whose own `ConnectionRecovery` property is unchanged from the parameterless-constructor default. Explicit per-plugin overrides SHALL win.

#### Scenario: Bound section sets the default
- **WHEN** `appsettings.json` contains `"ChangeTracking:Publisher:ConnectionRecovery": { "MaxAttempts": 10 }`
- **AND** a Kafka publisher is registered without an explicit `ConnectionRecovery` override
- **THEN** that publisher's reconnect loop SHALL be bounded to 10 attempts.

#### Scenario: Per-plugin override wins
- **WHEN** the same configuration is set AND a Kafka publisher is registered with `UseKafka(o => o.ConnectionRecovery = new ConnectionRecoveryOptions { MaxAttempts = 3 })`
- **THEN** the Kafka publisher SHALL use 3 attempts; other publishers SHALL use 10.

### Requirement: Recovery logs are emitted at documented levels
Each plugin that participates in connection recovery (whether it implements the recovery itself or merely observes the SDK) SHALL emit the following log entries through its runtime-service `ILogger<T>`:

- **First detection of disconnect per recovery cycle**: `Warning`, with the underlying exception, `{Component}`, and `{Endpoint}`.
- **Each retry attempt** (Postgres and Kafka only — the two plugins owning their own retry loop): `Information`, with `{Component}`, `{Endpoint}`, `{AttemptNumber}`, and `{Delay}` (the actually scheduled delay including jitter, in seconds).
- **Successful recovery**: `Information`, with `{Component}`, `{Endpoint}`, `{AttemptCount}` (for Postgres/Kafka — the count of attempts), and `{Duration}` (wall-clock seconds elapsed since the first detection in this cycle).
- **Exhausted attempts** (Postgres and Kafka only): `Error`, with the most recent exception, `{Component}`, `{Endpoint}`, and `{AttemptCount}`.

`RabbitMqConsumer` has no `ILogger` field (existing exception to the logging-placement rule). Its disconnects and recoveries SHALL still be observable via the metric instruments but SHALL NOT produce log entries.

#### Scenario: First disconnect emits Warning with exception
- **WHEN** any participating plugin observes the first disconnect of a recovery cycle (Kafka error handler fires, Postgres `WaitAsync` throws a connection fault, RabbitMQ `ConnectionShutdownAsync` fires non-application)
- **THEN** a `Warning` log SHALL be emitted with `{Component}` and `{Endpoint}` structured properties and the underlying exception attached (where available).

#### Scenario: Retry attempt emits Information with delay
- **WHEN** Postgres or Kafka schedules the Nth retry attempt
- **THEN** an `Information` log SHALL be emitted with `{AttemptNumber} = N` and `{Delay}` equal to the actually-scheduled delay (including jitter) in seconds.

#### Scenario: RabbitMqConsumer recovery is silent on logs but observable via metrics
- **WHEN** a recovery cycle runs for `RabbitMqConsumer`
- **THEN** no log entries SHALL be emitted from the consumer
- **AND** `raytree.connection.disconnects` and `raytree.connection.recoveries` with `component = "rabbitmq.consumer"` SHALL be recorded as usual.
