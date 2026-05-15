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
- **`ChangeTrackingBuilder` / `IChangeTrackingBuilder`** — unified fluent builder for both sides. Accepts an optional `ILoggerFactory?` constructor parameter; `null` normalizes to `NullLoggerFactory.Instance`, so existing call-sites that omit it continue to work. Global factories (`UseOutbox<T>`, `UseSerializer<T>`, etc.) apply to all entity types. `UseSerializer`/`UseCompressor` at the global level forward to both the publisher factory and the subscriber's global instance. `UseSubscriberOptions` and `UseDeduplicationStore` configure the subscriber globally. Per-entity overrides live inside `.ForEntity<TEntity>(Action<IEntityBuilder<TEntity>>)` which exposes both publisher methods (`UseOutbox`, `UsePublisher`, `UseSerializer`, `UseCompressor`, `UseRepository`) and subscriber methods (`UseConsumer`, `OnInsert`, `OnUpdate`, `OnDelete`, `OnChange`, `UseSubscriberOptions`). `Build()` / `BuildAsync()` produce a fully initialized `EntityChangeTracker` with the subscriber already attached.
- **`IEntityBuilder<TEntity>`** — generic per-entity configuration interface. Publisher side: `UseOutbox`, `UsePublisher(IQueuePublisher)`, `UseSerializer`, `UseCompressor`, `UseRepository`. Subscriber side: `UseConsumer(IQueueConsumer)`, `UseSubscriberOptions`, `OnInsert`, `OnUpdate`, `OnDelete`, `OnChange`. `where TEntity : class` is required because subscriber handler registration is typed.
- **`ChangePublisher`** — owns all publisher-side plugin registrations (`IOutbox`, `IQueuePublisher`, `IChangeSerializer`, `IChangeCompressor`, `IRepository` per entity type) in `ConcurrentDictionary<Type, …>`, and manages the `OutboxPublisherService` instances. Constructor requires `ILoggerFactory loggerFactory`. `InitializeAsync()` initializes repositories, outboxes, publishers, then starts one `OutboxPublisherService` per registered entity type. Parallel to `ChangeSubscriber` on the subscriber side.
- **`OutboxPublisherService`** — background polling loop per entity type: reads unpublished changes → serialize → compress → wrap in `MessageEnvelope` → publish → mark published → rotate outbox (see below). Constructor signature: `(ChangePublisher, Type entityType, OutboxPublisherOptions, ILoggerFactory)`. When `OutboxPublisherOptions.UseNotificationChannel = true`, uses `FallbackPollingInterval` instead of `PollingInterval` as the inter-batch sleep, demoting the service to a safety-net role (catching anything the NOTIFY fast-path misses) while `NotificationBasedPublisher` handles normal delivery. Each batch is published in parallel via `Parallel.ForEachAsync` bounded by `MaxPublishConcurrency`. When a batch is full (`changes.Count == BatchSize`), the inter-batch sleep is skipped entirely to drain the backlog immediately. Logs start/stop at `Information`; batch errors at `Error`; per-retry warnings at `Warning`; exhausted retries at `Error`. After each batch it calls `MaybeRunCleanupAsync`, which fires once on the first tick and then respects `OutboxPublisherOptions.CleanupInterval` (default 1 h). Rotation logs: start at `Debug`; records deleted at `Information`; nothing to delete at `Debug`; stale unpublished records found at `Warning`; errors at `Error` (isolated — a cleanup failure does not abort the publish loop).
- **`OutboxPublisherOptions`** — `PollingInterval` (default 5 s), `BatchSize` (default 100), `MaxPublishConcurrency` (default 1 — sequential; increase for throughput when message ordering within a partition is not required), `MaxRetryCount` (default 3), `RetryDelay` (default 1 s), `UseNotificationChannel` / `NotificationChannel` / `FallbackPollingInterval` for PostgreSQL NOTIFY/LISTEN. Rotation options: `CleanupRetentionPeriod` (default 7 days — how old a published record must be before deletion), `CleanupInterval` (default 1 h — how often rotation runs), `StaleUnpublishedThreshold` (default `null` — opt-in; when set, unpublished records older than this value are also removed with a `Warning` log so operators notice stuck queues).
- **`MessageEnvelope`** — the only thing that crosses the queue boundary. Contains metadata fields plus `byte[] Payload` (already serialized+compressed entity state). Serialization happens on the publisher side; deserialization on the subscriber side.
- **`IChangeSerializer`** / **`IChangeCompressor`** — stream-based interfaces. Serializers handle `EntityChange<TEntity>` with typed `State`. `NoOpCompressorPlugin` (built-in) is a passthrough for testing.
- **`IOutbox`** — nine methods: `InitializeAsync`, `WriteAsync<TEntity>`, two `GetUnpublishedAsync<TEntity>` overloads (by batch size; or by `ChangeType?`, `DateTime? since`, batch size), `MarkPublishedAsync`, `TryClaimForPublishingAsync(id)` (atomically sets `published = TRUE WHERE published = FALSE`, returns `true` if this caller claimed it), `RevertClaimAsync(id)` (sets `published = FALSE` — used to undo a claim when publish fails so the fallback loop can retry), `GetByIdAsync<TEntity>`, `CleanupPublishedAsync(retentionPeriod)`, `CleanupStaleUnpublishedAsync(staleThreshold)`. Both cleanup methods return the count of rows deleted.
- **`OutboxCleanupService`** — standalone manual trigger for outbox rotation. Takes `IEnumerable<IOutbox>`, `ILogger<OutboxCleanupService>`, and optional `retentionPeriod` (default 7 days). `RunCleanupAsync()` calls `CleanupPublishedAsync` on every registered outbox and sums the results. Registered as a singleton by `AddChangeTracking` using `OutboxPublisherOptions.CleanupRetentionPeriod`. Useful for one-off or scheduled cleanup outside the normal poll loop.
- **`ChangeSubscriber`** — internal implementation detail owned by `EntityChangeTracker`. Receives `MessageEnvelope` from an `IQueueConsumer`, deduplicates on `CorrelationId` via `IDeduplicationStore`, decompresses and deserializes the payload using reflection (`DeserializeCoreAsync<TEntity>` via `MethodInfo.MakeGenericMethod`), then dispatches to registered `ChangeHandlerAsync<TEntity>` handlers with retry. `ConsumeFromConsumerAsync` and `ConsumeFromQueueAsync` use `Parallel.ForEachAsync` bounded by `SubscriberOptions.MaxDegreeOfParallelism` (default 1 — sequential, preserves per-partition ordering). When all handlers succeed, calls `MaybeDedupCleanupAsync` to periodically evict old dedup entries (gated by `SubscriberOptions.DeduplicationCleanupInterval`); a `SemaphoreSlim` gate ensures only one concurrent invocation runs cleanup at a time. When a handler exhausts retries and throws (`SkipOnFailure = false`), reverts the dedup mark via `RevertProcessedAsync` before rethrowing — this ensures the redelivered message can be retried rather than silently dropped by a persistent dedup store. Still public for direct low-level usage (e.g., standalone subscriber tests), but not the primary API. Constructor signature: `(ILogger<ChangeSubscriber> logger, IDeduplicationStore? dedupStore = null, SubscriberOptions? options = null)` — logger is the required first parameter. Logs unknown entity types at `Warning`; dedup hits at `Debug`; no-handler-match at `Debug`; successful message dispatch at `Debug`; dedup revert on handler failure at `Warning`; retry attempts at `Warning`; SkipOnFailure drops at `Error`; dedup cleanup errors at `Error`.
- **`ChangeSubscriberBuilder` / `IChangeSubscriberBuilder`** — standalone subscriber-only builder. Produces a `ChangeSubscriber` via `Build()`. `ChangeTrackingBuilder` composes one internally; `BuildInternal()` calls `_subscriberBuilder.Build()` and passes the result to the `EntityChangeTracker` constructor.

