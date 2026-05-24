## ADDED Requirements

### Requirement: Connection-recovery logs are emitted at documented levels
Every connection-bearing plugin SHALL emit the following log entries through its existing runtime-service `ILogger<T>`:

- **First detection of disconnect per recovery cycle**: `Warning`, with the underlying exception (where available), `{Component}` (one of `"rabbitmq.publisher"`, `"rabbitmq.consumer"`, `"kafka.publisher"`, `"kafka.consumer"`, `"postgres.notification"`, `"postgres.outbox"`), and `{Endpoint}`.
- **Each retry attempt** (Postgres and Kafka only — the two plugins owning their own retry loop): `Information`, with `{Component}`, `{Endpoint}`, `{AttemptNumber}`, and `{Delay}` (the actually scheduled inter-attempt delay including jitter, in seconds).
- **Successful recovery**: `Information`, with `{Component}`, `{Endpoint}`, `{AttemptCount}` (where applicable — Postgres/Kafka), and `{Duration}` (wall-clock seconds elapsed since first detection).
- **Exhausted attempts** (Postgres and Kafka only): `Error`, with the most recent exception, `{Component}`, `{Endpoint}`, and `{AttemptCount}`.

Recovery logging SHALL follow the existing logging-placement rule: it lives in the runtime service classes (`RabbitMqPublisher`, `KafkaPublisher`, `KafkaConsumer`, `NotificationBasedPublisher`), each of which already requires a non-null `ILogger<T>`. The documented exception for `RabbitMqConsumer` stands — it has no `ILogger` field, and its recovery events SHALL therefore not produce log entries (but remain observable via the metric instruments emitted by the publisher-side shutdown handler is independent; consumer-side observation relies on metrics alone).

#### Scenario: First disconnect emits Warning with exception
- **WHEN** a participating plugin observes the first disconnect of a recovery cycle
- **THEN** a `Warning` log SHALL be emitted with `{Component}` and `{Endpoint}` structured properties and the underlying exception attached (where available).

#### Scenario: Retry attempt emits Information with delay
- **WHEN** Postgres or Kafka schedules the Nth retry attempt
- **THEN** an `Information` log SHALL be emitted with `{AttemptNumber} = N` and `{Delay}` equal to the actually-scheduled delay (including jitter) in seconds.

#### Scenario: Successful recovery emits Information with duration
- **WHEN** any participating plugin observes a successful recovery
- **THEN** an `Information` log SHALL be emitted with `{Duration}` (wall-clock seconds elapsed since first detection) and, for Postgres/Kafka, `{AttemptCount}`.

#### Scenario: Exhausted attempts emit Error before propagating
- **WHEN** Postgres or Kafka exceeds `ConnectionRecovery.MaxAttempts`
- **THEN** an `Error` log SHALL be emitted with the most recent exception and `{AttemptCount}` immediately before the cycle terminates.

#### Scenario: RabbitMqConsumer recovery is silent on logs but observable via metrics
- **WHEN** a recovery cycle runs for `RabbitMqConsumer` (which has no `ILogger` field)
- **THEN** no log entries SHALL be emitted from the consumer
- **AND** the `raytree.connection.disconnects` and `raytree.connection.recoveries` counters SHALL still record the cycle with `component = "rabbitmq.consumer"`.

### Requirement: Outbox polling-loop log level is demoted on connection fault
`OutboxPublisherService.ProcessBatchAsync` and `NotificationBasedPublisher.FallbackPollingLoopAsync` SHALL, when their existing batch-error catch block traps an exception classified by `IOutbox.IsConnectionFault(ex) == true` AND `IOutbox.ConnectionComponent` is non-null, log the exception at `Warning` (not the existing `Error`) with `{Component}` and `{Endpoint}` structured properties. All other exceptions SHALL continue to log at `Error` unchanged.

This demotion exists because a transient Postgres outage is observable at the disconnect/recovery metric layer; emitting `Error` per failed batch on top of the metric inflates the apparent severity and noise of a recoverable condition. Operators who want to alert on outbox-publisher unavailability SHALL alert on `raytree.connection.disconnects{component="postgres.outbox"}` or `raytree.connection.state{component="postgres.outbox"} = 0`, not on the log stream.

#### Scenario: Connection-fault batch error is logged at Warning
- **WHEN** `OutboxPublisherService.ProcessBatchAsync` catches an exception classified as a connection fault by the outbox
- **THEN** the failure SHALL be logged at `Warning` with `{Component}` and `{Endpoint}` structured properties (not the previous `Error`).

#### Scenario: Non-connection-fault batch error preserves Error log
- **WHEN** `OutboxPublisherService.ProcessBatchAsync` catches an exception NOT classified as a connection fault
- **THEN** the failure SHALL be logged at `Error` exactly as before.

#### Scenario: Recovery emits a single Information log
- **WHEN** the polling loop completes a batch successfully after one or more connection-fault failures
- **THEN** exactly one `Information` log "outbox connection recovered" SHALL be emitted with `{Duration}` and `{Component}` structured properties.
