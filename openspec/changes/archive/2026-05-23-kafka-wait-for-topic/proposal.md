## Why

In microservice deployments where one service owns a Kafka topic and others connect later, the consuming or publishing service crashes on startup if the topic does not yet exist. RabbitMQ already supports an opt-in `WaitForTopology` flag for the same scenario (`rmq-topology-wait`); Kafka should offer a symmetric capability so deployments aren't forced into strict startup ordering or external orchestration.

## What Changes

- Add `WaitForTopic` (bool, default `false`), `TopicWaitInterval` (TimeSpan, default 5 s), and `TopicWaitTimeout` (TimeSpan?, default `null`) to both `KafkaPublisherOptions` and `KafkaConsumerOptions`.
- When `WaitForTopic = true`, `KafkaPublisher.InitializeAsync` and `KafkaConsumer.InitializeAsync` SHALL probe the configured `Topic` with an `IAdminClient.GetMetadata` call and retry while the response indicates the topic is not yet available — defined as: empty `Topics` collection, per-topic `ErrorCode.UnknownTopicOrPart`, or per-topic `ErrorCode.LeaderNotAvailable` (a transient state during cluster bootstrap / partition-leader election).
- Default behaviour is unchanged — without the opt-in, missing-topic conditions surface through the underlying client as today (publisher: `UnknownTopicOrPart` on first `ProduceAsync`; consumer: silent no-message returns from `Consume`).
- All other broker errors (authorization, other non-retryable codes, fatal librdkafka errors, connection failures) propagate immediately.
- `KafkaPublisher` constructor gains an optional `ILoggerFactory?` parameter (null → `NullLoggerFactory.Instance`) so the probe can log its progress, mirroring the `RabbitMqPublisher` change made for `rmq-topology-wait`. Both `KafkaBuilderExtensions.UseKafka` (publisher-side) and `KafkaSubscriberExtensions.UseKafka<TEntity>` (subscriber-side) gain an optional `ILoggerFactory?` parameter — the consumer-side change is required because the existing extension hardcodes `NullLoggerFactory.Instance` and would otherwise silently drop all probe logs.
- The publisher's `lock (_lock)` around producer construction is replaced with a `SemaphoreSlim` so the probe (async) can serialize correctly with concurrent `PublishAsync` callers, mirroring `RabbitMqPublisher`. The probe runs inside the lazy `GetProducer` path (not just `InitializeAsync`) so callers that reach `PublishAsync` without explicit `InitializeAsync` still benefit.
- `KafkaConsumer.InitializeAsync` converts from a sync-completing `Task.CompletedTask` shape to a genuinely `async Task` body so the probe can be awaited safely (sync-over-async would deadlock under captured `SynchronizationContext`s).
- New internal helper `KafkaTopicProbe` (parallel to `TopologyProbe`) encapsulates the wait loop.

## Capabilities

### New Capabilities
- `kafka-topic-wait`: Opt-in retry on Kafka publisher and consumer initialization when the configured topic does not yet exist on the broker, so services can start in any order in microservice deployments.

### Modified Capabilities
<!-- none -->

## Impact

- Affected code: `src/RayTree.Plugins.Kafka/KafkaPublisher.cs`, `KafkaPublisherOptions.cs`, `KafkaConsumer.cs`, `KafkaConsumerOptions.cs`, `KafkaBuilderExtensions.cs`, `KafkaSubscriberExtensions.cs`; new file `KafkaTopicProbe.cs`.
- **Source-compatible** API additions: every new parameter is optional with a default, so existing source code recompiles unchanged.
- **Binary-breaking** for the `KafkaPublisher` constructor: adding an optional parameter to a public constructor in a published assembly changes the binary contract — pre-compiled downstream consumers will hit `MissingMethodException` until they recompile against the new signature. Will be called out in the release notes.
- New tests in `tests/RayTree.Plugins.Kafka.Tests`: unit tests for the probe validation/cancellation paths; integration tests (Testcontainers, `KAFKA_AUTO_CREATE_TOPICS_ENABLE=false`) covering both publisher and consumer delayed-topic flows plus a capturing logger to verify the Information-level log contract.
- Docs: update `CLAUDE.md` Kafka plugin row to describe the new options and the broadened retry set, and add a logging-placement note for `KafkaPublisher` matching the `RabbitMqPublisher` exception; release-notes entry for the binary break.