### Publisher-side plugins

| Package | What it provides |
|---|---|
| `RayTree.Plugins.PostgreSQL` | `PostgreSqlOutbox<TEntity>` — stores changes as flat columns (one column per entity property via `EntityColumnMapper`). Constructor: `PostgreSqlOutbox<TEntity>(PostgreSqlOutboxOptions, ILoggerFactory)` — both params required. `PostgreSqlRepository<TEntity>` constructor: `PostgreSqlRepository<TEntity>(PostgreSqlRepositoryOptions, ILoggerFactory)` — both params required. Builder extension methods accept `ILoggerFactory? loggerFactory = null` and default to `NullLoggerFactory.Instance`. `EntityColumnMapper` honours `System.ComponentModel.DataAnnotations` / `Schema` attributes: `[NotMapped]` excludes a property; `[Column("name")]` overrides the column name suffix (the `state_` prefix is always kept to avoid collisions with outbox metadata columns); `[Column(TypeName = "JSONB")]` sets the PostgreSQL type verbatim; `[Required]` forces `NOT NULL` on reference types; `[MaxLength(n)]`/`[StringLength(n)]` emits `VARCHAR(n)` instead of `TEXT`; `[Table("name")]` on the entity class is used as the base name when deriving default outbox/source table names; `[Key]` (one or more properties) identifies the business primary key — `PostgreSqlRepository` uses these for INSERT/UPDATE/DELETE/SELECT and adds a UNIQUE index on the corresponding `state_*` columns in the source table; for composite keys pair `[Key]` with `[Column(Order = n)]` to control column order. 1D arrays of primitive types are automatically mapped to the corresponding PostgreSQL array column type: `int[]` → `INTEGER[]`, `long[]` → `BIGINT[]`, `bool[]` → `BOOLEAN[]`, `string[]` → `TEXT[]`, `Guid[]` → `UUID[]`, `float[]` → `REAL[]`, `double[]` → `DOUBLE PRECISION[]`, `decimal[]` → `NUMERIC[]`, `DateTime[]`/`DateTimeOffset[]` → `TIMESTAMPTZ[]`, `short[]`/`byte[]`/`sbyte[]` → `SMALLINT[]`; nullable-element arrays (e.g. `int?[]`) strip the nullable wrapper before mapping the element type. Multi-dimensional arrays are not supported — declare the column type explicitly via `[Column(TypeName = "...")]` if needed. When reading values back, `EntityColumnMapper.ConvertFromDb` first attempts a direct CLR assignability check (Npgsql returns the correct array type natively) and falls back to `Convert.ChangeType` for scalar numeric coercions. Both `CleanupPublishedAsync` and `CleanupStaleUnpublishedAsync` delete in batches (`PostgreSqlOutboxOptions.CleanupBatchSize`, default 1000) using a `DELETE … WHERE id IN (SELECT id … LIMIT @BatchSize)` loop to avoid large single-statement locks and WAL spikes. **`InitializeAsync` manages schema automatically** — no flag required, always active. Fresh table path: single `CREATE TABLE IF NOT EXISTS` (columns + indexes). Existing table path: column diff via `SchemaMigrator` (adds missing columns with `ALTER TABLE … ADD COLUMN IF NOT EXISTS`; guards NOT NULL without default on non-empty tables by throwing `InvalidOperationException`; logs `Warning` for orphan columns and type mismatches) + index diff via `IndexMigrator` (creates missing indexes; drops and recreates indexes whose definition changed — uniqueness, column order, or WHERE clause; logs `Warning` for orphan indexes). Internal infrastructure: `SchemaInspector` (static — `TableExistsAsync`, `GetColumnsAsync` via `information_schema.columns`, `GetIndexesAsync` via `pg_index` catalog using `unnest(indkey::smallint[]) WITH ORDINALITY` for ordered columns and `pg_get_expr` for WHERE, `ExecuteDdlAsync`, `TableHasRowsAsync`); `SchemaMigrator` (column diff, parameterised delegate for DDL generation and orphan filter); `IndexMigrator` (index diff with schema-qualified `DROP INDEX IF EXISTS public.{name}`; WHERE clause comparison is case-insensitive and trimmed); `PostgreSqlTypeNormalizer` (maps `information_schema` type fields to canonical DDL strings). `NotificationBasedPublisher` — NOTIFY/LISTEN fast-path with polling fallback; bounded by `NotificationBasedPublisherOptions.MaxConcurrentNotifications` (default 16) via a `SemaphoreSlim` in `OnNotification`; fallback polling uses `Parallel.ForEachAsync` with `MaxPublishConcurrency` (default 1 — sequential). Logs LISTEN connection loss at `Warning` (once, on the first unhealthy tick), recovery at `Information`, and claim contention (record already taken by another publisher) at `Debug`. |
| `RayTree.Plugins.InMemory` | `InMemoryQueue` implements both `IQueuePublisher` and `IQueueConsumer` via `Channel<MessageEnvelope>`. Use for tests and local dev. |
| `RayTree.Plugins.Kafka` | `KafkaPublisher` + `KafkaConsumer`. Consumer uses a dedicated background thread (channel-based) because Confluent.Kafka requires all `Consume`/`Commit` calls on one thread. `KafkaConsumer(KafkaConsumerOptions, ILoggerFactory)` — both params required. |
| `RayTree.Plugins.RabbitMQ` | `RabbitMqPublisher` + `RabbitMqConsumer`. Consumer uses `AsyncEventingBasicConsumer` buffered via `Channel<MessageEnvelope>`. `RabbitMqConsumer(RabbitMqConsumerOptions, ILoggerFactory)` — both params required. |
| `RayTree.Plugins.Serializers.*` | JSON, MessagePack, Protobuf — each in its own package. |
| `RayTree.Plugins.Compressors.*` | Gzip, Brotli, LZ4 — each in its own package. |

