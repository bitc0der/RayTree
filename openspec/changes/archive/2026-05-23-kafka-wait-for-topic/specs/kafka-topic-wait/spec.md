## ADDED Requirements

### Requirement: Opt-in topic wait flag
The Kafka publisher and consumer SHALL expose a `WaitForTopic` boolean option (default `false`) that, when `true`, causes `InitializeAsync` to wait for the configured Kafka topic to become available on the broker before completing. When `false`, `InitializeAsync` SHALL NOT contact the broker for topic-existence purposes and the missing-topic behaviour SHALL match the pre-change behaviour of the underlying Confluent.Kafka client (publisher: the first `ProduceAsync` raises `UnknownTopicOrPart`; consumer: `Consume` returns no messages until the topic is created and librdkafka logs `UnknownTopicOrPart` warnings internally).

#### Scenario: Default behaviour is unchanged on publisher
- **WHEN** `WaitForTopic` is not set (or set to `false`) on `KafkaPublisherOptions`
- **THEN** `InitializeAsync` SHALL NOT issue any pre-flight metadata probe
- **AND** the first subsequent `ProduceAsync` against a non-existent topic SHALL raise a `KafkaException` whose `Error.Code` equals `ErrorCode.UnknownTopicOrPart` (unchanged from current behaviour).

#### Scenario: Default behaviour is unchanged on consumer
- **WHEN** `WaitForTopic` is not set (or set to `false`) on `KafkaConsumerOptions`
- **THEN** `InitializeAsync` SHALL NOT issue any pre-flight metadata probe
- **AND** subsequent `Consume` calls against a non-existent topic SHALL continue to return null/empty results without throwing (unchanged from current behaviour).

#### Scenario: Opt-in enables wait loop
- **WHEN** `WaitForTopic = true` is set on either options class
- **THEN** `InitializeAsync` SHALL probe the configured `Topic` with `IAdminClient.GetMetadata` and retry while the response indicates the topic is not yet available, as defined by **Requirement: Retry conditions**.

### Requirement: Publisher waits for externally-owned topic
When `KafkaPublisherOptions.WaitForTopic = true`, `KafkaPublisher.InitializeAsync` SHALL probe the configured `Topic` and complete successfully only after the metadata response contains an entry for that topic with `Error.Code == ErrorCode.NoError`. The wait SHALL occur before any internal `IProducer` is built or returned, AND before any path that lazily constructs the producer (e.g. `PublishAsync`) is permitted to proceed.

#### Scenario: Topic appears after one or more probe attempts
- **WHEN** the topic named in `Topic` does not exist at the moment `InitializeAsync` is called but is created by another service shortly after
- **THEN** the publisher SHALL retry the metadata call at intervals of `TopicWaitInterval`
- **AND** SHALL complete `InitializeAsync` successfully once the metadata response reports the topic
- **AND** SHALL log the first miss at `Information` level and the eventual recovery at `Information` level.

#### Scenario: Topic already exists
- **WHEN** the topic exists at the moment `InitializeAsync` is called and `WaitForTopic = true`
- **THEN** the first probe SHALL succeed and `InitializeAsync` SHALL complete without emitting any topic-wait log entries at `Information` level or above.

### Requirement: Consumer waits for externally-owned topic
When `KafkaConsumerOptions.WaitForTopic = true`, `KafkaConsumer.InitializeAsync` SHALL probe the configured `Topic` and complete successfully only after the metadata response contains an entry for that topic with `Error.Code == ErrorCode.NoError`. The wait SHALL occur before the internal `IConsumer` is built AND before `Subscribe` is called AND before any other broker-touching consumer call.

#### Scenario: Topic appears after one or more probe attempts
- **WHEN** the topic named in `Topic` does not exist when `InitializeAsync` is called
- **AND** another service creates it shortly after
- **THEN** the consumer SHALL retry the metadata call at intervals of `TopicWaitInterval`
- **AND** SHALL proceed to `Subscribe` once the metadata response reports the topic
- **AND** SHALL log the first miss at `Information` level and the eventual recovery at `Information` level.

#### Scenario: Topic already exists
- **WHEN** the topic exists at the moment `InitializeAsync` is called and `WaitForTopic = true`
- **THEN** the first probe SHALL succeed and `InitializeAsync` SHALL proceed to `Subscribe` without emitting any topic-wait log entries at `Information` level or above.

### Requirement: Retry conditions
The topic wait loop SHALL retry when the metadata response indicates the topic is not yet available on the broker, OR when the metadata call throws a transient transport-level `KafkaException` (broker briefly unreachable during startup ordering). "Retryable" SHALL be defined as any of:

