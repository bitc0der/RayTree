## Context

`RayTree.Plugins.Kafka` currently fails fast in `KafkaPublisher.InitializeAsync` and `KafkaConsumer.InitializeAsync` when the configured Kafka topic does not exist on the broker (the publisher's first `ProduceAsync` raises an `UnknownTopicOrPart` error; the consumer's first `Consume` returns a metadata error). In microservice topologies — common in deployments where a dedicated "schema owner" service creates topics — the order in which pods come up cannot be guaranteed, and a hard failure forces external orchestration (init containers, Helm hooks) to compensate.

The RabbitMQ plugin already addresses the analogous problem via `WaitForTopology` (capability `rmq-topology-wait`), implemented by the internal helper `TopologyProbe`. That design uses AMQP passive declares and retries only on `NOT_FOUND` so genuine misconfiguration still fails fast. This change ports that pattern to Kafka.

## Goals / Non-Goals

**Goals:**
- Opt-in flag (default off) on both `KafkaPublisherOptions` and `KafkaConsumerOptions` that waits for the configured topic to appear before completing `InitializeAsync`.
- Surface only "unknown topic" as a retryable condition; authorization failures, fatal librdkafka errors, and cancellation propagate immediately.
- Logging parity with `TopologyProbe` (first miss `Information`, subsequent misses `Debug`, recovery `Information`, timeout `Error`).
- Zero impact on existing call-sites — adding a logger parameter to `KafkaPublisher` and `UseKafka` MUST be source-compatible.

**Non-Goals:**
- Topic auto-creation. If the broker has `auto.create.topics.enable = true`, the broker handles creation; this feature only waits, it does not create.
- Partition / replica count validation. We only check existence, not that the topic matches an expected shape.
- Retrying around connection-level failures (broker unreachable). Those continue to propagate as before — `WaitForTopic` is strictly about topic-existence retry.
- Cross-cutting changes to `IQueuePublisher` / `IQueueConsumer` contracts.

## Decisions

### Use `IAdminClient.GetMetadata(topic, timeout)` for the probe
**Why:** It is the canonical Confluent.Kafka API for asking the broker whether a topic exists without producing or consuming data. The returned `Metadata.Topics[0].Error.Code` is `ErrorCode.UnknownTopicOrPart` for missing topics — a clean discriminator that maps directly to RabbitMQ's `NOT_FOUND`.

**Alternatives considered:**
- *Producer-side `ProduceAsync` retry loop.* Rejected: tying the wait to the data path means partial writes during topic creation and pollutes the publisher's hot path with retry logic.
- *Consumer-side `Consume` polling.* Rejected: librdkafka logs noisy `UnknownTopicOrPart` errors per poll; the admin API is quiet by comparison.
- *`IAdminClient.DescribeTopicsAsync` (newer API).* Equivalent in behaviour but heavier dependency surface and async-only; `GetMetadata` is broadly supported across Confluent.Kafka 2.x and already returns the per-topic `Error` we need.

### Place the probe logic in a new internal `KafkaTopicProbe` static class
**Why:** Mirrors `TopologyProbe` so reviewers can see the parallel structure. Keeps probe state (stopwatch, miss count, logging cadence) out of the consumer/publisher classes, which already shoulder enough responsibility (poll thread management, deferred ACK channels).

**Alternatives considered:**
- *Inline the loop in each class.* Rejected: duplicated logic across publisher and consumer, harder to unit-test in isolation.

### Use a single shared admin client per probe call, disposed at the end
**Why:** Admin clients are cheap to build and the probe is a one-shot startup operation — no need to share an admin client across the lifetime of the publisher/consumer. Disposing on exit avoids leaking native handles if `InitializeAsync` is called from a long-running host that may eventually shut down.

**Alternatives considered:**
- *Cache the admin client on the publisher/consumer.* Rejected: extra disposal complexity for a feature that runs once.

### Run `GetMetadata` on a worker thread via `Task.Run`
**Why:** `IAdminClient.GetMetadata` is synchronous and blocks the calling thread for up to its timeout argument. Wrapping in `Task.Run` keeps `InitializeAsync` non-blocking on the host's main thread and lets us cooperatively check the `CancellationToken` between attempts.

**Alternatives considered:**
- *Call `GetMetadata` inline.* Rejected: stalls the calling thread for up to N seconds per attempt.

### Add an optional `ILoggerFactory?` to `KafkaPublisher` (and `UseKafka`)
**Why:** The probe needs to log progress, and the existing `RabbitMqPublisher` already follows this exact pattern as the documented exception to the logging-placement rule in `CLAUDE.md`. Making the parameter optional with a `null → NullLoggerFactory.Instance` fallback keeps every existing call-site source-compatible.

**Alternatives considered:**
- *Silent probe with no logger.* Rejected: operators need at least one log line to know the service is waiting for a topic — startup hangs without visibility are a common production support failure mode.
- *Require a non-null `ILoggerFactory`.* Rejected: breaks every existing `new KafkaPublisher(options)` call-site and the `UseKafka(configure)` builder shape.

### `KafkaConsumer` keeps its non-nullable `ILoggerFactory` — the consumer already requires one
**Why:** Unlike RabbitMQ (where `RabbitMqConsumer` intentionally has no logger), `KafkaConsumer` already takes `ILoggerFactory` for fatal-error logging on the poll thread. The probe reuses that logger directly — no API change on the consumer side.

## Risks / Trade-offs

- **Risk:** A broker configured with `auto.create.topics.enable = true` will respond to the metadata probe by creating an empty topic, masking real misconfiguration (typo in topic name still succeeds).
  → **Mitigation:** Document this in the option XML doc and in the `kafka-microservices-example` flow. This matches RabbitMQ's behaviour where `WaitForTopology` cannot distinguish "owner declared exchange" from "we accidentally declared a typo'd exchange ourselves" if `DeclareExchange = true` is ever flipped.

- **Risk:** Long startup hangs are silent unless the operator looks at logs.
  → **Mitigation:** First miss logs at `Information` (visible at default verbosity). `TopicWaitTimeout` lets operators bound the wait explicitly.

- **Risk:** `GetMetadata` blocking inside `Task.Run` means cancellation between attempts is granular at the probe-timeout level (default a few seconds), not instant.
  → **Mitigation:** Use a small inner `GetMetadata` timeout (≤ `TopicWaitInterval`) so cancellation is observed within roughly one interval — same trade-off `TopologyProbe` accepts.

- **Trade-off:** Adding an `AdminClient` build per probe call adds a small startup cost (~10 ms on a healthy broker) even when the topic exists. Acceptable: the feature is opt-in and only runs once per process.