### Subscriber-side (`src/RayTree.Core/Handling`)

- **`ChangeSubscriber`** — see Core section above.
- Handler signature: `ChangeHandlerAsync<TEntity>(EntityChange<TEntity> change, CancellationToken ct)`. `change.State` is the fully-typed entity; it is `null` when no serializer is registered for that entity type.
- **`IDeduplicationStore`** — three methods: `TryMarkProcessedAsync(correlationId)` (atomic add, returns `false` if already present — the primary dedup gate), `RevertProcessedAsync(correlationId)` (removes the entry — called on handler failure so re-delivered messages can be retried), `CleanupAsync(retentionPeriod)` (evicts entries older than the retention window). `InMemoryDeduplicationStore` is the default (process-local, cleared on restart). Use `RedisDeduplicationStore` for distributed deployments where dedup state must survive restarts.
- **`SubscriberOptions`** — `MaxDegreeOfParallelism` (default 1 — sequential, preserves per-partition ordering; increase for throughput when handlers are order-independent), `MaxRetries` (retry *attempts* after the first call), `RetryDelay`, `SkipOnFailure`, `DeduplicationRetention` (default 24 h — how old a processed `CorrelationId` must be before cleanup eligibility), `DeduplicationCleanupInterval` (default 1 h — how often `ChangeSubscriber` calls `IDeduplicationStore.CleanupAsync` after a successful message).

