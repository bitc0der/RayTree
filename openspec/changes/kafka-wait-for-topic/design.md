## Context

`RayTree.Plugins.Kafka` currently does no broker-side validation in `KafkaPublisher.InitializeAsync` or `KafkaConsumer.InitializeAsync` — both methods only build local librdkafka client handles. The missing-topic condition surfaces later: on the publisher, the first `ProduceAsync` raises `UnknownTopicOrPart`; on the consumer, `Consume` silently returns null/empty results indefinitely while librdkafka logs `UnknownTopicOrPart` warnings internally. In microservice topologies — common in deployments where a dedicated "schema owner" service creates topics — the order in which pods come up cannot be guaranteed, and these downstream failure modes force external orchestration (init containers, Helm hooks) or noisy production support tickets to compensate.

The RabbitMQ plugin already addresses the analogous problem via `WaitForTopology` (capability `rmq-topology-wait`), implemented by the internal helper `TopologyProbe`. That design uses AMQP passive declares and retries only on `NOT_FOUND` so genuine misconfiguration still fails fast. This change ports that pattern to Kafka.

## Goals / Non-Goals

**Goals:**
- Opt-in flag (default off) on both `KafkaPublisherOptions` and `KafkaConsumerOptions` that waits for the configured topic to appear before completing `InitializeAsync`.
- Retry on the narrow set of broker responses that mean "topic is not yet available": empty `Topics` collection, per-topic `UnknownTopicOrPart`, and per-topic `LeaderNotAvailable` (a transient state during cluster bootstrap and partition-leader election). All other broker errors, fatal librdkafka errors, and cancellation propagate immediately.
- Logging parity with `TopologyProbe` (first miss `Information`, subsequent misses `Debug`, recovery `Information`, timeout `Error`).
- Logger plumbing reaches both code paths: the publisher gains an optional `ILoggerFactory?` constructor parameter; the consumer already has one; BOTH builder extensions (`KafkaBuilderExtensions.UseKafka` and `KafkaSubscriberExtensions.UseKafka`) gain an optional `ILoggerFactory?` parameter so the documented fluent API can forward host logging.
- Source-compatible API addition — adding optional parameters to existing public constructors and builder extensions MUST not break existing call-sites at compile time. (Note: adding optional parameters to public constructors of a published library IS binary-breaking; see Risks.)

**Non-Goals:**
- Topic auto-creation. If the broker has `auto.create.topics.enable = true`, the broker handles creation; this feature only waits, it does not create.
- Partition / replica count validation. We only check existence, not that the topic matches an expected shape.
- Retrying around connection-level failures (broker unreachable). Those continue to propagate as before — `WaitForTopic` is strictly about topic-existence retry.
- Cross-cutting changes to `IQueuePublisher` / `IQueueConsumer` contracts.

## Decisions

### Use `IAdminClient.GetMetadata(topic, timeout)` for the probe
**Why:** It is the canonical Confluent.Kafka API for asking the broker whether a topic exists without producing or consuming data. The returned metadata response uses a small, stable set of error codes (`UnknownTopicOrPart`, `LeaderNotAvailable`, etc.) that map cleanly to retryable / non-retryable categories. Implementations MUST use `Metadata.Topics.FirstOrDefault(t => t.Topic == name)` rather than indexing `Topics[0]` directly — some broker versions return an empty `Topics` collection rather than a placeholder entry, and that empty case is itself a retryable miss per the spec.

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

### Run `GetMetadata` on a worker thread via `Task.Run` with a fixed-1-second timeout decoupled from `TopicWaitInterval`
**Why:** `IAdminClient.GetMetadata` is synchronous and blocks the calling thread for up to its timeout argument. Wrapping in `Task.Run` keeps `InitializeAsync` non-blocking on the host's main thread and lets us cooperatively check the `CancellationToken` between attempts. The internal `KafkaTopicProbe.MetadataCallTimeout = 1s` is decoupled from `TopicWaitInterval` so (a) cancellation latency is bounded at ~1s regardless of how long the user sets the inter-attempt interval, and (b) threadpool threads pinned in blocking librdkafka calls during shutdown release in roughly one second.

**Alternatives considered:**
- *Call `GetMetadata` inline.* Rejected: stalls the calling thread for up to N seconds per attempt.
- *Use `TopicWaitInterval` as the metadata-call timeout.* Rejected: doubles per-cycle wall-clock time under broker unreachability (GetMetadata + Task.Delay) and pins shutdown threads for the full interval — the option semantics ("delay between attempts") would silently double under common failure modes.

### Retry on transient transport-level `KafkaException`s
**Why:** The dominant microservice startup-ordering case is "broker pod has not yet started" — librdkafka surfaces this as a non-fatal `KafkaException` (e.g. `Local_Transport`, `Local_AllBrokersDown`, `Local_Resolve`, `Local_TimedOut`) thrown from `GetMetadata` BEFORE any metadata response is constructed. If we treated all thrown `KafkaException`s as terminal, `WaitForTopic` would fail the startup race it exists to solve. The probe therefore catches non-fatal `KafkaException`s whose `Error.Code` is in the enumerated transport set and classifies them as retryable misses, identical to a per-topic `UnknownTopicOrPart`. Fatal errors (`Error.IsFatal == true`) and all other broker error codes still propagate immediately so genuine misconfiguration fails fast.

