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
dotnet test tests/RayTree.EntityFrameworkCore.Tests
dotnet test tests/RayTree.Plugins.Compressors.{Brotli,Gzip,Lz4}.Tests
dotnet test tests/RayTree.Plugins.Serializers.{Json,MessagePack,Protobuf}.Tests

# Run a single test by name
dotnet test tests/RayTree.Core.Tests --filter "FullyQualifiedName~NoSerializer"

# Run integration tests (requires Docker — spins up containers via Testcontainers)
dotnet test tests/RayTree.Plugins.PostgreSQL.Tests
dotnet test tests/RayTree.Plugins.RabbitMQ.Tests
dotnet test tests/RayTree.Plugins.Kafka.Tests
```

`TreatWarningsAsErrors=true` is global. Nullable warnings are always errors. All new public code must satisfy these constraints.

Centralized package versions live in `Directory.Packages.props`. Add new packages there; reference them in `.csproj` without a version attribute.

## Architecture Overview

RayTree is a modular .NET 8 entity change-tracking library built on the **outbox pattern**. All change tracking flows through a single `EntityChangeTracker` that acts as the unified host for both the publisher and subscriber pipelines:

```
EntityChangeTracker
  → IOutbox (persist change + entity state)
  → OutboxPublisherService (polls outbox, serializes, publishes)
  → IQueuePublisher (broker-specific)
      ↓ MessageEnvelope (meta headers + byte[] Payload)
  → IQueueConsumer
  → ChangeSubscriber (internal: dedup, decompress, deserialize, dispatch)
  → ChangeHandlerAsync<TEntity>(EntityChange<TEntity>, CancellationToken)
