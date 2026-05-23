## Why

In microservice deployments where one service owns a Kafka topic and others connect later, the consuming or publishing service crashes on startup if the topic does not yet exist. RabbitMQ already supports an opt-in `WaitForTopology` flag for the same scenario (`rmq-topology-wait`); Kafka should offer a symmetric capability so deployments aren't forced into strict startup ordering or external orchestration.

## What Changes

- Add `WaitForTopic` (bool, default `false`), `TopicWaitInterval` (TimeSpan, default 5 s), and `TopicWaitTimeout` (TimeSpan?, default `null`) to both `KafkaPublisherOptions` and `KafkaConsumerOptions`.
- When `WaitForTopic = true`, `KafkaPublisher.InitializeAsync` and `KafkaConsumer.InitializeAsync` SHALL probe the configured `Topic` with an `IAdminClient.GetMetadata` call and retry while the broker reports `ErrorCode.UnknownTopicOrPart` (or returns metadata with no partitions for the topic).
- Default behaviour is unchanged — without the opt-in, missing-topic errors surface immediately as before.
- Only "unknown topic" responses trigger retry. All other broker errors (authorization, fatal librdkafka errors, connection failures) propagate immediately.
- `KafkaPublisher` constructor gains an optional `ILoggerFactory?` parameter (null → `NullLoggerFactory.Instance`) so the probe can log its progress, mirroring the `RabbitMqPublisher` change made for `rmq-topology-wait`. `UseKafka` builder extension gains an optional `ILoggerFactory?` parameter.
- New internal helper `KafkaTopicProbe` (parallel to `TopologyProbe`) encapsulates the wait loop.

## Capabilities

### New Capabilities
- `kafka-topic-wait`: Opt-in retry on Kafka publisher and consumer initialization when the configured topic does not yet exist on the broker, so services can start in any order in microservice deployments.

### Modified Capabilities
<!-- none -->

## Impact

- Affected code: `src/RayTree.Plugins.Kafka/KafkaPublisher.cs`, `KafkaPublisherOptions.cs`, `KafkaConsumer.cs`, `KafkaConsumerOptions.cs`, `KafkaBuilderExtensions.cs`, `KafkaSubscriberExtensions.cs`; new file `KafkaTopicProbe.cs`.
- Public API additions only — no breaking changes. `KafkaPublisher`'s constructor parameter is optional with a null default, so existing call-sites still compile.
- New tests in `tests/RayTree.Plugins.Kafka.Tests` (unit tests for the probe behaviour; integration test verifying a delayed-topic publish flow against Testcontainers).
- Docs: update `CLAUDE.md` Kafka plugin row to describe the new options, and add a logging-placement note for `KafkaPublisher` matching the `RabbitMqPublisher` exception.