**Alternatives considered:**
- *Catch every non-fatal `KafkaException`.* Rejected: would also swallow non-transient bugs like `Local_InvalidArg` or `Local_BadMsg` that should fail loudly.
- *Add a user-facing retry list option.* Rejected: the four transport codes are stable and well-known; an option would be configuration noise without a real use case.

### Add an optional `ILoggerFactory?` to `KafkaPublisher` (and `UseKafka`)
**Why:** The probe needs to log progress, and the existing `RabbitMqPublisher` already follows this exact pattern as the documented exception to the logging-placement rule in `CLAUDE.md`. Making the parameter optional with a `null → NullLoggerFactory.Instance` fallback keeps every existing call-site source-compatible.

**Alternatives considered:**
- *Silent probe with no logger.* Rejected: operators need at least one log line to know the service is waiting for a topic — startup hangs without visibility are a common production support failure mode.
- *Require a non-null `ILoggerFactory`.* Rejected: breaks every existing `new KafkaPublisher(options)` call-site and the `UseKafka(configure)` builder shape.

### Add an optional `ILoggerFactory?` to `KafkaSubscriberExtensions.UseKafka` too
**Why:** `KafkaConsumer` already takes a required `ILoggerFactory`, but the public builder extension `KafkaSubscriberExtensions.UseKafka` currently hardcodes `NullLoggerFactory.Instance` — so consumers wired through the documented fluent API silently swallow every probe log entry. The fix is to add an optional `ILoggerFactory? loggerFactory = null` parameter to that extension and forward it to the `KafkaConsumer` constructor. Symmetric with the publisher-side change and required for the spec's logging contract to actually be observable on the consumer side.

**Alternatives considered:**
- *Resolve `ILoggerFactory` from DI inside the extension.* Rejected: the existing `UsePublisher`/`UseConsumer` builder shape passes a `Type` discriminator to its factory delegate, not a service provider — there's no DI handle to resolve from at the extension layer. Callers using `AddChangeTracking` must pass the host's `ILoggerFactory` through explicitly.

### Probe placement: inside the producer/consumer lazy-init paths, not just `InitializeAsync`
**Why:** `KafkaPublisher.PublishAsync` calls `GetProducerAsync()` independently of `InitializeAsync` (lazy double-checked init), so placing the probe only at the `InitializeAsync` entry point creates a bypass: any caller that reaches `PublishAsync` without first awaiting `InitializeAsync` builds the producer with the probe skipped. The mitigation is to either (a) make `WaitForTopic = true` imply that the probe runs inside `GetProducerAsync()` before `_producer` is constructed (mirroring `RabbitMqPublisher.GetChannelAsync`), or (b) document that `InitializeAsync` MUST be awaited explicitly before any `PublishAsync` call when `WaitForTopic = true`. We choose (a) because the production framework's existing call order already does (b) implicitly, and (a) is robust against tests, direct usage, and future call-site additions.

**Concurrency — split semaphores.** `KafkaPublisher` uses TWO `SemaphoreSlim` instances rather than one, with a `volatile bool _probeCompleted` flag separating them:

1. `_probeSemaphore` gates the one-shot probe. First-time concurrent callers serialize here for the probe duration. Once `_probeCompleted` flips to `true`, ALL subsequent callers short-circuit the semaphore entirely on every call — steady-state publishers never enter it.
2. `_buildSemaphore` gates the (microsecond-long) `ProducerBuilder.Build()` critical section.

Splitting the two is essential: a unified semaphore covering both the probe and the build would force every concurrent first-time `PublishAsync` caller to serialize behind a multi-second probe, head-of-line-blocking the entire publisher graph during cold start. With the split, the cold-start delay seen by concurrent callers is bounded to the build step (microseconds), not the probe (potentially minutes).

**Dispose safety.** `KafkaPublisher.Dispose` is idempotent (`volatile bool _disposed` guard) and uses an internal `SafeRelease(SemaphoreSlim)` helper that swallows `ObjectDisposedException` from in-flight `Release()` calls. Without this, a Dispose-during-init race during host shutdown would throw out of a `finally` block, producing a noisy crash log that masks the real cancellation signal. The same pattern exists in `RabbitMqPublisher` but the longer critical section opened by `WaitForTopic` makes the race much more likely on Kafka — explicit guard is warranted.

### Cancellation re-check after probe in `KafkaConsumer.InitializeAsync`
**Why:** The pre-probe comment justifies running the probe BEFORE allocating the native `IConsumer` handle on the basis that "a failed probe leaves no state to clean up." The inverse — a SUCCESSFUL probe followed by cancellation before `ConsumerBuilder.Build()` — would allocate a native librdkafka handle that a caller treating `OperationCanceledException` as "no resources" would discard without disposing. A single-line `cancellationToken.ThrowIfCancellationRequested()` between the probe and the builder closes the window.