1. The `Metadata.Topics` collection contains no entry for the requested topic name.
2. The entry for the requested topic has `Error.Code == ErrorCode.UnknownTopicOrPart`.
3. The entry for the requested topic has `Error.Code == ErrorCode.LeaderNotAvailable` (a transient state during fresh-cluster bootstrap and partition leader election).
4. `GetMetadata` throws a `KafkaException` with `Error.IsFatal == false` AND `Error.Code` in {`Local_Transport`, `Local_AllBrokersDown`, `Local_Resolve`, `Local_TimedOut`}. This covers the dominant microservice startup-ordering case where the broker pod has not yet finished starting.

All other broker error codes, all fatal `KafkaException` instances (where `Error.IsFatal == true`), and `OperationCanceledException` SHALL propagate immediately without retry.

#### Scenario: Empty Topics collection is retryable
- **WHEN** the metadata response contains no entry for the requested topic name
- **THEN** the probe SHALL treat this as a miss and retry after `TopicWaitInterval`.

#### Scenario: UnknownTopicOrPart is retryable
- **WHEN** the per-topic `Error.Code` equals `ErrorCode.UnknownTopicOrPart`
- **THEN** the probe SHALL treat this as a miss and retry after `TopicWaitInterval`.

#### Scenario: LeaderNotAvailable is retryable
- **WHEN** the per-topic `Error.Code` equals `ErrorCode.LeaderNotAvailable` (e.g. the topic is being created and partition leaders have not yet been elected)
- **THEN** the probe SHALL treat this as a miss and retry after `TopicWaitInterval`.

#### Scenario: Authorization failure propagates immediately
- **WHEN** the broker reports `ErrorCode.TopicAuthorizationFailed` (or any per-topic error code not enumerated above)
- **THEN** `InitializeAsync` SHALL propagate the resulting `KafkaException` on the first attempt without further retries.

#### Scenario: Fatal Kafka exception propagates immediately
- **WHEN** `GetMetadata` throws a `KafkaException` whose `Error.IsFatal` is `true`
- **THEN** the resulting exception SHALL propagate without retry.

#### Scenario: Transient transport error is retryable
- **WHEN** `GetMetadata` throws a `KafkaException` with `Error.IsFatal == false` and `Error.Code` in {`Local_Transport`, `Local_AllBrokersDown`, `Local_Resolve`, `Local_TimedOut`} (broker not yet reachable / DNS not yet resolved during cluster startup)
- **THEN** the probe SHALL treat this as a miss and retry after `TopicWaitInterval`
- **AND** SHALL log the first miss at `Information` and recovery at `Information` per the standard logging contract.

### Requirement: Retry interval and timeout configuration
The publisher and consumer options SHALL expose `TopicWaitInterval` (TimeSpan, default `5 seconds`) and `TopicWaitTimeout` (TimeSpan?, default `null`). When `TopicWaitTimeout` is non-null, the wait loop SHALL stop and rethrow the last `KafkaException` produced by a retryable response once the elapsed time exceeds the timeout. When no `KafkaException` is available (e.g. all responses came back as empty `Topics` collections), the wait loop SHALL throw a `KafkaException` synthesised from `ErrorCode.UnknownTopicOrPart` describing the topic name.

Both values SHALL be validated when the wait loop is entered. If `TopicWaitInterval <= TimeSpan.Zero`, OR if `TopicWaitTimeout` is non-null and `<= TimeSpan.Zero`, the probe entry point SHALL throw `ArgumentOutOfRangeException` before issuing any metadata call.

#### Scenario: Custom interval is honoured
- **WHEN** `TopicWaitInterval = TimeSpan.FromMilliseconds(500)` is set
- **AND** the broker is reachable and responsive
- **THEN** consecutive metadata probes against a missing topic SHALL be separated by approximately 500 milliseconds (within a tolerance of 250 ms to allow for broker round-trip time and scheduler jitter).

#### Scenario: Timeout exhaustion surfaces the underlying error
- **WHEN** `TopicWaitTimeout = TimeSpan.FromSeconds(10)` is set
- **AND** the topic has not appeared after 10 seconds of probing
- **THEN** `InitializeAsync` SHALL throw a `KafkaException` whose `Error.Code` describes the most recent retryable response (or `UnknownTopicOrPart` if all responses were empty-Topics).

#### Scenario: Null timeout means no ceiling
- **WHEN** `TopicWaitTimeout = null`
- **THEN** the wait loop SHALL continue indefinitely until either the topic appears or the cancellation token is cancelled.

#### Scenario: Non-positive interval is rejected
- **WHEN** `TopicWaitInterval = TimeSpan.Zero` (or any negative TimeSpan) is set
- **AND** the probe entry point is invoked
- **THEN** it SHALL throw `ArgumentOutOfRangeException` without issuing any metadata call.

#### Scenario: Non-positive timeout is rejected
- **WHEN** `TopicWaitTimeout = TimeSpan.Zero` (or any negative TimeSpan) is set
- **AND** the probe entry point is invoked
- **THEN** it SHALL throw `ArgumentOutOfRangeException` without issuing any metadata call.

