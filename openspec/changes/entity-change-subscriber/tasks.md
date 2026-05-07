## 1. Transport Layer — MessageEnvelope

- [x] 1.1 Define `MessageEnvelope` model in `src/RayTree.Core/Models/MessageEnvelope.cs` with metadata fields (EntityType, EntityId, ChangeType, CorrelationId, Version, Timestamp) and `byte[] Payload`
- [x] 1.2 Update `IQueuePublisher.PublishAsync` to accept `MessageEnvelope` instead of raw `EntityChange`
- [x] 1.3 Define `IQueueConsumer` interface in `src/RayTree.Core/Plugins/Consumer/IQueueConsumer.cs` with `InitializeAsync` and `ConsumeAsync` returning `IAsyncEnumerable<MessageEnvelope>`
- [x] 1.4 Update `OutboxPublisherService` to serialize+compress entity state into `MessageEnvelope.Payload` before publishing

## 2. Queue Plugin Implementations

- [x] 2.1 Update `InMemoryQueue` to implement both `IQueuePublisher` and `IQueueConsumer` using `Channel<MessageEnvelope>`
- [x] 2.2 Implement `KafkaConsumer` with channel-based poll loop (all Confluent.Kafka ops on one thread to satisfy single-thread requirement); store meta in Kafka headers, payload as message value
- [x] 2.3 Implement `KafkaConsumerOptions` with BootstrapServers, Topic, GroupId, FromEarliest, PollTimeoutMs
- [x] 2.4 Update `KafkaPublisher.PublishAsync` to build `MessageEnvelope` from headers + payload bytes
- [x] 2.5 Add `KafkaSubscriberExtensions` fluent configuration helpers
- [x] 2.6 Implement `RabbitMqConsumer` with `AsyncEventingBasicConsumer` buffered via `Channel<MessageEnvelope>`; meta in AMQP headers, payload as message body
- [x] 2.7 Implement `RabbitMqConsumerOptions` with HostName, Port, UserName, Password, QueueName, exchange/binding settings, PrefetchCount, DeclareQueue, Durable
- [x] 2.8 Update `RabbitMqPublisher.PublishAsync` to write envelope meta as AMQP headers and `Payload` as body
- [x] 2.9 Add `RabbitMqSubscriberExtensions` fluent configuration helpers
- [x] 2.10 Update `NotificationBasedPublisher` (PostgreSQL) to construct `MessageEnvelope` before publishing

## 3. Subscriber — Typed Handler

- [x] 3.1 Define `ChangeHandlerAsync<TEntity>` delegate: `(EntityChange<TEntity> change, CancellationToken ct) => Task`
- [x] 3.2 Update `HandlerRegistration` to store handler as `Func<EntityChange, CancellationToken, Task>` wrapper
- [x] 3.3 Update `ChangeSubscriber.OnChange<TEntity>` to accept `ChangeHandlerAsync<TEntity>` and wrap it so `EntityChange<TEntity>` cast is always valid at invocation
- [x] 3.4 Implement `ChangeSubscriber.DeserializeEnvelopeAsync`: decompress payload then reflect-invoke `DeserializeCoreAsync<TEntity>` to produce typed `EntityChange<TEntity>`
- [x] 3.5 Implement fallback for when no serializer is registered: use `Activator.CreateInstance(typeof(EntityChange<>).MakeGenericType(entityType))` so handler cast always succeeds with `State = null`
- [x] 3.6 Update `ChangeSubscriber.ProcessMessageAsync` to accept `MessageEnvelope`, run deduplication, deserialize, then invoke handlers
- [x] 3.7 Update `ChangeSubscriber.ConsumeFromConsumerAsync` to iterate `IAsyncEnumerable<MessageEnvelope>` from `IQueueConsumer`

## 4. Subscriber Configuration & DI

- [x] 4.1 Update `ChangeSubscriberConfiguration` with `UseQueue<T>`, `UseSerializer<T>`, `UseCompressor<T>`, `OnChange<T>`, `OnInsert<T>`, `OnUpdate<T>`, `OnDelete<T>`, `UseDeduplicationStore`, `UseRedisDeduplication`
- [x] 4.2 Add `InMemorySubscriberConfigurationExtensions.UseInMemoryQueue<TEntity>` extension method
- [x] 4.3 Implement `ServiceCollectionExtensions.AddChangeSubscriber` registering `ChangeSubscriber` as singleton (built lazily from DI-resolved `SubscriberOptions` and `IDeduplicationStore`) and `ChangeSubscriberHostedService` as hosted service
- [x] 4.4 Bind `SubscriberOptions` from `IConfiguration` section `ChangeTracking:Subscriber` when configuration is provided
- [x] 4.5 Implement `ChangeSubscriberHostedService.StartAsync` to call `InitializeAsync` on each registered queue then launch a background consume loop per queue
- [x] 4.6 Implement `ChangeSubscriberHostedService.StopAsync` to cancel all loops and await `Task.WhenAll` gracefully

## 5. Retry Logic

- [x] 5.1 Implement `InvokeWithRetryAsync` with `MaxRetries` meaning the number of retry attempts after the initial call (total attempts = MaxRetries + 1)
- [x] 5.2 Apply `RetryDelay × attempt` back-off between retries
- [x] 5.3 Honor `SkipOnFailure`: swallow exception after retries exhausted instead of re-throwing

## 6. CI Pipeline

- [x] 6.1 Add `.github/workflows/ci.yml` with three jobs: `build` (fail-fast compilation gate), `unit-tests` (10 projects, no Docker), `integration-tests` (matrix: PostgreSQL / RabbitMQ / Kafka via Testcontainers)
- [x] 6.2 Cache NuGet packages keyed on `Directory.Packages.props` + all `*.csproj` hashes
- [x] 6.3 Remove stale Testcontainers references from `RayTree.EntityFrameworkCore.Tests.csproj` and move it to the unit-tests job

## 7. Testing

- [x] 7.1 Add `InMemoryEndToEndTests`: Insert, Update, Delete, NullChangeType (all types), Deduplication, Build-path (via `ChangeSubscriberConfiguration`)
- [x] 7.2 Add `NoSerializer_HandlerReceivesTypedChangeWithNullState` test covering `Activator.CreateInstance` fallback
- [x] 7.3 Add `InvokeWithRetry_HandlerSucceedsAfterRetries` test (MaxRetries=2, succeeds on 3rd call)
- [x] 7.4 Add `InvokeWithRetry_MaxRetries1_ExhaustedWithSkipOnFailure_DoesNotThrow` test
- [x] 7.5 Add `KafkaEndToEndTests`: Insert, Update, batch (all change types in order) using Testcontainers
- [x] 7.6 Add `RabbitMqEndToEndTests`: Insert, Update, Delete using Testcontainers