### Make `KafkaConsumer.InitializeAsync` genuinely async
**Why:** The current implementation returns `Task.CompletedTask`. Adding an `await` for the probe requires changing the method body to `async Task` and ordering: probe first, then `ConsumerBuilder.Build()`, then `Subscribe`. Implementations MUST NOT wrap the probe in `.GetAwaiter().GetResult()` to preserve the sync-completing shape — that would deadlock under ASP.NET Core's `SynchronizationContext` and any other captured context.

## Risks / Trade-offs

- **Risk:** A broker configured with `auto.create.topics.enable = true` will respond to the metadata probe by creating an empty topic, masking real misconfiguration (typo in topic name still succeeds). This is the broker default on `confluentinc/cp-kafka` images used by Testcontainers.
  → **Mitigation:** Document this in the option XML doc and in the `kafka-microservices-example` flow. Integration tests that need to exercise the wait loop MUST override `KAFKA_AUTO_CREATE_TOPICS_ENABLE=false` via the Testcontainers `WithEnvironment` API (not just via the `KafkaBuilder` shortcuts) when spinning up the broker. Matches RabbitMQ's analogous quirk where `WaitForTopology` cannot distinguish "owner declared exchange" from "we accidentally declared a typo'd exchange ourselves".

- **Risk:** Long startup hangs are silent unless the operator looks at logs, and the consumer-side fluent builder previously hardcoded `NullLoggerFactory.Instance`.
  → **Mitigation:** Both builder extensions now accept an optional `ILoggerFactory?` (see Decisions). First miss logs at `Information` (visible at default verbosity). `TopicWaitTimeout` lets operators bound the wait explicitly.

- **Risk:** `GetMetadata` blocking inside `Task.Run` means cancellation during an in-flight metadata call is granular at the probe-timeout level (default a few seconds), not instant. librdkafka does not accept managed cancellation tokens.
  → **Mitigation:** `KafkaTopicProbe.MetadataCallTimeout` is a fixed 1 second decoupled from `TopicWaitInterval`. Cancellation during a metadata call is therefore observed within ~1 s regardless of how long the user sets the interval. Spec explicitly carves this out (Requirement: Cancellation token cancels the wait).

- **Risk:** `ChangeTrackingBuilder.Build()` is sync-over-async with no cancellation token — `AddChangeTracking` uses this path. With `WaitForTopic = true` and the default `TopicWaitTimeout = null`, host startup blocks indefinitely with no SIGTERM escape.
  → **Mitigation:** Documentation-only. Both `KafkaPublisherOptions.TopicWaitTimeout` and `KafkaConsumerOptions.TopicWaitTimeout` XML docs carry an explicit caution about this combination and point callers to `BuildAsync(cancellationToken)` with the host's `ApplicationStopping` token. An earlier draft hooked `Console.CancelKeyPress` from `Build()` to provide an escape — rejected: layering violation (the library has no business reaching into Console signal handling, and it would race the host's own `ConsoleLifetime` in ASP.NET Core / generic hosts).

- **Risk:** Sequential consumer initialization in `ChangeSubscriber.InitializeAsync` meant one slow consumer (e.g. Kafka `WaitForTopic` against a missing topic) would block unrelated consumers from subscribing, with no diagnostic indicating which consumer was stuck.
  → **Mitigation:** Changed `ChangeSubscriber.InitializeAsync` to parallelise via `Task.WhenAll` across `_queues ∪ _isolatedQueues`. A single slow consumer no longer head-of-line-blocks the others.

- **Risk:** Adding optional parameters to `KafkaPublisher`'s constructor is source-compatible but binary-breaking — pre-compiled callers built against the old single-arg signature will hit `MissingMethodException` at runtime when they upgrade only the RayTree.Plugins.Kafka assembly.
  → **Mitigation:** Document this in the release notes. The proposal acknowledges the limitation explicitly. An alternative (publish an overload rather than mutate the existing constructor) was considered but rejected because the new parameter is opt-in and the package is still in active pre-1.0 development; the cost of polluting the surface with overloads exceeds the cost of a one-line release-note caveat.

- **Risk:** Brand-new Kafka clusters return `LeaderNotAvailable` transiently for the first few seconds while partition leaders are elected. Treating this as non-retryable would defeat the deployment-ordering goal.
  → **Mitigation:** The spec includes `LeaderNotAvailable` in the retryable set alongside `UnknownTopicOrPart` and empty `Topics`. Other transient errors (`KafkaStorageError`, `NotController`, etc.) are NOT retryable — operators who need them should wrap startup with their own retry layer.

- **Trade-off:** Adding an `AdminClient` build per probe call adds a small startup cost (~10 ms on a healthy broker) even when the topic exists. Acceptable: the feature is opt-in and only runs once per process.
