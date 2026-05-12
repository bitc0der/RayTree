# Changelog

All notable changes to this project will be documented in this file.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

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