### Requirement: Cancellation token cancels the wait
The wait loop SHALL observe the `CancellationToken` passed into `InitializeAsync`. Cancellation SHALL be observed at the next of: (a) the inter-attempt `Task.Delay` boundary, or (b) the return of the in-flight `GetMetadata` call. Because `IAdminClient.GetMetadata` is a synchronous, blocking call that does not accept a managed cancellation token, observation MAY be delayed by up to a small fixed per-call metadata timeout (~1 second, decoupled from `TopicWaitInterval`) while a metadata call is in flight. When observed, the loop SHALL throw `OperationCanceledException`.

#### Scenario: Cancellation during the inter-attempt delay
- **WHEN** the cancellation token is cancelled while the wait loop is sleeping between attempts
- **THEN** `InitializeAsync` SHALL throw `OperationCanceledException` promptly, without issuing another metadata call.

#### Scenario: Cancellation before the first attempt
- **WHEN** the cancellation token is already cancelled at the moment the probe entry point is invoked
- **THEN** the probe SHALL throw `OperationCanceledException` without issuing any metadata call.

#### Scenario: Cancellation during an in-flight metadata call is observed within ~1 second
- **WHEN** the cancellation token is cancelled while a `GetMetadata` call is in flight
- **THEN** `InitializeAsync` SHALL throw `OperationCanceledException` no later than the end of the current metadata call (bounded by the implementation's fixed per-call metadata timeout, ~1 second, decoupled from `TopicWaitInterval`).

### Requirement: Probe uses a disposable admin client
Each invocation of the wait loop SHALL build a dedicated `IAdminClient`, use it for the duration of the wait, and dispose it before returning control to the caller. The persistent `IProducer` / `IConsumer` held by the publisher/consumer SHALL be created only after the probe succeeds.

#### Scenario: Admin client is disposed after success
- **WHEN** the wait loop completes successfully
- **THEN** the admin client used for probing SHALL be disposed before `InitializeAsync` returns.

#### Scenario: Admin client is disposed after failure
- **WHEN** the wait loop throws (timeout, cancellation, or non-retryable broker error)
- **THEN** the admin client used for probing SHALL be disposed before the exception is rethrown.

### Requirement: Logging of topic wait
The plugin SHALL emit the following log entries when `WaitForTopic = true`:

- First retryable response per probed topic: `Information`, with the topic name, interval, and timeout (or `<none>`).
- Subsequent retryable responses for the same topic: `Debug`.
- Recovery (probe succeeds after one or more misses): `Information`.
- Timeout exhaustion: `Error`, immediately before rethrowing.

For the publisher, log entries SHALL be emitted via the `ILoggerFactory` passed to `KafkaPublisher` (when `null`, falls through to `NullLoggerFactory.Instance` → silent). For the consumer, log entries SHALL be emitted via the `ILoggerFactory` passed to `KafkaConsumer`. The public builder extensions (`KafkaBuilderExtensions.UseKafka` for the publisher and `KafkaSubscriberExtensions.UseKafka` for the consumer) SHALL each expose an optional `ILoggerFactory?` parameter so callers using the documented fluent API can route probe logs through their host's logging infrastructure.

#### Scenario: First miss logged at Information
- **WHEN** the first metadata probe for a topic returns a retryable response
- **THEN** an `Information`-level log SHALL be emitted indicating the consumer/publisher is waiting for that topic by name.

#### Scenario: Recovery logged at Information
- **WHEN** a metadata probe succeeds after at least one prior retryable response
- **THEN** an `Information`-level log SHALL be emitted indicating the topic became available.

#### Scenario: Subsequent misses logged at Debug
- **WHEN** the second and subsequent metadata probes for the same topic return retryable responses
- **THEN** each SHALL be logged at `Debug` level (not `Information`) to avoid log spam during long waits.

#### Scenario: Timeout exhaustion logged at Error
- **WHEN** `TopicWaitTimeout` is exceeded and the wait loop is about to rethrow
- **THEN** an `Error`-level log SHALL be emitted immediately before the throw, identifying the topic and elapsed time.

#### Scenario: Silent publisher when no logger factory supplied
- **WHEN** `KafkaPublisher` is constructed without an `ILoggerFactory` (legacy call shape) and `WaitForTopic = true`
- **THEN** the probe SHALL still run correctly but SHALL produce no log output.

#### Scenario: Builder-supplied logger factory is honoured on the consumer
- **WHEN** a consumer is constructed via `IEntityBuilder<TEntity>.UseKafka(configure, loggerFactory)` with a non-null `loggerFactory`
- **AND** `WaitForTopic = true`
- **THEN** the probe's log entries SHALL be emitted through the supplied `loggerFactory` (not through `NullLoggerFactory.Instance`).
