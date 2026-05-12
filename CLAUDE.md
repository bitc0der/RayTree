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

- **`EntityChangeTracker`** — the single runtime host. Constructor-injects a `ChangePublisher` (required) and a `ChangeSubscriber?` (optional). `InitializeAsync()` calls `ChangePublisher.InitializeAsync()` (which starts one `OutboxPublisherService` per entity type) then initializes registered consumer queues. Exposes `Publisher`, `Subscriber`, `ConsumeFromConsumerAsync`, and `ProcessMessageAsync` as its public surface. `TrackXxxAsync` writes to `Publisher.GetOutbox(entityType)`.
- **`ChangeTrackingBuilder` / `IChangeTrackingBuilder`** — unified fluent builder for both sides. Accepts an optional `ILoggerFactory?` constructor parameter; `null` normalizes to `NullLoggerFactory.Instance`, so existing call-sites that omit it continue to work. Global factories (`UseOutbox<T>`, `UseSerializer<T>`, etc.) apply to all entity types. `UseSerializer`/`UseCompressor` at the global level forward to both the publisher factory and the subscriber's global instance. `UseSubscriberOptions` and `UseDeduplicationStore` configure the subscriber globally. Per-entity overrides live inside `.ForEntity<TEntity>(Action<IEntityBuilder<TEntity>>)` which exposes both publisher methods (`UseOutbox`, `UseQueue`, `UseSerializer`, `UseCompressor`, `UseRepository`) and subscriber methods (`UseConsumer`, `OnInsert`, `OnUpdate`, `OnDelete`, `OnChange`, `UseSubscriberOptions`). `Build()` / `BuildAsync()` produce a fully initialized `EntityChangeTracker` with the subscriber already attached.
- **`IEntityBuilder<TEntity>`** — generic per-entity configuration interface. Publisher side: `UseOutbox`, `UseQueue(IQueuePublisher)`, `UseSerializer`, `UseCompressor`, `UseRepository`. Subscriber side: `UseConsumer(IQueueConsumer)`, `UseSubscriberOptions`, `OnInsert`, `OnUpdate`, `OnDelete`, `OnChange`. `where TEntity : class` is required because subscriber handler registration is typed.
- **`ChangePublisher`** — owns all publisher-side plugin registrations (`IOutbox`, `IQueuePublisher`, `IChangeSerializer`, `IChangeCompressor`, `IRepository` per entity type) in `ConcurrentDictionary<Type, …>`, and manages the `OutboxPublisherService` instances. Constructor requires `ILoggerFactory loggerFactory`. `InitializeAsync()` initializes repositories, outboxes, publishers, then starts one `OutboxPublisherService` per registered entity type. Parallel to `ChangeSubscriber` on the subscriber side.
- **`OutboxPublisherService`** — background polling loop per entity type: reads unpublished changes → serialize → compress → wrap in `MessageEnvelope` → publish → mark published → rotate outbox (see below). Constructor signature: `(ChangePublisher, Type entityType, OutboxPublisherOptions, ILoggerFactory)`. Logs start/stop at `Information`; batch errors at `Error`; per-retry warnings at `Warning`; exhausted retries at `Error`. After each batch it calls `MaybeRunCleanupAsync`, which fires once on the first tick and then respects `OutboxPublisherOptions.CleanupInterval` (default 1 h). Rotation logs: start at `Debug`; records deleted at `Information`; nothing to delete at `Debug`; stale unpublished records found at `Warning`; errors at `Error` (isolated — a cleanup failure does not abort the publish loop).
- **`OutboxPublisherOptions`** — `PollingInterval` (default 5 s), `BatchSize` (default 100), `MaxRetryCount` (default 3), `RetryDelay` (default 1 s), `UseNotificationChannel` / `NotificationChannel` / `FallbackPollingInterval` for PostgreSQL NOTIFY/LISTEN. Rotation options: `CleanupRetentionPeriod` (default 7 days — how old a published record must be before deletion), `CleanupInterval` (default 1 h — how often rotation runs), `StaleUnpublishedThreshold` (default `null` — opt-in; when set, unpublished records older than this value are also removed with a `Warning` log so operators notice stuck queues).
- **`MessageEnvelope`** — the only thing that crosses the queue boundary. Contains metadata fields plus `byte[] Payload` (already serialized+compressed entity state). Serialization happens on the publisher side; deserialization on the subscriber side.
- **`IChangeSerializer`** / **`IChangeCompressor`** — stream-based interfaces. Serializers handle `EntityChange<TEntity>` with typed `State`. `NoOpCompressorPlugin` (built-in) is a passthrough for testing.
- **`IOutbox`** — seven methods: `InitializeAsync`, `WriteAsync<TEntity>`, two `GetUnpublishedAsync<TEntity>` overloads (by batch size; or by `ChangeType?`, `DateTime? since`, batch size), `MarkPublishedAsync`, `GetByIdAsync<TEntity>`, `CleanupPublishedAsync(retentionPeriod)`, `CleanupStaleUnpublishedAsync(staleThreshold)`. Both cleanup methods return the count of rows deleted.
- **`OutboxCleanupService`** — standalone manual trigger for outbox rotation. Takes `IEnumerable<IOutbox>`, `ILogger<OutboxCleanupService>`, and optional `retentionPeriod` (default 7 days). `RunCleanupAsync()` calls `CleanupPublishedAsync` on every registered outbox and sums the results. Registered as a singleton by `AddChangeTracking` using `OutboxPublisherOptions.CleanupRetentionPeriod`. Useful for one-off or scheduled cleanup outside the normal poll loop.
- **`ChangeSubscriber`** — internal implementation detail owned by `EntityChangeTracker`. Receives `MessageEnvelope` from an `IQueueConsumer`, deduplicates on `CorrelationId` via `IDeduplicationStore`, decompresses and deserializes the payload using reflection (`DeserializeCoreAsync<TEntity>` via `MethodInfo.MakeGenericMethod`), then dispatches to registered `ChangeHandlerAsync<TEntity>` handlers with retry. Still public for direct low-level usage (e.g., standalone subscriber tests), but not the primary API. Constructor signature: `(ILogger<ChangeSubscriber> logger, IDeduplicationStore? dedupStore = null, SubscriberOptions? options = null)` — logger is the required first parameter. Logs unknown entity types at `Warning`; dedup hits at `Debug`; no-handler-match at `Debug`; retry attempts at `Warning`; SkipOnFailure drops at `Error`.
- **`ChangeSubscriberBuilder` / `IChangeSubscriberBuilder`** — standalone subscriber-only builder. Produces a `ChangeSubscriber` via `Build()`. `ChangeTrackingBuilder` composes one internally; `BuildInternal()` calls `_subscriberBuilder.Build()` and passes the result to the `EntityChangeTracker` constructor.

