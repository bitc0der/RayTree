## 1. Options surface

- [x] 1.1 Add `WaitForTopic`, `TopicWaitInterval` (default `TimeSpan.FromSeconds(5)`), and `TopicWaitTimeout` properties to `src/RayTree.Plugins.Kafka/KafkaPublisherOptions.cs` with XML docs mirroring the RabbitMQ wording.
- [x] 1.2 Add the same three properties to `src/RayTree.Plugins.Kafka/KafkaConsumerOptions.cs`.

## 2. Probe helper

- [x] 2.1 Create `src/RayTree.Plugins.Kafka/KafkaTopicProbe.cs` as `internal static class` with a single `WaitForTopicAsync(string bootstrapServers, string topic, TimeSpan interval, TimeSpan? timeout, ILogger? logger, CancellationToken)` entry point.
- [x] 2.2 Validate inputs first: throw `ArgumentOutOfRangeException` when `interval <= TimeSpan.Zero` or when `timeout` is non-null and `<= TimeSpan.Zero`. Throw `OperationCanceledException` if the cancellation token is already cancelled before issuing any metadata call.
- [x] 2.3 Build a dedicated `IAdminClient` via `AdminClientBuilder` and wrap the loop in `try { ... } finally { adminClient.Dispose(); }` so success, failure, cancellation, and timeout paths all dispose the client.
- [x] 2.4 Inner loop: `await Task.Run(() => admin.GetMetadata(topic, interval))` per attempt. Locate the per-topic entry via `metadata.Topics.FirstOrDefault(t => t.Topic == topic)` — do NOT index `Topics[0]` directly (the empty-Topics branch is a retryable miss). Treat as a retryable miss when: the entry is null/missing, OR `entry.Error.Code == ErrorCode.UnknownTopicOrPart`, OR `entry.Error.Code == ErrorCode.LeaderNotAvailable`.
- [x] 2.5 Propagate immediately on: any per-topic `Error.Code` not enumerated above (synthesise/throw a `KafkaException`), any `KafkaException` where `Error.IsFatal == true`, and `OperationCanceledException`.
- [x] 2.6 Between attempts: `await Task.Delay(interval, cancellationToken)`. Check elapsed time after each failed attempt; if `timeout` is non-null and exceeded, log `Error` and rethrow the last `KafkaException` (or a synthesised `KafkaException` carrying `ErrorCode.UnknownTopicOrPart` if every prior response was an empty-Topics one).
- [x] 2.7 Logging: first miss `Information` with topic name, interval, and timeout (`<none>` when null); subsequent misses `Debug`; recovery `Information` (only when at least one prior miss occurred); timeout exhaustion `Error` immediately before rethrow.

## 3. Publisher integration

- [x] 3.1 Change `KafkaPublisher` constructor to `KafkaPublisher(KafkaPublisherOptions options, ILoggerFactory? loggerFactory = null)`; default the factory to `NullLoggerFactory.Instance` and create `ILogger<KafkaPublisher>` from it; store it for the probe.
- [x] 3.2 Replace the `lock (_lock)` in `KafkaPublisher` with a `SemaphoreSlim _semaphore = new(1, 1)` so the producer-init critical section can `await` the probe (mirroring `RabbitMqPublisher.GetChannelAsync`).
- [x] 3.3 Move the probe call inside `GetProducer()` (renamed to `GetProducerAsync` returning `Task<IProducer<...>>`) so it runs on the lazy-init path used by both `InitializeAsync` and `PublishAsync`. When `_options.WaitForTopic == true`, invoke `KafkaTopicProbe.WaitForTopicAsync` before constructing `_producer`. `InitializeAsync` becomes `await GetProducerAsync(cancellationToken)`.
- [x] 3.4 Update `KafkaBuilderExtensions.UseKafka` to accept an optional `ILoggerFactory? loggerFactory = null` parameter and forward it to `new KafkaPublisher(options, loggerFactory)`.

## 4. Consumer integration

- [x] 4.1 Convert `KafkaConsumer.InitializeAsync` from sync-completing (`return Task.CompletedTask`) to genuinely `async Task`. Do NOT use `.GetAwaiter().GetResult()` — would deadlock under ASP.NET Core's `SynchronizationContext`.
- [x] 4.2 In the new async body, when `_options.WaitForTopic == true`, invoke `KafkaTopicProbe.WaitForTopicAsync` (passing `_logger`) BEFORE `new ConsumerBuilder<...>(config).Build()` and before `Subscribe`.
- [x] 4.3 Update `KafkaSubscriberExtensions.UseKafka<TEntity>` to accept an optional `ILoggerFactory? loggerFactory = null` parameter and forward it to `new KafkaConsumer(options, loggerFactory ?? NullLoggerFactory.Instance)`. Without this, the spec's consumer-side logging requirements are unsatisfiable for fluent-builder callers.

