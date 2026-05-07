# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Test Commands

```bash
# Build (Debug — no doc requirements)
dotnet build RayTree.sln

# Build (Release — used by CI)
dotnet build RayTree.sln -c Release

# Run all unit tests (no Docker required)
dotnet test tests/RayTree.Core.Tests
dotnet test tests/RayTree.Plugins.InMemory.Tests
dotnet test tests/RayTree.Subscriber.Tests
dotnet test tests/RayTree.EntityFrameworkCore.Tests
dotnet test tests/RayTree.Plugins.Compressors.{Brotli,Gzip,Lz4}.Tests
dotnet test tests/RayTree.Plugins.Serializers.{Json,MessagePack,Protobuf}.Tests

# Run a single test by name
dotnet test tests/RayTree.Subscriber.Tests --filter "FullyQualifiedName~NoSerializer"

# Run integration tests (requires Docker — spins up containers via Testcontainers)
dotnet test tests/RayTree.Plugins.PostgreSQL.Tests
dotnet test tests/RayTree.Plugins.RabbitMQ.Tests
dotnet test tests/RayTree.Plugins.Kafka.Tests
```

`TreatWarningsAsErrors=true` is global. Nullable warnings are always errors. All new public code must satisfy these constraints.

Centralized package versions live in `Directory.Packages.props`. Add new packages there; reference them in `.csproj` without a version attribute.

## Architecture Overview

RayTree is a modular .NET 8 entity change-tracking library built on the **outbox pattern**. All change tracking flows through:

```
EntityChangeTracker
  → IOutbox (persist change + entity state)
  → OutboxPublisherService (polls outbox, serializes, publishes)
  → IQueuePublisher (broker-specific)
      ↓ MessageEnvelope (meta headers + byte[] Payload)
  → IQueueConsumer
  → ChangeSubscriber (dedup, decompress, deserialize, dispatch)
  → ChangeHandlerAsync<TEntity>(EntityChange<TEntity>, CancellationToken)
```

### Core (`src/RayTree.Core`)

- **`EntityChangeTracker`** — central engine. Holds per-entity registrations (outbox, publisher, serializer, compressor, repository) in `ConcurrentDictionary<Type, …>`. `InitializeAsync()` calls `InitializeAsync()` on each plugin, then starts one `OutboxPublisherService` per entity type.
- **`ChangeTrackingBuilder` / `IChangeTrackingBuilder`** — fluent builder. Global factories (`UseOutbox<T>(Func<Type,…>)`) apply to all entity types; per-entity overrides via `.ForEntity<T>()` take precedence.
- **`OutboxPublisherService`** — background polling loop: reads unpublished changes → serialize → compress → wrap in `MessageEnvelope` → publish → mark published.
- **`MessageEnvelope`** — the only thing that crosses the queue boundary. Contains metadata fields plus `byte[] Payload` (already serialized+compressed entity state). Serialization happens on the publisher side; deserialization on the subscriber side.
- **`IChangeSerializer`** / **`IChangeCompressor`** — stream-based interfaces. Serializers handle `EntityChange<TEntity>` with typed `State`. `NoOpCompressorPlugin` (built-in) is a passthrough for testing.

### Publisher-side plugins

| Package | What it provides |
|---|---|
| `RayTree.Plugins.PostgreSQL` | `PostgreSqlOutbox<TEntity>` — stores changes as flat columns (one column per entity property via `EntityColumnMapper`). `NotificationBasedPublisher` — NOTIFY/LISTEN fast-path with polling fallback. |
| `RayTree.Plugins.InMemory` | `InMemoryQueue` implements both `IQueuePublisher` and `IQueueConsumer` via `Channel<MessageEnvelope>`. Use for tests and local dev. |
| `RayTree.Plugins.Kafka` | `KafkaPublisher` + `KafkaConsumer`. Consumer uses a dedicated background thread (channel-based) because Confluent.Kafka requires all `Consume`/`Commit` calls on one thread. |
| `RayTree.Plugins.RabbitMQ` | `RabbitMqPublisher` + `RabbitMqConsumer`. Consumer uses `AsyncEventingBasicConsumer` buffered via `Channel<MessageEnvelope>`. |
| `RayTree.Plugins.Serializers.*` | JSON, MessagePack, Protobuf — each in its own package. |
| `RayTree.Plugins.Compressors.*` | Gzip, Brotli, LZ4 — each in its own package. |