### Publisher-side plugins

| Package | What it provides |
|---|---|
| `RayTree.Plugins.PostgreSQL` | `PostgreSqlOutbox<TEntity>` — stores changes as flat columns (one column per entity property via `EntityColumnMapper`). `EntityColumnMapper` honours `System.ComponentModel.DataAnnotations` / `Schema` attributes: `[NotMapped]` excludes a property; `[Column("name")]` overrides the column name suffix (the `state_` prefix is always kept to avoid collisions with outbox metadata columns); `[Column(TypeName = "JSONB")]` sets the PostgreSQL type verbatim; `[Required]` forces `NOT NULL` on reference types; `[MaxLength(n)]`/`[StringLength(n)]` emits `VARCHAR(n)` instead of `TEXT`; `[Table("name")]` on the entity class is used as the base name when deriving default outbox/source table names; `[Key]` (one or more properties) identifies the business primary key — `PostgreSqlRepository` uses these for INSERT/UPDATE/DELETE/SELECT and adds a UNIQUE index on the corresponding `state_*` columns in the source table; for composite keys pair `[Key]` with `[Column(Order = n)]` to control column order. Both `CleanupPublishedAsync` and `CleanupStaleUnpublishedAsync` delete in batches (`PostgreSqlOutboxOptions.CleanupBatchSize`, default 1000) using a `DELETE … WHERE id IN (SELECT id … LIMIT @BatchSize)` loop to avoid large single-statement locks and WAL spikes. `NotificationBasedPublisher` — NOTIFY/LISTEN fast-path with polling fallback. |
| `RayTree.Plugins.InMemory` | `InMemoryQueue` implements both `IQueuePublisher` and `IQueueConsumer` via `Channel<MessageEnvelope>`. Use for tests and local dev. |
| `RayTree.Plugins.Kafka` | `KafkaPublisher` + `KafkaConsumer`. Consumer uses a dedicated background thread (channel-based) because Confluent.Kafka requires all `Consume`/`Commit` calls on one thread. `KafkaConsumer(KafkaConsumerOptions, ILoggerFactory)` — both params required. |
| `RayTree.Plugins.RabbitMQ` | `RabbitMqPublisher` + `RabbitMqConsumer`. Consumer uses `AsyncEventingBasicConsumer` buffered via `Channel<MessageEnvelope>`. `RabbitMqConsumer(RabbitMqConsumerOptions, ILoggerFactory)` — both params required. |
| `RayTree.Plugins.Serializers.*` | JSON, MessagePack, Protobuf — each in its own package. |
| `RayTree.Plugins.Compressors.*` | Gzip, Brotli, LZ4 — each in its own package. |