```

### Core (`src/RayTree.Core`)

- **`EntityChangeTracker`** — the single runtime host. Holds per-entity publisher registrations (outbox, publisher, serializer, compressor, repository) in `ConcurrentDictionary<Type, …>` and owns an internal `ChangeSubscriber` for the consumer side. `InitializeAsync()` initializes all plugins and starts one `OutboxPublisherService` per entity type; it also initializes any registered consumer queues. Exposes `Consumers`, `ConsumeFromConsumerAsync`, and `ProcessMessageAsync` as the public subscriber API.
- **`ChangeTrackingBuilder` / `IChangeTrackingBuilder`** — unified fluent builder for both sides. Global factories (`UseOutbox<T>`, `UseSerializer<T>`, etc.) apply to all entity types. `UseSerializer`/`UseCompressor` at the global level forward to both the publisher factory and the subscriber's global instance. `UseSubscriberOptions` and `UseDeduplicationStore` configure the subscriber globally. Per-entity overrides live inside `.ForEntity<TEntity>(Action<IEntityBuilder<TEntity>>)` which exposes both publisher methods (`UseOutbox`, `UseQueue`, `UseSerializer`, `UseCompressor`, `UseRepository`) and subscriber methods (`UseConsumer`, `OnInsert`, `OnUpdate`, `OnDelete`, `OnChange`, `UseSubscriberOptions`). `Build()` / `BuildAsync()` produce a fully initialized `EntityChangeTracker` with the subscriber already attached.
- **`IEntityBuilder<TEntity>`** — generic per-entity configuration interface. Publisher side: `UseOutbox`, `UseQueue(IQueuePublisher)`, `UseSerializer`, `UseCompressor`, `UseRepository`. Subscriber side: `UseConsumer(IQueueConsumer)`, `UseSubscriberOptions`, `OnInsert`, `OnUpdate`, `OnDelete`, `OnChange`. `where TEntity : class` is required because subscriber handler registration is typed.
- **`OutboxPublisherService`** — background polling loop: reads unpublished changes → serialize → compress → wrap in `MessageEnvelope` → publish → mark published.
- **`MessageEnvelope`** — the only thing that crosses the queue boundary. Contains metadata fields plus `byte[] Payload` (already serialized+compressed entity state). Serialization happens on the publisher side; deserialization on the subscriber side.
- **`IChangeSerializer`** / **`IChangeCompressor`** — stream-based interfaces. Serializers handle `EntityChange<TEntity>` with typed `State`. `NoOpCompressorPlugin` (built-in) is a passthrough for testing.
- **`ChangeSubscriber`** — internal implementation detail owned by `EntityChangeTracker`. Receives `MessageEnvelope` from an `IQueueConsumer`, deduplicates on `CorrelationId` via `IDeduplicationStore`, decompresses and deserializes the payload using reflection (`DeserializeCoreAsync<TEntity>` via `MethodInfo.MakeGenericMethod`), then dispatches to registered `ChangeHandlerAsync<TEntity>` handlers with retry. Still public for direct low-level usage (e.g., standalone subscriber tests), but not the primary API.
- **`ChangeSubscriberBuilder` / `IChangeSubscriberBuilder`** — standalone subscriber-only builder. Useful when running a subscriber-only service. Produces a `ChangeSubscriber` via `Build()`. `ChangeTrackingBuilder` composes one internally and calls `Apply(EntityChangeTracker)` during `BuildInternal()` to attach the configured subscriber to the tracker.

### Publisher-side plugins

| Package | What it provides |
|---|---|
| `RayTree.Plugins.PostgreSQL` | `PostgreSqlOutbox<TEntity>` — stores changes as flat columns (one column per entity property via `EntityColumnMapper`). `NotificationBasedPublisher` — NOTIFY/LISTEN fast-path with polling fallback. |
| `RayTree.Plugins.InMemory` | `InMemoryQueue` implements both `IQueuePublisher` and `IQueueConsumer` via `Channel<MessageEnvelope>`. Use for tests and local dev. |
| `RayTree.Plugins.Kafka` | `KafkaPublisher` + `KafkaConsumer`. Consumer uses a dedicated background thread (channel-based) because Confluent.Kafka requires all `Consume`/`Commit` calls on one thread. |
| `RayTree.Plugins.RabbitMQ` | `RabbitMqPublisher` + `RabbitMqConsumer`. Consumer uses `AsyncEventingBasicConsumer` buffered via `Channel<MessageEnvelope>`. |
| `RayTree.Plugins.Serializers.*` | JSON, MessagePack, Protobuf — each in its own package. |
| `RayTree.Plugins.Compressors.*` | Gzip, Brotli, LZ4 — each in its own package. |

### Subscriber-side (`src/RayTree.Core/Handling`)

- **`ChangeSubscriber`** — see Core section above.
- Handler signature: `ChangeHandlerAsync<TEntity>(EntityChange<TEntity> change, CancellationToken ct)`. `change.State` is the fully-typed entity; it is `null` when no serializer is registered for that entity type.
- **`IDeduplicationStore`** — `InMemoryDeduplicationStore` (default), `RedisDeduplicationStore` for distributed deployments.
- **`SubscriberOptions`** — `MaxRetries` (retry *attempts* after the first call), `RetryDelay`, `SkipOnFailure`.

### .NET Generic Host integration (`src/RayTree.Hosting`)

- `AddChangeTracking(services, configuration, configure)` — the primary registration path. Registers `EntityChangeTracker` as a singleton (publisher + subscriber configured together) and `ChangeTrackingHostedService` as a hosted service. Publisher loops are started during `EntityChangeTracker.InitializeAsync()` (called inside `Build()`). The hosted service starts consumer loops from `tracker.Consumers` on application startup. Publisher options are bound from `ChangeTracking:Publisher`; subscriber options from `ChangeTracking:Subscriber`.
- `AddChangeSubscriber(services, configuration)` — standalone subscriber-only path. Registers a separate `ChangeSubscriber` singleton and `ChangeSubscriberHostedService`. Use only when running a subscriber-only service without a tracker. Returns `IChangeSubscriberBuilder` for fluent configuration.
- **`ChangeTrackingHostedService`** — unified hosted service that starts consumer consume loops from `tracker.Consumers`. Publisher loops are not started here (they are already running from `InitializeAsync`).

### EF Core integration (`src/RayTree.EntityFrameworkCore`)

`EntityChangeInterceptor` hooks into `SaveChangesAsync` to automatically call `TrackInsertAsync`/`TrackUpdateAsync`/`TrackDeleteAsync` based on EF change tracker state.

## Key Design Decisions

- **Unified builder**: `IChangeTrackingBuilder.ForEntity<TEntity>()` takes `Action<IEntityBuilder<TEntity>>` where `IEntityBuilder<TEntity>` covers both publisher and subscriber configuration. The `where TEntity : class` constraint is required by the subscriber handler registration. Value types cannot be entity types. `UseSerializer`/`UseCompressor` at the global level forward the factory's output to the subscriber's global instance by calling `factory(typeof(object))` — this works correctly when the factory ignores the type parameter (the common case).
- **Tracker as single host**: `EntityChangeTracker` owns the `ChangeSubscriber` internally via composition. `ChangeSubscriberBuilder.Apply(EntityChangeTracker)` is called at the end of `ChangeTrackingBuilder.BuildInternal()` to create the subscriber and attach it. The tracker exposes `Consumers`, `ConsumeFromConsumerAsync`, and `ProcessMessageAsync` by delegating to the internal subscriber. `ChangeSubscriber` stays public for low-level testing but is not part of the primary API.
- **Publisher loop lifetime**: `OutboxPublisherService` instances are created and started inside `EntityChangeTracker.InitializeAsync()`, which is called synchronously by `Build()`. `ChangeTrackingHostedService` does **not** create additional publisher services — doing so would cause duplication. The hosted service only manages consumer loops.
- **Reflection for generic dispatch**: `OutboxPublisherService`, `NotificationBasedPublisher`, and `ChangeSubscriber` all use `MethodInfo.MakeGenericMethod` to invoke serializer/deserializer methods with the runtime entity type. Return types are declared as the non-generic base (`Task<EntityChange>`) so the async upcast works cleanly.
- **PostgreSQL outbox schema**: Each entity type gets its own outbox table. Entity properties are stored as flat columns (not JSON), derived via `EntityColumnMapper.GetColumns(typeof(TEntity))`. Column names are `snake_case`.
- **Kafka thread safety**: `KafkaConsumer` keeps a single background `Task.Run` thread that owns all `IConsumer<K,V>` operations. `Dispose()` cancels via `_disposeCts`, waits up to `2×PollTimeoutMs + 200 ms` for the poll task to exit, then frees the native handle.
- **Integration tests use Testcontainers**: PostgreSQL, Kafka, and RabbitMQ tests require Docker. Mark test classes `[NonParallelizable]` when sharing a container. Use unique topic/queue names per test to avoid cross-test contamination.

## CI

`.github/workflows/ci.yml` has three jobs: `build` (compile gate), `unit-tests` (10 projects, no Docker), `integration-tests` (matrix: PostgreSQL / RabbitMQ / Kafka). Jobs do not share filesystem state — each job independently restores and builds. The `build` job is a fast fail-early gate only.