## 5. Tests — unit

- [x] 5.1 In `tests/RayTree.Plugins.Kafka.Tests`, assert the three new properties' default values (`false`, `5s`, `null`) on both `KafkaPublisherOptions` and `KafkaConsumerOptions`.
- [x] 5.2 Add `tests/RayTree.Plugins.Kafka.Tests/KafkaTopicProbeTests.cs` covering: non-positive `interval` throws `ArgumentOutOfRangeException`; non-positive `timeout` throws `ArgumentOutOfRangeException`; pre-cancelled token throws `OperationCanceledException` without calling `GetMetadata`; cancellation between attempts throws `OperationCanceledException` promptly.
- [x] 5.3 Add a test asserting `KafkaPublisher` constructed with no logger factory still constructs and disposes cleanly (legacy call shape unchanged).
- [x] 5.4 Add a test for `KafkaSubscriberExtensions.UseKafka` confirming the no-arg overload still works (back-compat) and the new overload accepting `ILoggerFactory?` constructs a consumer whose internal logger is wired through the supplied factory (reflection check on `_logger`).

## 6. Tests — integration (Testcontainers)

- [x] 6.1 Add `tests/RayTree.Plugins.Kafka.Tests/KafkaTopicWaitTests.cs` marked `[NonParallelizable]`. Spin up a fresh Kafka container using the Testcontainers container builder's `.WithEnvironment("KAFKA_AUTO_CREATE_TOPICS_ENABLE", "false")` (the `KafkaBuilder` shortcuts do not expose this, so use the raw container API or post-process the configuration). Without this override the broker auto-creates the probed topic and the wait loop never engages.
- [x] 6.2 Test (publisher): `WaitForTopic = true` returns once the topic is created mid-wait. Create the topic from an `IAdminClient` after a 1-second delay; assert `InitializeAsync` completes within ~2 seconds.
- [x] 6.3 Test (publisher): `WaitForTopic = true` with `TopicWaitTimeout = TimeSpan.FromSeconds(2)` throws a `KafkaException` after the timeout elapses when the topic never appears.
- [x] 6.4 Test (publisher): `WaitForTopic = false` against a non-existent topic still surfaces `UnknownTopicOrPart` through `ProduceAsync` (regression guard for default behaviour).
- [x] 6.5 Test (consumer): mirror 6.2 for `KafkaConsumer` — assert `InitializeAsync` completes once the topic appears, and that subsequent `Subscribe`/`Consume` work normally.
- [x] 6.6 Tests in 6.2 and 6.5 SHALL use a capturing `ILoggerProvider` (e.g. an in-memory `ITestLoggerFactory` from `Microsoft.Extensions.Logging.Testing` or a tiny custom one) and assert that exactly one `Information` log was emitted on the first miss and exactly one `Information` log was emitted on recovery, satisfying the spec's logging contract.
- [x] 6.7 Test: `WaitForTopic = true` against a topic protected by ACLs (or simulated by an `IAdminClient` that returns `TopicAuthorizationFailed`) propagates immediately on the first attempt without retry.

## 7. Documentation

- [x] 7.1 Update `CLAUDE.md` Kafka plugin row (under "Publisher-side plugins") to describe `WaitForTopic`, `TopicWaitInterval`, `TopicWaitTimeout` on both options classes, the broadened retry set (`UnknownTopicOrPart`, `LeaderNotAvailable`, empty-Topics), and the new optional `ILoggerFactory?` parameters on both `KafkaPublisher`/`UseKafka` (publisher-side) and `UseKafka<TEntity>` (subscriber-side).
- [x] 7.2 Update the "Logging placement rule" entry in `CLAUDE.md` to note that `KafkaPublisher` now accepts an optional `ILoggerFactory?` (null → `NullLoggerFactory.Instance`) and that both Kafka builder extensions follow the same shape — explicitly callout that the consumer-side builder extension change is required so the consumer's already-non-nullable logger requirement is reachable from the fluent API.
- [x] 7.3 Add a release-notes entry noting the binary-breaking constructor change to `KafkaPublisher` (adding an optional parameter to a public constructor in a published assembly bumps the binary contract); recommend full-recompile when upgrading.

## 8. Verification

- [x] 8.1 Run `dotnet build RayTree.slnx -c Release` and confirm no new warnings.
- [x] 8.2 Run `dotnet test tests/RayTree.Plugins.Kafka.Tests` (unit tests) and confirm green.
- [x] 8.3 Run the integration tests against a local Docker Kafka and confirm green.
- [x] 8.4 Run `openspec validate kafka-wait-for-topic --strict` to confirm spec format is still valid after edits.