### .NET Generic Host integration (`src/RayTree.Hosting`)

- `AddChangeTracking(services, configuration, configure)` — the primary registration path. Registers `EntityChangeTracker` as a singleton (publisher + subscriber configured together) and `ChangeTrackingHostedService` as a hosted service. Resolves `ILoggerFactory` from the DI container and passes it to `new ChangeTrackingBuilder(loggerFactory)` — no explicit logging setup is required from the caller. Publisher loops are started during `EntityChangeTracker.InitializeAsync()` (called inside `Build()`). The hosted service starts consumer loops from `tracker.Subscriber?.Queues` on application startup. Publisher options are bound from `ChangeTracking:Publisher`; subscriber options from `ChangeTracking:Subscriber`.
- **`ChangeTrackingHostedService`** — unified hosted service that starts consumer loops from `tracker.Subscriber?.Queues`. Publisher loops are not started here (they are already running from `InitializeAsync`). If the tracker has no subscriber (publisher-only deployment), `StartAsync` is a no-op. Constructor requires `(EntityChangeTracker, ILogger<ChangeTrackingHostedService>)` — both are auto-wired by the DI container. Logs consumer loop start per entity type name at `Information`; graceful shutdown at `Information`.

### EF Core integration (`src/RayTree.EntityFrameworkCore`)

`EntityChangeInterceptor` hooks into `SaveChangesAsync` to automatically call `TrackInsertAsync`/`TrackUpdateAsync`/`TrackDeleteAsync` based on EF change tracker state.

## Key Design Decisions