### Subscriber-side (`src/RayTree.Core/Handling`)

- **`ChangeSubscriber`** — see Core section above.
- Handler signature: `ChangeHandlerAsync<TEntity>(EntityChange<TEntity> change, CancellationToken ct)`. `change.State` is the fully-typed entity; it is `null` when no serializer is registered for that entity type.
- **`IDeduplicationStore`** — `InMemoryDeduplicationStore` (default), `RedisDeduplicationStore` for distributed deployments.
- **`SubscriberOptions`** — `MaxRetries` (retry *attempts* after the first call), `RetryDelay`, `SkipOnFailure`.

### .NET Generic Host integration (`src/RayTree.Hosting`)

- `AddChangeTracking(services, configuration, configure)` — the primary registration path. Registers `EntityChangeTracker` as a singleton (publisher + subscriber configured together) and `ChangeTrackingHostedService` as a hosted service. Resolves `ILoggerFactory` from the DI container and passes it to `new ChangeTrackingBuilder(loggerFactory)` — no explicit logging setup is required from the caller. Publisher loops are started during `EntityChangeTracker.InitializeAsync()` (called inside `Build()`). The hosted service starts consumer loops from `tracker.Subscriber?.Queues` on application startup. Publisher options are bound from `ChangeTracking:Publisher`; subscriber options from `ChangeTracking:Subscriber`.
- **`ChangeTrackingHostedService`** — unified hosted service that starts consumer loops from `tracker.Subscriber?.Queues`. Publisher loops are not started here (they are already running from `InitializeAsync`). If the tracker has no subscriber (publisher-only deployment), `StartAsync` is a no-op. Constructor requires `(EntityChangeTracker, ILogger<ChangeTrackingHostedService>)` — both are auto-wired by the DI container. Logs consumer loop start at `Information`; graceful shutdown at `Information`.

### EF Core integration (`src/RayTree.EntityFrameworkCore`)

`EntityChangeInterceptor` hooks into `SaveChangesAsync` to automatically call `TrackInsertAsync`/`TrackUpdateAsync`/`TrackDeleteAsync` based on EF change tracker state.

## Key Design Decisions

