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