- **Unified builder**: `IChangeTrackingBuilder.ForEntity<TEntity>()` takes `Action<IEntityBuilder<TEntity>>` where `IEntityBuilder<TEntity>` covers both publisher and subscriber configuration. The `where TEntity : class` constraint is required by the subscriber handler registration. Value types cannot be entity types. `UseSerializer`/`UseCompressor` at the global level forward the factory's output to the subscriber's global instance by calling `factory(typeof(object))` — this works correctly when the factory ignores the type parameter (the common case).
- **Tracker as thin coordinator**: `EntityChangeTracker` composes a `ChangePublisher` (required) and a `ChangeSubscriber?` (optional) via constructor injection. `ChangeTrackingBuilder.BuildInternal()` creates the `ChangePublisher`, registers all plugins on it, then builds the subscriber via `_subscriberBuilder.Build()` and passes both to the `EntityChangeTracker` constructor. Neither class exposes delegation wrappers on the tracker — callers use `tracker.Publisher` and `tracker.Subscriber` directly. Both remain public for low-level testing but are not part of the primary API.
- **Publisher loop lifetime**: `OutboxPublisherService` instances are created and started inside `EntityChangeTracker.InitializeAsync()`, which is called synchronously by `Build()`. `ChangeTrackingHostedService` does **not** create additional publisher services — doing so would cause duplication. The hosted service only manages consumer loops.
- **Reflection for generic dispatch**: `OutboxPublisherService`, `NotificationBasedPublisher`, and `ChangeSubscriber` all use `MethodInfo.MakeGenericMethod` to invoke serializer/deserializer methods with the runtime entity type. Return types are declared as the non-generic base (`Task<EntityChange>`) so the async upcast works cleanly.
- **PostgreSQL outbox schema**: Each entity type gets its own outbox table. Entity properties are stored as flat columns (not JSON), derived via `EntityColumnMapper.GetColumns(typeof(TEntity))`. By default column names are `state_<snake_case>` and the table name is `<snake_case>_outbox`. Both are customisable via `System.ComponentModel.DataAnnotations` / `Schema` attributes on the entity class — see the `RayTree.Plugins.PostgreSQL` plugin row above for the full attribute reference. `EntityColumnMapper.GetTableName(Type)` encapsulates the `[Table]`-aware table-name logic and is the single place both `PostgreSqlOutbox` and `PostgreSqlRepository` use for their defaults. `EntityColumnMapper.ToPostgresType(Type)` maps CLR types to PostgreSQL column types; 1D array properties (e.g. `int[]`) are emitted as the corresponding PostgreSQL array type (e.g. `INTEGER[]`) — see the plugin row above for the full mapping table. `EntityColumnMapper.ConvertFromDb(object value, Type targetType)` is the shared read-path helper used by both `PostgreSqlOutbox.ReadEntityChange` and `PostgreSqlRepository.MapEntity`; it short-circuits via `IsAssignableFrom` (covering arrays and exact-type matches) before falling back to `Convert.ChangeType` for scalar numeric coercions. `InitializeAsync` branches on table existence: if the table does not exist, one `CREATE TABLE IF NOT EXISTS` creates all columns and indexes; if it exists, column diff + index diff are applied (see `SchemaMigrator` and `IndexMigrator` in the plugin row above). Three outbox indexes are declared in the schema: `idx_*_outbox_unpublished` — partial on `(published, timestamp) WHERE published = FALSE`, used by `GetUnpublishedAsync`; `idx_*_outbox_cleanup` — partial on `(timestamp) WHERE published = TRUE`, used by `CleanupPublishedAsync`; `idx_*_outbox_entity` — on `(entity_type, published, timestamp)`, used by the filtered `GetUnpublishedAsync` overload. `IndexMigrator.ApplyDiffAsync` keeps these in sync with the live database on every startup by comparing against `pg_index` catalog data and applying DROP + CREATE for changed definitions.
- **PostgreSQL primary key resolution**: `EntityColumnMapper.GetKeyProperties(Type)` returns the ordered list of key properties for an entity. It first looks for properties annotated with `[Key]`; multiple `[Key]` properties form a composite key ordered by `[Column(Order)]` then by declaration order. If no `[Key]` is found it falls back to the `Id` convention property. If neither exists it throws `InvalidOperationException` at construction time (fail-fast). `PostgreSqlRepository` uses key properties to build INSERT, WHERE (UPDATE/DELETE/SELECT), and the source-table UNIQUE index. `IRepository<TEntity>.GetByIdAsync` takes `object[] keyValues` — one value per key property in the same order.
- **Outbox rotation is part of the publisher loop, not a separate service**: `OutboxPublisherService.MaybeRunCleanupAsync` runs inline after each batch in the same polling goroutine. It fires eagerly on the first tick (cleans up stale data from before startup), then gates subsequent runs on `CleanupInterval`. `_lastCleanup` is only advanced when both cleanup operations succeed; a failure leaves `_lastCleanup` unchanged so the next tick retries immediately, giving operators fast feedback via repeated error logs rather than silently waiting a full interval. This keeps rotation within the tracker's lifecycle — no extra hosted service, no external scheduler. Cleanup errors are isolated with their own try/catch so a transient DB failure does not abort the publish loop. Rotation runs sequentially (not in parallel with publishing) because the cleanup DELETE and the unpublished SELECT target disjoint row sets (`published = TRUE` vs `published = FALSE`) — no concurrency is gained, but isolation makes error handling straightforward. `OutboxCleanupService` remains available for ad-hoc manual rotation outside the normal cycle.
- **NotificationBasedPublisher as a fast-path overlay**: when `UseNotificationChannel = true` in `PostgreSqlOutboxOptions`, the DB trigger fires a `pg_notify` on every `INSERT` into the outbox table. `NotificationBasedPublisher` receives that notification, atomically claims the record via `IOutbox.TryClaimForPublishingAsync` (prevents races with other publishers), and publishes immediately. The fallback polling loop inside `NotificationBasedPublisher` runs only on the first tick (to drain records written before the listener was established) and when `_listenerHealthy = false` (LISTEN connection broke). `OutboxPublisherService` continues running but at `FallbackPollingInterval` cadence — it acts as a safety net for anything the notification path misses, not an equal peer. Both paths use `TryClaimForPublishingAsync`/`RevertClaimAsync` to prevent duplicate publishing without relying solely on subscriber-side deduplication.
- **Dedup mark-before-process with revert-on-failure**: `ChangeSubscriber` calls `TryMarkProcessedAsync` before invoking handlers (single round-trip, prevents concurrent duplicate processing). If a handler exhausts its retries and throws, `RevertProcessedAsync` removes the entry before the exception propagates, so the redelivered message is accepted and retried. When `SkipOnFailure = true`, no revert is issued — the intentional skip is permanent. This ensures at-least-once semantics are preserved even with persistent dedup stores (e.g., Redis) that survive process restarts.
- **Kafka thread safety**: `KafkaConsumer` keeps a single background `Task.Run` thread that owns all `IConsumer<K,V>` operations. `Dispose()` cancels via `_disposeCts`, waits up to `2×PollTimeoutMs + 200 ms` for the poll task to exit, then frees the native handle.
- **Integration tests use Testcontainers**: PostgreSQL, Kafka, and RabbitMQ tests require Docker. Mark test classes `[NonParallelizable]` when sharing a container. Use unique topic/queue names per test to avoid cross-test contamination.
- **Logging placement rule**: `NullLoggerFactory.Instance` / `NullLogger<T>.Instance` defaults belong **only** in builders and builder-context extension methods (`ChangeTrackingBuilder`, `ChangePublisherBuilder`, `ChangeSubscriberBuilder`, `KafkaSubscriberExtensions.UseKafka`, `RabbitMqSubscriberExtensions.UseRabbitMq`, `BuilderExtensions.UsePostgreSqlOutbox`, `RepositoryExtensions.UsePostgreSqlRepository`). All runtime service classes (`ChangePublisher`, `OutboxPublisherService`, `ChangeSubscriber`, `ChangeTrackingHostedService`, `KafkaConsumer`, `RabbitMqConsumer`, `NotificationBasedPublisher`, `OutboxCleanupService`, `PostgreSqlOutbox<TEntity>`, `PostgreSqlRepository<TEntity>`) require a non-nullable logger — no internal fallback. This ensures that callers always make a conscious choice about whether to produce log output.

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