- **Unified builder**: `IChangeTrackingBuilder.ForEntity<TEntity>()` takes `Action<IEntityBuilder<TEntity>>` where `IEntityBuilder<TEntity>` covers both publisher and subscriber configuration. The `where TEntity : class` constraint is required by the subscriber handler registration. Value types cannot be entity types. `UseSerializer`/`UseCompressor` at the global level forward the factory's output to the subscriber's global instance by calling `factory(typeof(object))` — this works correctly when the factory ignores the type parameter (the common case).
- **Tracker as thin coordinator**: `EntityChangeTracker` composes a `ChangePublisher` (required) and a `ChangeSubscriber?` (optional) via constructor injection. `ChangeTrackingBuilder.BuildInternal()` creates the `ChangePublisher`, registers all plugins on it, then builds the subscriber via `_subscriberBuilder.Build()` and passes both to the `EntityChangeTracker` constructor. Neither class exposes delegation wrappers on the tracker — callers use `tracker.Publisher` and `tracker.Subscriber` directly. Both remain public for low-level testing but are not part of the primary API.
- **Publisher loop lifetime**: `OutboxPublisherService` instances are created and started inside `EntityChangeTracker.InitializeAsync()`, which is called synchronously by `Build()`. `ChangeTrackingHostedService` does **not** create additional publisher services — doing so would cause duplication. The hosted service only manages consumer loops.
- **Reflection for generic dispatch**: `OutboxPublisherService`, `NotificationBasedPublisher`, and `ChangeSubscriber` all use `MethodInfo.MakeGenericMethod` to invoke serializer/deserializer methods with the runtime entity type. Return types are declared as the non-generic base (`Task<EntityChange>`) so the async upcast works cleanly.
- **PostgreSQL outbox schema**: Each entity type gets its own outbox table. Entity properties are stored as flat columns (not JSON), derived via `EntityColumnMapper.GetColumns(typeof(TEntity))`. By default column names are `state_<snake_case>` and the table name is `<snake_case>_outbox`. Both are customisable via `System.ComponentModel.DataAnnotations` / `Schema` attributes on the entity class — see the `RayTree.Plugins.PostgreSQL` plugin row above for the full attribute reference. `EntityColumnMapper.GetTableName(Type)` encapsulates the `[Table]`-aware table-name logic and is the single place both `PostgreSqlOutbox` and `PostgreSqlRepository` use for their defaults. Three indexes are created at `InitializeAsync` time: `idx_*_outbox_unpublished` — partial on `(published, timestamp) WHERE published = FALSE`, used by `GetUnpublishedAsync`; `idx_*_outbox_cleanup` — partial on `(timestamp) WHERE published = TRUE`, used by `CleanupPublishedAsync`; `idx_*_outbox_entity` — on `(entity_type, published, timestamp)`, used by the filtered `GetUnpublishedAsync` overload.
- **PostgreSQL primary key resolution**: `EntityColumnMapper.GetKeyProperties(Type)` returns the ordered list of key properties for an entity. It first looks for properties annotated with `[Key]`; multiple `[Key]` properties form a composite key ordered by `[Column(Order)]` then by declaration order. If no `[Key]` is found it falls back to the `Id` convention property. If neither exists it throws `InvalidOperationException` at construction time (fail-fast). `PostgreSqlRepository` uses key properties to build INSERT, WHERE (UPDATE/DELETE/SELECT), and the source-table UNIQUE index. `IRepository<TEntity>.GetByIdAsync` takes `object[] keyValues` — one value per key property in the same order.
- **Outbox rotation is part of the publisher loop, not a separate service**: `OutboxPublisherService.MaybeRunCleanupAsync` runs inline after each batch in the same polling goroutine. It fires eagerly on the first tick (cleans up stale data from before startup), then gates subsequent runs on `CleanupInterval`. This keeps rotation within the tracker's lifecycle — no extra hosted service, no external scheduler. Cleanup errors are isolated with their own try/catch so a transient DB failure does not abort the publish loop. Rotation runs sequentially (not in parallel with publishing) because the cleanup DELETE and the unpublished SELECT target disjoint row sets (`published = TRUE` vs `published = FALSE`) — no concurrency is gained, but isolation makes error handling straightforward. `OutboxCleanupService` remains available for ad-hoc manual rotation outside the normal cycle.
- **Kafka thread safety**: `KafkaConsumer` keeps a single background `Task.Run` thread that owns all `IConsumer<K,V>` operations. `Dispose()` cancels via `_disposeCts`, waits up to `2×PollTimeoutMs + 200 ms` for the poll task to exit, then frees the native handle.
- **Integration tests use Testcontainers**: PostgreSQL, Kafka, and RabbitMQ tests require Docker. Mark test classes `[NonParallelizable]` when sharing a container. Use unique topic/queue names per test to avoid cross-test contamination.
- **Logging placement rule**: `NullLoggerFactory.Instance` / `NullLogger<T>.Instance` defaults belong **only** in builders and builder-context extension methods (`ChangeTrackingBuilder`, `ChangePublisherBuilder`, `ChangeSubscriberBuilder`, `KafkaSubscriberExtensions.UseKafka`, `RabbitMqSubscriberExtensions.UseRabbitMq`). All runtime service classes (`ChangePublisher`, `OutboxPublisherService`, `ChangeSubscriber`, `ChangeTrackingHostedService`, `KafkaConsumer`, `RabbitMqConsumer`, `NotificationBasedPublisher`, `OutboxCleanupService`) require a non-nullable logger — no internal fallback. This ensures that callers always make a conscious choice about whether to produce log output.

