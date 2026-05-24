## ADDED Requirements

### Requirement: Logger factory opt-in on the unified builder
The `IChangeTrackingBuilder` interface SHALL expose a `UseLoggerFactory(ILoggerFactory loggerFactory)` method that stores the factory and forwards it to both the publisher and subscriber builder chains. Calling this method SHALL be optional; when it is not called the system SHALL default to `NullLoggerFactory.Instance` so no log output is produced and existing call-sites require no changes.

#### Scenario: Builder with no logger factory produces no log output
- **WHEN** a caller builds a tracker via `ChangeTrackingBuilder.Build()` without calling `UseLoggerFactory`
- **THEN** the tracker starts without error and no logging infrastructure is required in the host environment

#### Scenario: Builder with explicit logger factory propagates loggers to all background services
- **WHEN** a caller calls `builder.UseLoggerFactory(myFactory)` before `Build()`
- **THEN** `OutboxPublisherService`, `ChangeSubscriber`, and `ChangeTrackingHostedService` all receive loggers created from `myFactory`

### Requirement: Automatic logger factory injection under the .NET Generic Host
When `ServiceCollectionExtensions.AddChangeTracking` is used, the system SHALL automatically resolve `ILoggerFactory` from the DI container and pass it to the builder before constructing the `EntityChangeTracker` singleton. No explicit `UseLoggerFactory` call SHALL be required from the user.

#### Scenario: Host with logging configured gets structured log output automatically
- **WHEN** a host registers logging (e.g., `services.AddLogging()`) and calls `AddChangeTracking`
- **THEN** the `EntityChangeTracker` singleton is built with the host's `ILoggerFactory`, and log events are emitted through the configured providers

#### Scenario: Host without logging does not fail
- **WHEN** `AddChangeTracking` is called in a minimal host that has not registered `ILoggerFactory`
- **THEN** construction succeeds and defaults to `NullLoggerFactory`

### Requirement: Outbox poll-loop errors are logged
When `OutboxPublisherService` catches an exception from `ProcessBatchAsync` in its polling loop, it SHALL log the exception at `Error` level, including the entity type name as a structured property, before waiting for the next poll interval.

#### Scenario: Transient error in poll batch is logged then retried
- **WHEN** `ProcessBatchAsync` throws an exception during a polling cycle
- **THEN** the error is logged at `Error` level with `{EntityType}` and exception details
- **THEN** the service continues polling after the configured interval (the exception is not re-thrown)

### Requirement: Publish retry attempts are logged
When `OutboxPublisherService.PublishWithRetryAsync` catches a publish failure and has remaining retry budget, it SHALL log a `Warning` per retry attempt including entity type, attempt number, and max retries. When all retries are exhausted it SHALL log an `Error` before re-throwing.

#### Scenario: Transient publish failure emits warning per retry
- **WHEN** a publish call fails and `retries < MaxRetryCount`
- **THEN** a `Warning` is logged with `{EntityType}`, `{Attempt}`, and `{MaxRetries}` structured properties

#### Scenario: Exhausted publish retries emit error before re-throw
- **WHEN** a publish call fails and `retries >= MaxRetryCount`
- **THEN** an `Error` is logged with the exception and structured properties before the exception propagates

### Requirement: Subscriber handler retry attempts are logged
When `ChangeSubscriber.InvokeWithRetryAsync` catches a handler failure and has remaining retry budget, it SHALL log a `Warning` per retry including entity type and attempt count. When `SkipOnFailure` is true and all retries are exhausted, it SHALL log an `Error` before silently dropping the message.

#### Scenario: Handler retry emits warning per attempt
- **WHEN** a handler throws and `attempt < MaxRetries`
- **THEN** a `Warning` is logged with `{EntityType}`, `{Attempt}`, and exception details

#### Scenario: SkipOnFailure drop emits error log
- **WHEN** a handler exhausts all retries and `SkipOnFailure` is `true`
- **THEN** an `Error` is logged with entity type and exception before the message is silently dropped

### Requirement: Unknown entity type in envelope is logged
When `ChangeSubscriber.ProcessMessageAsync` receives an envelope whose `EntityType` string cannot be resolved to a CLR type, it SHALL log a `Warning` with the unresolvable type name as a structured property before returning.

#### Scenario: Unresolvable entity type emits warning
- **WHEN** an envelope arrives with an `EntityType` that does not match any loaded assembly type
- **THEN** a `Warning` is logged with `{EntityType}` structured property and processing is skipped

### Requirement: Hosted service lifecycle is logged
`ChangeTrackingHostedService` SHALL log at `Information` level when consumer loops are started and when the service stops gracefully.

#### Scenario: Consumer start is logged per queue
- **WHEN** `StartAsync` is called and at least one consumer queue is registered
- **THEN** an `Information` log is emitted for each consumer loop that is started

#### Scenario: Graceful shutdown is logged
- **WHEN** `StopAsync` completes (including the expected `OperationCanceledException`)
- **THEN** an `Information` log confirms the service has stopped

### Requirement: Builder configuration calls emit structured logs
`ChangeTrackingBuilder` SHALL emit a structured `Information` log for each top-level configuration call made by the caller: `UseOutbox`, `UsePublisher`, `UseSerializer`, `UseCompressor`, `UseRepository`, `UseDeduplicationStore`, `UseMeter`, `UsePublisherOptions`, and `UseSubscriberOptions`. Each log entry SHALL include the registered plugin's CLR type name (or the options class name for option-configurers) as a structured property so that the log stream documents how the tracker was wired up.

