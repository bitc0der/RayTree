## 1. Options surface

- [ ] 1.1 Add `WaitForTopic`, `TopicWaitInterval` (default `TimeSpan.FromSeconds(5)`), and `TopicWaitTimeout` properties to `src/RayTree.Plugins.Kafka/KafkaPublisherOptions.cs` with XML docs mirroring the RabbitMQ wording.
- [ ] 1.2 Add the same three properties to `src/RayTree.Plugins.Kafka/KafkaConsumerOptions.cs`.

## 2. Probe helper

- [ ] 2.1 Create `src/RayTree.Plugins.Kafka/KafkaTopicProbe.cs` as `internal static class` with a single `WaitForTopicAsync(string bootstrapServers, string topic, TimeSpan interval, TimeSpan? timeout, ILogger? logger, CancellationToken)` entry point.
- [ ] 2.2 Inside `WaitForTopicAsync`: validate `interval > 0` and `timeout > 0` when set; build an `IAdminClient` via `AdminClientBuilder`; loop with `Task.Run(() => admin.GetMetadata(topic, interval))` per attempt, treating an empty `Topics` collection or per-topic `ErrorCode.UnknownTopicOrPart` as a retryable miss.
- [ ] 2.3 Propagate any other `KafkaException` (authorization, fatal) and `OperationCanceledException` immediately; honour `CancellationToken` between attempts via `Task.Delay`.
- [ ] 2.4 Log first miss at `Information` with topic name, interval, and timeout (`<none>` when null); subsequent misses at `Debug`; recovery at `Information`; timeout exhaustion at `Error` immediately before rethrow.
- [ ] 2.5 Dispose the `IAdminClient` in a `finally` block so both success and failure paths free the native handle.

## 3. Publisher integration

- [ ] 3.1 Change `KafkaPublisher` constructor to `KafkaPublisher(KafkaPublisherOptions options, ILoggerFactory? loggerFactory = null)`; default the factory to `NullLoggerFactory.Instance` and create `ILogger<KafkaPublisher>` from it; store it for the probe.
- [ ] 3.2 In `KafkaPublisher.InitializeAsync`, before any `GetProducer()` call, invoke `KafkaTopicProbe.WaitForTopicAsync` when `_options.WaitForTopic == true`.
- [ ] 3.3 Update `KafkaBuilderExtensions.UseKafka` to accept an optional `ILoggerFactory? loggerFactory = null` and pass it through to `new KafkaPublisher(options, loggerFactory)`.

## 4. Consumer integration

- [ ] 4.1 In `KafkaConsumer.InitializeAsync`, before `new ConsumerBuilder<...>(config).Build()`, invoke `KafkaTopicProbe.WaitForTopicAsync` when `_options.WaitForTopic == true`, passing the existing `_logger`.

## 5. Tests — unit

- [ ] 5.1 Add `tests/RayTree.Plugins.Kafka.Tests/KafkaOptionsTests.cs` (or extend the existing publisher test class) asserting the three new properties' default values (`false`, `5s`, `null`) on both options classes.
- [ ] 5.2 Add `tests/RayTree.Plugins.Kafka.Tests/KafkaTopicProbeTests.cs` covering: validation throws on non-positive interval and non-positive timeout; cancellation between attempts throws `OperationCanceledException`. (Skip cases that require a running broker — those live in integration tests.)
- [ ] 5.3 Add a test asserting `KafkaPublisher` constructed with no logger factory still constructs and disposes cleanly (legacy call shape unchanged).

## 6. Tests — integration (Testcontainers)

- [ ] 6.1 Add `tests/RayTree.Plugins.Kafka.Tests/KafkaTopicWaitTests.cs` marked `[NonParallelizable]` spinning up a fresh Kafka container with `auto.create.topics.enable=false`.
- [ ] 6.2 Test: `WaitForTopic = true` on the publisher returns once the topic is created mid-wait (create the topic from an admin client after a delay; assert `InitializeAsync` completes).
- [ ] 6.3 Test: `WaitForTopic = true` with `TopicWaitTimeout` set to a short duration throws after the timeout when the topic never appears.
- [ ] 6.4 Test: `WaitForTopic = false` against a non-existent topic still surfaces the unknown-topic error through `ProduceAsync` (regression guard for default behaviour).

## 7. Documentation

- [ ] 7.1 Update `CLAUDE.md` Kafka plugin row (under "Publisher-side plugins") to describe `WaitForTopic`, `TopicWaitInterval`, `TopicWaitTimeout` on both options classes, mirroring the existing RabbitMQ description.
- [ ] 7.2 Update the "Logging placement rule" entry in `CLAUDE.md` to note that `KafkaPublisher` now accepts an optional `ILoggerFactory?` (null → `NullLoggerFactory.Instance`) for the same reason as `RabbitMqPublisher`.

## 8. Verification

- [ ] 8.1 Run `dotnet build RayTree.slnx -c Release` and confirm no new warnings.
- [ ] 8.2 Run `dotnet test tests/RayTree.Plugins.Kafka.Tests` (unit tests) and confirm green.
- [ ] 8.3 Run the integration tests against a local Docker Kafka and confirm green.