## .NET Conventions

### async / await

- Every method that does I/O must be `async` and accept a `CancellationToken` as its last parameter. Never swallow or ignore the token.
- Never use `async void` — use `async Task` instead. The sole exception is event handlers where the signature is imposed by the framework.
- Do not use `.Result` or `.Wait()` on a `Task`. Always `await`. Blocking on async code deadlocks under `SynchronizationContext`.
- Do not add `ConfigureAwait(false)` — this is a library, not an ASP.NET app, but the codebase does not apply it consistently so omit it everywhere for uniformity.
- Name async methods with the `Async` suffix. Overloads that differ only by cancellation token still carry the suffix.

### Exception handling

- Catch the most specific exception type that is meaningful. Never `catch (Exception)` unless you are at a top-level loop boundary and log the error before continuing or rethrowing.
- Do not swallow exceptions silently. If you catch and do not rethrow, log at `Error` with the original exception attached.
- Use `OperationCanceledException` (not `TaskCanceledException`) as the canonical cancellation signal; let it propagate rather than catching it in inner loops.
- Throw `InvalidOperationException` for programmer errors (wrong call order, missing required configuration). Throw `ArgumentException` / `ArgumentNullException` for bad caller input.
- Avoid `try/catch` purely for control flow. Use `bool`-returning methods (e.g., `TryClaimForPublishingAsync`) instead.

