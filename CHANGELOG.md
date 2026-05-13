# Changelog

All notable changes to this project will be documented in this file.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

---

## [0.0.6-pre-release]

### Fixed

#### Duplicate publish prevention in `NotificationBasedPublisher`

- `OutboxPublisherService` was ignoring `OutboxPublisherOptions.UseNotificationChannel`
  and always polling at `PollingInterval` (default 5 s), racing with
  `NotificationBasedPublisher` to publish the same record. When
  `UseNotificationChannel = true` the service now uses `FallbackPollingInterval`
  instead, demoting itself to a safety-net role while `NotificationBasedPublisher`
  handles normal delivery.
- `NotificationBasedPublisher.FallbackPollingLoopAsync` was running on every tick
  unconditionally, publishing every change a second time in parallel with the
  `OnNotification` fast-path. The loop now polls only on the first tick at startup
  (to drain records written before the listener was established) and when the LISTEN
  connection is unhealthy (`_listenerHealthy = false`).
- `OnNotification` had a TOCTOU race: it read `change.Published`, then raced to
  publish before calling `MarkPublishedAsync`, allowing two concurrent publishers to
  both publish the same record. `OnNotification` and `ProcessUnpublishedChangesAsync`
  now atomically claim records via `IOutbox.TryClaimForPublishingAsync` before
  publishing. On publish failure the claim is reverted via `IOutbox.RevertClaimAsync`
  so the fallback loop can retry.

#### Deduplication correctness in `ChangeSubscriber`

- `ChangeSubscriber` marked a message's `CorrelationId` as processed **before**
  invoking handlers. When a handler exhausted retries and threw (`SkipOnFailure =
  false`), the correlation ID remained in the store. With a persistent dedup store
  (e.g. Redis), the message broker's redelivered copy would then be silently dropped
  forever. The subscriber now calls `IDeduplicationStore.RevertProcessedAsync` before
  rethrowing, so the redelivered message is accepted and retried.
- `IDeduplicationStore.CleanupAsync` and `SubscriberOptions.DeduplicationRetention`
  existed but were never wired up, causing `InMemoryDeduplicationStore` to grow
  without bound. `ChangeSubscriber` now calls `MaybeDedupCleanupAsync` after each
  successfully processed message, gated by the new
  `SubscriberOptions.DeduplicationCleanupInterval` (default 1 h).
- `IDeduplicationStore.IsProcessedAsync` was defined on the interface and implemented
  but never called anywhere. Removed (breaking change for external implementations —
  delete the method).

### Added

- `IOutbox.TryClaimForPublishingAsync(long id, CancellationToken)` — atomically
  transitions a record from `published = FALSE` to `published = TRUE` and returns
  `true` if this caller made the transition (i.e. the record was unpublished).
  Returns `false` when another publisher already claimed it.
- `IOutbox.RevertClaimAsync(long id, CancellationToken)` — sets `published = FALSE`,
  undoing a claim after a publish failure so the record remains visible to the
  fallback polling loop.
- `IDeduplicationStore.RevertProcessedAsync(string correlationId, CancellationToken)`
  — removes a correlation ID from the store so a redelivered message can be retried
  after a handler failure.
- `SubscriberOptions.DeduplicationCleanupInterval` (default 1 h) — how often
  `ChangeSubscriber` triggers `IDeduplicationStore.CleanupAsync` to evict entries
  older than `DeduplicationRetention`.

### Breaking Changes

- `IOutbox` gains two new methods: `TryClaimForPublishingAsync` and `RevertClaimAsync`.
  External implementations must add both.
- `IDeduplicationStore` loses `IsProcessedAsync` and gains `RevertProcessedAsync`.
  External implementations must remove the former and add the latter.

---

## [0.0.5-pre-release]

### Added

#### Primitive array support for PostgreSQL outbox and repository

