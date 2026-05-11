# Changelog

All notable changes to this project will be documented in this file.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

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