### Disposable

- Implement `IAsyncDisposable` (not `IDisposable`) for types that own async resources (channels, connections, background tasks). Implement both only when a synchronous release path is genuinely needed.
- Always `await using` or `using` in the consuming code; never call `Dispose()` / `DisposeAsync()` manually unless you own the lifetime explicitly.
- Cancel the `CancellationTokenSource` before calling `Dispose()` on background-loop owners so the loop exits cleanly before the handle is released.

### LINQ and collections

- Do not enumerate an `IEnumerable<T>` more than once — call `.ToList()` or `.ToArray()` at the point of materialisation and reuse the result.
- Return `IReadOnlyList<T>` or `IReadOnlyCollection<T>` from public APIs when the caller must not mutate the result. Return `IEnumerable<T>` only when lazy streaming is intentional.
- Prefer `List<T>.ForEach` / `foreach` over LINQ `Select` + side-effects. LINQ is for projections, not mutations.
- Avoid chaining more than three LINQ operators without assigning an intermediate result to a named variable — readability over one-liners.

### Test conventions

- Test method names follow the pattern `MethodUnderTest_Scenario_ExpectedBehaviour` (e.g., `WriteAsync_WhenEntityIsNull_ThrowsArgumentNullException`).
- Each test follows Arrange / Act / Assert with a blank line between sections.
- Assert only one logical outcome per test. Multiple `Assert` calls are fine when they all verify the same logical fact.
- Do not share mutable state between tests in the same class. Each test arranges its own dependencies.
- Unit tests must not touch the file system, network, or real time (`DateTime.UtcNow`). Inject `TimeProvider` or a clock abstraction if the production code reads the clock.

### Span&lt;T&gt; and Memory&lt;T&gt;

- Prefer `Span<T>` / `ReadOnlySpan<T>` over `byte[]` for synchronous, stack-local slicing (serialization scratch buffers, parsing). Do not store a `Span<T>` in a field or closure — it is stack-only.
- Use `Memory<T>` / `ReadOnlyMemory<T>` when the slice must cross an `await` boundary or be stored on the heap (e.g., passed to an async I/O method).
- Do not allocate a new `byte[]` just to pass a sub-range — use `.Slice(offset, length)` or `AsSpan()`/`AsMemory()` on the existing array.
- When writing to a fixed-size destination prefer `Span<T>` overloads of `BinaryPrimitives`, `MemoryMarshal`, or `Encoding` over the array-allocating variants.
- Avoid mixing `Span<T>` and `Memory<T>` in the same call chain without a deliberate reason; pick one ownership model per logical operation and stay consistent.

### Strings and primitives

- Use `string.Empty` instead of `""` for empty-string literals assigned to variables. Inline literals in interpolations are fine.
- Prefer string interpolation (`$"..."`) over `string.Concat` or `+` for readability. Use `string.Format` only when formatting must be passed around as a delegate.
- Avoid `ToString()` on nullable types — null-check or use `?.ToString() ?? string.Empty` explicitly.

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