#### Scenario: Outbox registration is logged
- **WHEN** a caller invokes `builder.UseOutbox<MyOutbox>(factory)`
- **THEN** an `Information` log is emitted with `{Plugin}` equal to `"MyOutbox"` and a message identifying it as an outbox registration

#### Scenario: Each Use* method emits exactly one log entry
- **WHEN** a caller chains `UseOutbox`, `UsePublisher`, `UseSerializer`, `UseCompressor`, `UseRepository`, `UseDeduplicationStore`, and `UseMeter`
- **THEN** seven distinct `Information` log entries are captured, one per call, each with the corresponding plugin type name

#### Scenario: Null logger factory produces no configuration output
- **WHEN** a caller builds without supplying an `ILoggerFactory` (defaulting to `NullLoggerFactory.Instance`)
- **THEN** no configuration log entries are emitted and the builder still completes successfully

### Requirement: Per-entity configuration is logged
`ChangeTrackingBuilder.ForEntity<TEntity>` SHALL emit an `Information` log naming the configured entity type before invoking the configure delegate, and SHALL emit `Debug` logs for each per-entity plugin override (`UseOutbox`, `UsePublisher`, `UseSerializer`, `UseCompressor`, `UseRepository`, `UseConsumer`, `UseConsumerFactory`, `UseSubscriberOptions`, `OnInsert`/`OnUpdate`/`OnDelete`/`OnChange`) applied inside the delegate.

#### Scenario: ForEntity logs the entity type at Information
- **WHEN** a caller invokes `builder.ForEntity<Order>(b => { /* ... */ })`
- **THEN** an `Information` log is emitted with `{EntityType}` equal to `"Order"`

#### Scenario: Per-entity plugin overrides log at Debug
- **WHEN** the configure delegate calls `b.UseOutbox(...)` and `b.OnInsert("name", handler)`
- **THEN** a `Debug` log is emitted for each override with `{EntityType}` and the override kind (e.g. `{Override}` = `"Outbox"`, `"OnInsert:name"`) as structured properties

### Requirement: Tracker build emits a summary log
When `ChangeTrackingBuilder.Build()` or `BuildAsync()` completes the `BuildInternal` step, the builder SHALL emit a single `Information` log entry summarising the configured tracker. The log entry SHALL include the following structured properties: `{EntityTypes}` (the list of configured entity-type names), `{HasCustomMeter}` (bool), `{HasCustomDeduplicationStore}` (bool), `{HasCustomLoggerFactory}` (bool), and `{Plugins}` containing the registered global outbox, publisher, serializer, and compressor type names (or `"<none>"` when not registered).

#### Scenario: Build summary is emitted once
- **WHEN** `builder.Build()` is called
- **THEN** exactly one `Information` "tracker built" log entry is captured containing the listed structured properties

#### Scenario: Build summary reflects unconfigured plugins
- **WHEN** the builder is built without a global serializer registration
- **THEN** the build summary's `{Plugins}` property contains `Serializer = "<none>"`

### Requirement: Builder default-meter decision is logged
When `ChangeTrackingBuilder.BuildInternal` falls back to a default `RayTreeMeter` (because `UseMeter` was not called) it SHALL emit a `Debug` log indicating that a default meter was created and is owned by the tracker. (The `NullLoggerFactory` fallback path is already covered by the existing "Logger factory opt-in on the unified builder" requirement and is not restated here.)

#### Scenario: Default meter creation is logged
- **WHEN** `Build()` is called without a prior `UseMeter` call
- **THEN** a `Debug` log is emitted stating that a default `RayTreeMeter` was created and is owned by the tracker

### Requirement: Tracker initialization lifecycle is logged
`EntityChangeTracker.InitializeAsync` SHALL log at `Information` level when initialization begins and when it completes successfully, and SHALL log at `Debug` level after each major sub-step: publisher initialization complete, and consumer connections initialized. On failure, an `Error` log SHALL be emitted with the exception before re-throwing.

#### Scenario: Successful initialization emits start and complete logs
- **WHEN** `InitializeAsync` is called and completes without error
- **THEN** an `Information` "tracker initialization started" log is captured followed by an `Information` "tracker initialization completed" log
- **THEN** exactly one `Debug` "publisher initialized" log is captured with `{EntityTypeCount}` structured property, and exactly one `Debug` "consumers initialized" log is captured with `{ConsumerCount}` structured property

#### Scenario: Initialization failure is logged before re-throw
- **WHEN** `InitializeAsync` throws because a plugin's `InitializeAsync` fails
- **THEN** an `Error` log is emitted with the exception details and the failing sub-step before the exception propagates

### Requirement: ChangeTrackingHostedService logs DI startup details
When `ChangeTrackingHostedService.StartAsync` runs (guaranteed one-shot per host instance), it SHALL emit a single `Information` "ChangeTracking starting" log with `{ConfigurationBound}` (bool — captured at `AddChangeTracking` registration time and stored on the hosted service) indicating whether configuration was bound from `IConfiguration`. This is additive to the existing hosted-service lifecycle logs and consolidates DI-registration visibility with the host start event.

#### Scenario: Hosted service startup emits a registration-context log
- **WHEN** the host starts and `ChangeTrackingHostedService.StartAsync` is invoked
- **THEN** exactly one `Information` "ChangeTracking starting" log is emitted with `{ConfigurationBound}` matching whether `AddChangeTracking` received a non-null `IConfiguration`


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