- 1D arrays of primitive types are now stored as native PostgreSQL array columns.
  `EntityColumnMapper.ToPostgresType` maps `T[]` to the corresponding `PG_TYPE[]`:

  | C# type | PostgreSQL column |
  |---|---|
  | `int[]` | `INTEGER[]` |
  | `long[]` | `BIGINT[]` |
  | `short[]` / `byte[]` / `sbyte[]` | `SMALLINT[]` |
  | `float[]` | `REAL[]` |
  | `double[]` | `DOUBLE PRECISION[]` |
  | `decimal[]` | `NUMERIC[]` |
  | `bool[]` | `BOOLEAN[]` |
  | `Guid[]` | `UUID[]` |
  | `DateTime[]` / `DateTimeOffset[]` | `TIMESTAMPTZ[]` |
  | `string[]` | `TEXT[]` |

- Nullable-element arrays (e.g. `int?[]`) strip the nullable wrapper before mapping
  the element type — the column type is the same as for a non-nullable element array.
- Multi-dimensional arrays are not supported; use `[Column(TypeName = "...")]` to
  declare the column type explicitly when needed.
- New `EntityColumnMapper.ConvertFromDb(object value, Type targetType)` helper is
  used by both `PostgreSqlOutbox.ReadEntityChange` and `PostgreSqlRepository.MapEntity`
  to read values back. It checks assignability first (Npgsql returns the correct CLR
  array type natively) and falls back to `Convert.ChangeType` for scalar numeric
  coercions.

#### Documentation

- `docs/database-migration.md` C# → PostgreSQL type mapping table extended with
  array types and array-specific rules.

### Fixed

- `OutboxPublisherService.MaybeRunCleanupAsync` previously advanced `_lastCleanup`
  before running the cleanup operations, so a transient DB failure would still delay
  the next retry by a full `CleanupInterval`. The timestamp is now only advanced when
  both operations complete successfully; a failure leaves the timer unchanged so the
  next poll tick retries immediately.
- `PostgreSqlOutbox.BatchDeleteAsync` was allocating a new `NpgsqlCommand` on every
  batch iteration. The command is now created once outside the loop and reused across
  all `ExecuteNonQueryAsync` calls, reducing allocation and repeated query-parse
  overhead on large cleanup runs.

---

## [0.0.4-pre-release]

### Added

#### Outbox rotation integrated into the publisher loop

- `OutboxPublisherService` now runs `MaybeRunCleanupAsync` after every poll batch.
  Rotation fires eagerly on the first tick (so stale rows from before a restart are
  cleaned up immediately), then respects `OutboxPublisherOptions.CleanupInterval`
  for subsequent runs.
- Cleanup errors are isolated in their own `try/catch`; a transient DB failure logs
  an error but does not abort the publish loop.
- No separate hosted service or external scheduler is needed — rotation is tied to
  the existing publisher lifetime.

#### Stale unpublished record cleanup

- New `IOutbox.CleanupStaleUnpublishedAsync(TimeSpan staleThreshold, CancellationToken)`
  method. When records have been in the outbox without being published for longer
  than `staleThreshold`, they are deleted. A `Warning` log is emitted whenever any
  are found — treat this as an operator signal for queue health issues.
- Implemented in `InMemoryOutbox` and `PostgreSqlOutbox<TEntity>`.
- Opt-in via `OutboxPublisherOptions.StaleUnpublishedThreshold` (default `null` —
  disabled).

#### New `OutboxPublisherOptions` rotation properties

| Property | Default | Description |
|---|---|---|
| `CleanupRetentionPeriod` | 7 days | Minimum age of a published row before it is deleted. |
| `CleanupInterval` | 1 hour | How often rotation runs; first tick is always immediate. |
| `StaleUnpublishedThreshold` | `null` | When set, unpublished rows older than this are also removed. |

#### PostgreSQL batched cleanup

- `PostgreSqlOutbox.CleanupPublishedAsync` and `CleanupStaleUnpublishedAsync` now
  delete in batches using `DELETE … WHERE id IN (SELECT id … ORDER BY id LIMIT
  @BatchSize)` loops. This avoids large single-statement locks and WAL spikes on
  busy tables.