### Subscriber-side (`src/RayTree.Subscriber`)

- **`ChangeSubscriber`** — receives `MessageEnvelope` from an `IQueueConsumer`, deduplicates on `CorrelationId` via `IDeduplicationStore`, decompresses and deserializes the payload using reflection (`DeserializeCoreAsync<TEntity>` invoked via `MethodInfo.MakeGenericMethod`), then dispatches to registered `ChangeHandlerAsync<TEntity>` handlers with retry.
- Handler signature: `ChangeHandlerAsync<TEntity>(EntityChange<TEntity> change, CancellationToken ct)`. `change.State` is the fully-typed entity; it is `null` when no serializer is registered for that entity type.
- **`ChangeSubscriberConfiguration`** — fluent builder that mirrors publisher-side ergonomics. `Build()` produces a `ChangeSubscriber`.
- **`ChangeSubscriberHostedService`** — ASP.NET Core hosted service that calls `InitializeAsync()` on each registered queue then launches one background `ConsumeFromConsumerAsync` task per entity queue.
- **`IDeduplicationStore`** — `InMemoryDeduplicationStore` (default), `RedisDeduplicationStore` for distributed deployments.
- **`SubscriberOptions`** — `MaxRetries` (retry *attempts* after the first call), `RetryDelay`, `SkipOnFailure`.

### .NET Generic Host integration (`src/RayTree.Hosting`)

- `AddChangeTracking(services, configuration, configure)` registers `EntityChangeTracker` as a singleton and `OutboxPublisherHostedService` as a hosted service.
- `AddChangeSubscriber(services, configuration)` registers `ChangeSubscriber` as a singleton and `ChangeSubscriberHostedService` as a hosted service. Options are bound from `IConfiguration` section `ChangeTracking:Subscriber`.

### EF Core integration (`src/RayTree.EntityFrameworkCore`)

`EntityChangeInterceptor` hooks into `SaveChangesAsync` to automatically call `TrackInsertAsync`/`TrackUpdateAsync`/`TrackDeleteAsync` based on EF change tracker state.

## Key Design Decisions

- **Reflection for generic dispatch**: `OutboxPublisherService`, `NotificationBasedPublisher`, and `ChangeSubscriber` all use `MethodInfo.MakeGenericMethod` to invoke serializer/deserializer methods with the runtime entity type. Return types are declared as the non-generic base (`Task<EntityChange>`) so the async upcast works cleanly.
- **PostgreSQL outbox schema**: Each entity type gets its own outbox table. Entity properties are stored as flat columns (not JSON), derived via `EntityColumnMapper.GetColumns(typeof(TEntity))`. Column names are `snake_case`.
- **Kafka thread safety**: `KafkaConsumer` keeps a single background `Task.Run` thread that owns all `IConsumer<K,V>` operations. `Dispose()` cancels via `_disposeCts`, waits up to `2×PollTimeoutMs + 200 ms` for the poll task to exit, then frees the native handle.
- **Integration tests use Testcontainers**: PostgreSQL, Kafka, and RabbitMQ tests require Docker. Mark test classes `[NonParallelizable]` when sharing a container. Use unique topic/queue names per test to avoid cross-test contamination.

## CI

`.github/workflows/ci.yml` has three jobs: `build` (compile gate), `unit-tests` (10 projects, no Docker), `integration-tests` (matrix: PostgreSQL / RabbitMQ / Kafka). Jobs do not share filesystem state — each job independently restores and builds. The `build` job is a fast fail-early gate only.