## Code Style

Follow `.editorconfig` at the repo root for all formatting and naming. Key conventions in effect:

- Private/internal fields: `_camelCase`
- Static private/internal fields: `s_PascalCase` prefix
- Constants: `PascalCase`
- Expression-bodied members preferred for single-expression methods, properties, and accessors
- `using` directives outside the namespace
- System `using` directives sorted first
- Braces on a new line (`csharp_new_line_before_open_brace = all`)

Do not override these rules. If a rule from `.editorconfig` conflicts with a general suggestion, `.editorconfig` wins.

## Design Principles

All code in this repository must respect the following principles. When reviewing or modifying code, check for violations before accepting a change.

- **SRP** — every class has one reason to change. Publisher management, subscriber management, and change tracking are separate concerns; do not merge them into one class.
- **OCP** — extend behaviour through new plugin implementations (`IOutbox`, `IQueuePublisher`, `IChangeSerializer`, etc.), not by modifying core classes.
- **LSP** — plugin implementations must be fully substitutable. A custom `IOutbox` must honour the same contract (idempotency, ordering) as `InMemoryOutbox`.
- **ISP** — keep interfaces narrow. `IQueuePublisher` and `IQueueConsumer` are separate even though `InMemoryQueue` implements both.
- **DIP** — core classes depend on abstractions (`IOutbox`, `IQueuePublisher`, `IChangeSerializer`, …), never on concrete plugin types.
- **KISS** — prefer the simplest solution that satisfies the requirement. Avoid speculative abstractions, configuration knobs, or indirection layers that have no current caller.
- **DRY** — shared logic lives in one place. Serialization, compression, and deduplication are plugin responsibilities, not duplicated across publisher and subscriber.
- **YAGNI** — do not add features, overloads, or extension points for hypothetical future requirements. Three similar lines are better than a premature abstraction.
- **Constructor injection** — dependencies are declared in the constructor, never set via properties or internal methods after construction. Optional dependencies use nullable parameters (`ChangeSubscriber? subscriber = null`).
- **Dead code** — unused fields, parameters, methods, and classes are removed immediately. A field that is injected but never read is a bug, not a harmless remnant.

## CI

`.github/workflows/ci.yml` has three job groups: `build` (compile gate, uploads compiled output as an artifact with 1-day retention), `unit-tests` (9-way parallel matrix, no Docker, downloads build artifact — no rebuild), `integration-tests` (3-way matrix: PostgreSQL / RabbitMQ / Kafka, also downloads build artifact). No job rebuilds the solution; all test jobs depend on the shared artifact from `build`.
