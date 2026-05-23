## ADDED Requirements

### Requirement: Opt-in topic wait flag
The Kafka publisher and consumer SHALL expose a `WaitForTopic` boolean option (default `false`) that, when `true`, causes `InitializeAsync` to wait for the configured Kafka topic to appear on the broker instead of failing immediately on `UnknownTopicOrPart`.

#### Scenario: Default behaviour is unchanged
- **WHEN** `WaitForTopic` is not set (or set to `false`) on `KafkaPublisherOptions` or `KafkaConsumerOptions`
- **THEN** `InitializeAsync` SHALL NOT issue any pre-flight metadata probe and SHALL behave exactly as before — a missing topic surfaces through the first `ProduceAsync` / `Consume` call as before.

#### Scenario: Opt-in enables wait loop
- **WHEN** `WaitForTopic = true` is set on either options class
- **THEN** `InitializeAsync` SHALL probe the configured `Topic` with `IAdminClient.GetMetadata` and retry while the broker reports `ErrorCode.UnknownTopicOrPart`.

### Requirement: Publisher waits for externally-owned topic
When `KafkaPublisherOptions.WaitForTopic = true`, `KafkaPublisher.InitializeAsync` SHALL probe the configured `Topic` and complete successfully only after the metadata response contains an entry for that topic with `Error.Code == ErrorCode.NoError`. The wait SHALL occur before any internal `IProducer` is built or returned.

#### Scenario: Topic appears after one or more probe attempts
- **WHEN** the topic named in `Topic` does not exist at the moment `InitializeAsync` is called but is created by another service shortly after
- **THEN** the publisher SHALL retry the metadata call at intervals of `TopicWaitInterval`
- **AND** SHALL complete `InitializeAsync` successfully once the metadata response reports the topic
- **AND** SHALL log the first miss at `Information` level and the eventual recovery at `Information` level.

#### Scenario: Topic already exists
- **WHEN** the topic exists at the moment `InitializeAsync` is called and `WaitForTopic = true`
- **THEN** the first probe SHALL succeed and `InitializeAsync` SHALL complete without emitting any `Information`-level wait log entries.

### Requirement: Consumer waits for externally-owned topic
When `KafkaConsumerOptions.WaitForTopic = true`, `KafkaConsumer.InitializeAsync` SHALL probe the configured `Topic` and complete successfully only after the metadata response contains an entry for that topic with `Error.Code == ErrorCode.NoError`. The wait SHALL occur before the internal `IConsumer` is built or before `Subscribe` is called.

#### Scenario: Topic appears after one or more probe attempts
- **WHEN** the topic named in `Topic` does not exist when `InitializeAsync` is called
- **AND** another service creates it shortly after
- **THEN** the consumer SHALL retry the metadata call at intervals of `TopicWaitInterval`
- **AND** SHALL proceed to `Subscribe` once the metadata response reports the topic.

### Requirement: Retry only on unknown topic
The topic wait loop SHALL retry only when the metadata response indicates the topic is unknown — either the `Topics` collection contains no entry for the requested name, or the per-topic `Error.Code` equals `ErrorCode.UnknownTopicOrPart`. All other broker errors, fatal `KafkaException` instances, and `OperationCanceledException` SHALL propagate immediately.

#### Scenario: Authorization failure propagates immediately
- **WHEN** the broker rejects the metadata call with `ErrorCode.TopicAuthorizationFailed` (or any non-`UnknownTopicOrPart` per-topic error)
- **THEN** `InitializeAsync` SHALL propagate the resulting `KafkaException` on the first attempt without further retries.

#### Scenario: Connection failure propagates immediately
- **WHEN** the broker cannot be reached and `GetMetadata` throws a fatal `KafkaException`
- **THEN** the resulting exception SHALL propagate without retry.

### Requirement: Retry interval and timeout configuration
The publisher and consumer options SHALL expose `TopicWaitInterval` (TimeSpan, default `5 seconds`) and `TopicWaitTimeout` (TimeSpan?, default `null`). When `TopicWaitTimeout` is non-null, the wait loop SHALL stop and rethrow the most recent unknown-topic error once the elapsed time exceeds the timeout. Both values SHALL be validated as positive when used.

#### Scenario: Custom interval is honoured
- **WHEN** `TopicWaitInterval = TimeSpan.FromMilliseconds(500)` is set
- **THEN** consecutive metadata probes SHALL be separated by approximately 500 milliseconds.

#### Scenario: Timeout exhaustion surfaces the underlying error
- **WHEN** `TopicWaitTimeout = TimeSpan.FromSeconds(10)` is set
- **AND** the topic has not appeared after 10 seconds of probing
- **THEN** `InitializeAsync` SHALL throw a `KafkaException` (or equivalent) describing the last unknown-topic response.

#### Scenario: Null timeout means no ceiling
- **WHEN** `TopicWaitTimeout = null`
- **THEN** the wait loop SHALL continue indefinitely until either the topic appears or the cancellation token is cancelled.

### Requirement: Cancellation token cancels the wait
The wait loop SHALL observe the `CancellationToken` passed into `InitializeAsync`. When the token is cancelled during a wait or between attempts, the loop SHALL throw `OperationCanceledException`.

#### Scenario: Cancellation during the inter-attempt delay
- **WHEN** the cancellation token is cancelled while the wait loop is sleeping between attempts
- **THEN** `InitializeAsync` SHALL throw `OperationCanceledException` rather than continuing.

### Requirement: Probe uses a disposable admin client
Each invocation of the wait loop SHALL build a dedicated `IAdminClient`, use it for the duration of the wait, and dispose it before returning. The persistent `IProducer` / `IConsumer` held by the publisher/consumer SHALL be created only after the probe succeeds.

#### Scenario: Admin client is disposed after success
- **WHEN** the wait loop completes successfully
- **THEN** the admin client used for probing SHALL be disposed before `InitializeAsync` returns.

#### Scenario: Admin client is disposed after failure
- **WHEN** the wait loop throws (timeout, cancellation, or non-retryable broker error)
- **THEN** the admin client used for probing SHALL be disposed before the exception is rethrown.

### Requirement: Logging of topic wait
The plugin SHALL emit the following log entries when `WaitForTopic = true`:

- First unknown-topic response per probed topic: `Information`, with the topic name, interval, and timeout (or "<none>").
- Subsequent unknown-topic responses for the same topic: `Debug`.
- Recovery (probe succeeds after one or more misses): `Information`.
- Timeout exhaustion: `Error`, immediately before rethrowing.

For the publisher, log entries SHALL be emitted via the optional `ILoggerFactory` passed to `KafkaPublisher` (`null` → `NullLoggerFactory.Instance` → silent). For the consumer, log entries SHALL be emitted via the existing required `ILoggerFactory`.

#### Scenario: First miss logged at Information
- **WHEN** the first metadata probe for a topic returns unknown-topic
- **THEN** an `Information`-level log SHALL be emitted indicating the consumer/publisher is waiting for that topic by name.

#### Scenario: Recovery logged at Information
- **WHEN** a metadata probe succeeds after at least one prior unknown-topic response
- **THEN** an `Information`-level log SHALL be emitted indicating the topic became available.

#### Scenario: Silent publisher when no logger factory supplied
- **WHEN** `KafkaPublisher` is constructed without an `ILoggerFactory` (legacy call shape)
- **AND** `WaitForTopic = true`
- **THEN** the probe SHALL still run correctly but SHALL produce no log output.