- `PostgreSqlOutboxOptions.CleanupBatchSize` (default `1000`) controls the rows
  deleted per statement.

#### New PostgreSQL partial index

- `idx_*_outbox_cleanup` — `(timestamp) WHERE published = TRUE` — added to the
  schema so `CleanupPublishedAsync` uses an index scan instead of a sequential scan.
  Created via `CREATE INDEX IF NOT EXISTS`, so existing tables pick it up on next
  startup.

#### Documentation

- New **Outbox rotation** section in `docs/README.md` covering configuration,
  `appsettings.json` binding, batch size tuning, log levels, and manual rotation
  via `OutboxCleanupService`.

### Breaking Changes

- `IOutbox` gains a new method `CleanupStaleUnpublishedAsync(TimeSpan staleThreshold, CancellationToken)`.
  Any external implementation of `IOutbox` must add this method. The built-in implementations
  (`InMemoryOutbox`, `PostgreSqlOutbox<TEntity>`) are updated automatically.

### Fixed

- `ServiceCollectionExtensions` was passing `options.PollingInterval * 10` (50 s by
  default) as the retention period for `OutboxCleanupService`. It now correctly uses
  `options.CleanupRetentionPeriod` (default 7 days).
- PostgreSQL integration tests no longer race with the background `OutboxPublisherService`
  poll loop. The outbox is created directly in `SetUp` without starting a publisher,
  eliminating a non-deterministic failure where the poller marked records as
  published between two `WriteAsync` calls in the same test.

### Dependencies

| Package | From | To |
|---|---|---|
| `Microsoft.EntityFrameworkCore` | `8.0.26` | `10.0.7` |
| `Microsoft.EntityFrameworkCore.InMemory` | `8.0.26` | `10.0.7` |
| `Microsoft.EntityFrameworkCore.Relational` | `8.0.11` | `10.0.7` |
| `Microsoft.Extensions.DependencyInjection` | `8.0.1` | `10.0.7` |
| `Microsoft.Extensions.Hosting` | `8.0.1` | `10.0.7` |
| `Microsoft.Extensions.Configuration.Binder` | `8.0.2` | `10.0.7` |
| `Npgsql.EntityFrameworkCore.PostgreSQL` | `8.0.11` | `10.0.1` |
| `Microsoft.Extensions.Options` | `8.0.2` | removed (unused) |
| `System.IO.Pipelines` | `8.0.0` | removed (unused) |
| `StackExchange.Redis` | `2.8.16` | removed (no implementation) |

---

## [0.0.3-pre-release]

### Added

#### `[Key]` primary key support (`RayTree.Plugins.PostgreSQL`, `RayTree.Plugins.InMemory`)

- `EntityColumnMapper.GetKeyProperties(Type)` resolves the ordered list of key
  properties for an entity type. Looks for `[Key]`-annotated properties first;
  multiple `[Key]` properties form a composite key ordered by `[Column(Order = n)]`
  then by declaration order. Falls back to the `Id` convention property when no
  `[Key]` is present. Throws `InvalidOperationException` at construction time
  (fail-fast) when neither exists.
- `PostgreSqlRepository<TEntity>` uses key properties to build typed `INSERT`,
  `WHERE` (for `UpdateAsync` / `DeleteAsync` / `GetByIdAsync`), and the source-table
  `UNIQUE` index. SQL is constructed once at construction time and reused for every
  call.
- `SourceTableDdlGenerator.CreateDefault` accepts `IReadOnlyList<SourceTableColumn>
  keyColumns`; key columns are appended after the infrastructure columns
  (`id`, `created_at`, `updated_at`, `version`) and a `UNIQUE` index is added
  covering them.
- `InMemoryRepository<TEntity>` gained full `[Key]` / composite-key / `Id`
  convention resolution, mirroring `EntityColumnMapper.GetKeyProperties`. Composite
  keys are serialised with a `\0` null-character separator to prevent ambiguous
  collisions between values that contain common delimiter characters.

#### DataAnnotations attribute support for PostgreSQL schema generation

