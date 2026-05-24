## ADDED Requirements

### Requirement: Connection-recovery logs are emitted at documented levels
Every plugin that participates in the `connection-recovery` capability SHALL emit the following structured log entries through its existing runtime-service `ILogger<T>`:

- **First detection of disconnect** (per recovery cycle): `Warning`, with the underlying exception and the structured properties `{Component}` (one of `"rabbitmq.publisher"`, `"rabbitmq.consumer"`, `"kafka.publisher"`, `"kafka.consumer"`, `"postgres.notification"`) and `{Endpoint}` (broker host or LISTEN channel).
- **Each retry attempt**: `Information`, with `{Component}`, `{Endpoint}`, `{AttemptNumber}`, and `{Delay}` (the actually scheduled inter-attempt delay including jitter, in seconds).
- **Successful recovery**: `Information`, with `{Component}`, `{Endpoint}`, `{AttemptCount}` (total attempts including the successful one), and `{Duration}` (wall-clock seconds elapsed).
- **Exhausted attempts**: `Error`, with the most recent exception, `{Component}`, `{Endpoint}`, and `{AttemptCount}`.

Recovery logging SHALL follow the existing logging-placement rule: it lives in the runtime service classes (`RabbitMqPublisher`, `KafkaPublisher`, `KafkaConsumer`, `NotificationBasedPublisher`) which all require a non-null `ILogger<T>` — there SHALL be no `NullLoggerFactory.Instance` fallback inside the recovery path. The documented exception to the logging-placement rule for `RabbitMqConsumer` stands: it has no `ILogger` field, and its recovery SHALL therefore run silently. All `RabbitMqConsumer` recovery telemetry SHALL still be observable through the connection-recovery metric instruments emitted by the shared recovery strategy.

#### Scenario: First disconnect emits Warning with exception
- **WHEN** a participating plugin observes the first disconnect in a recovery cycle
- **THEN** a `Warning` log SHALL be emitted with `{Component}` and `{Endpoint}` structured properties and the underlying exception attached.

#### Scenario: Retry attempt emits Information with delay
- **WHEN** the recovery strategy is about to schedule the Nth retry attempt
- **THEN** an `Information` log SHALL be emitted with `{AttemptNumber} = N` and `{Delay}` equal to the actually-scheduled delay (including jitter) in seconds.

#### Scenario: Successful recovery emits Information with duration
- **WHEN** the recovery strategy returns successfully
- **THEN** an `Information` log SHALL be emitted with `{AttemptCount}` and `{Duration}` (wall-clock seconds elapsed since the first detection in this cycle).

#### Scenario: Exhausted attempts emit Error before propagating
- **WHEN** the recovery strategy exceeds `MaxAttempts`
- **THEN** an `Error` log SHALL be emitted with the most recent exception and `{AttemptCount}` immediately before the cycle terminates.

#### Scenario: RabbitMqConsumer recovery is silent on logs but observable via metrics
- **WHEN** a recovery cycle runs for `RabbitMqConsumer` (which has no `ILogger` field)
- **THEN** no log entries SHALL be emitted from the consumer
- **AND** the `raytree.connection.disconnects` and `raytree.connection.recoveries` counters SHALL still record the cycle with `component = "rabbitmq.consumer"`.