- `[NotMapped]` — property excluded from the outbox schema entirely.
- `[Column("name")]` — overrides the column name suffix; the `state_` prefix is
  always kept to prevent collisions with fixed outbox metadata columns.
- `[Column(TypeName = "...")]` — sets the PostgreSQL type verbatim (e.g. `JSONB`,
  `NUMERIC(18,4)`).
- `[Required]` — emits `NOT NULL` on reference-type and nullable value-type
  properties.
- `[MaxLength(n)]` / `[StringLength(n)]` — emits `VARCHAR(n)` instead of `TEXT`
  for string properties (ignored when `TypeName` is already set).
- `[Table("name")]` — used as the base name when deriving default outbox and source
  table names. `EntityColumnMapper.GetTableName(Type)` encapsulates this logic;
  both `PostgreSqlOutbox` and `PostgreSqlRepository` use it for their defaults.

#### `IRepository<TEntity>` breaking change

- `GetByIdAsync(object id, ...)` → `GetByIdAsync(object[] keyValues, ...)`.
  Accepts one value per key property in the same order as declared. Both
  `PostgreSqlRepository` and `InMemoryRepository` validate the count at call time
  and throw `ArgumentException` if it does not match.

### Changed

- `PostgreSqlRepository<TEntity>` constructor now builds a complete
  `columnName → PropertyInfo` cache from `EntityColumnMapper.GetColumns` at startup.
  `MapEntity` is a pure dictionary lookup with no per-row reflection calls.
- `PostgreSqlOutbox<TEntity>` default table name derived via
  `EntityColumnMapper.GetTableName` + `"_outbox"` instead of a hand-rolled
  snake-case helper.
- Target framework upgraded from `net8.0` to `net10.0`.

### Fixed

- `InMemoryRepository` composite key separator changed from `|` to `\0`
  (null character), preventing key collisions when entity property values contain
  the pipe character.

### Tests

- `GetKeyPropertiesTests` — 9 unit tests covering single `[Key]`, `Id` fallback,
  composite key with `[Column(Order)]`, composite key without `[Column(Order)]`
  (declaration-order fallback), no-key throws, constructor fail-fast, and wrong
  key count.
- `InMemoryRepositoryTests` — wrong key count throws `ArgumentException`.
- `InMemoryRepositoryCompositeKeyTests` — null-char separator correctness: keys
  `("a|b", "c")` and `("a", "b|c")` remain distinct.
- `DefaultSourceTableNameIntegrationTests` — integration test verifying that
  omitting `TableName` in `PostgreSqlRepositoryOptions` creates a table named after
  the entity type.
- `PostgresContainerFactory` extracted as a shared helper used by all PostgreSQL
  integration tests.

### Dependencies

| Package | From | To |
|---|---|---|
| NUnit | — | updated |
| MessagePack | — | updated |
| Npgsql | — | updated |
| Kafka client | — | updated |
| Entity Framework Core | — | updated |
| RabbitMQ.Client | — | updated |
| Testcontainers.PostgreSql | — | updated |
| Testcontainers.RabbitMq | — | updated |

---

## [0.0.2] — 2026-05-09

### Added

- Structured logging via `Microsoft.Extensions.Logging` throughout. Pass an
  `ILoggerFactory` to `ChangeTrackingBuilder` or let `AddChangeTracking` wire it
  from DI automatically. Defaults to `NullLoggerFactory` so existing call-sites
  continue to compile without change.
- `ILoggerFactory` parameter on `ChangeTrackingBuilder`, `KafkaConsumer`,
  `RabbitMqConsumer`, and `ChangeTrackingHostedService` constructors.

---

## [0.0.1] — initial release

- Initial implementation of entity change tracking with outbox pattern, in-memory
  and PostgreSQL plugins, Kafka and RabbitMQ queue plugins, JSON / MessagePack /
  Protobuf serialisers, Gzip / Brotli / LZ4 compressors, EF Core interceptor,
  and ASP.NET Core hosting integration.
