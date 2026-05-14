## Why

When an entity definition changes (properties added, removed, or renamed), the PostgreSQL outbox and source tables that store its state become out of sync — new columns are never added and the application silently misreads or fails to write entity state. Today, operators must write and apply `ALTER TABLE` statements manually with no guidance from the library.

## What Changes

- **BREAKING** `PostgreSqlOutbox<TEntity>` constructor requires a second `ILoggerFactory` parameter (follows `KafkaConsumer`/`RabbitMqConsumer` convention)
- **BREAKING** `PostgreSqlRepository<TEntity>` constructor requires a second `ILoggerFactory` parameter
- **BREAKING** `PostgreSqlBuilderExtensions.UsePostgreSqlOutbox` gains an `ILoggerFactory? loggerFactory = null` parameter; callers using the extension are unaffected (defaults to `NullLoggerFactory.Instance`)
- New `AutoMigrate` flag on `PostgreSqlOutboxOptions` (default `false`) — opt-in schema diffing for the outbox table
- New `AutoMigrate` flag on `PostgreSqlRepositoryOptions` (default `false`) — opt-in schema diffing for the source table
- When `AutoMigrate = true`, `InitializeAsync` queries `information_schema.columns`, diffs against the entity definition, and applies `ALTER TABLE ADD COLUMN` for new columns
- Removed columns (present in DB, absent from entity) produce a `Warning` log — no auto-drop
- Type mismatches produce a `Warning` log — no auto-cast
- Adding a `NOT NULL` column without a default to a non-empty table throws `InvalidOperationException` immediately (fail-fast before the DB would reject it)

## Capabilities

### New Capabilities
- `auto-migrate-ddl`: Schema diffing and auto-migration for PostgreSQL outbox and source tables when entity definitions change

### Modified Capabilities

## Impact

- `src/RayTree.Plugins.PostgreSQL` — `PostgreSqlOutbox`, `PostgreSqlRepository`, both options classes, `BuilderExtensions`, new `SchemaInspector` and `PostgreSqlTypeNormalizer` classes, `OutboxSchemaGenerator`, `SourceTableDdlGenerator`
- `tests/RayTree.Plugins.PostgreSQL.Tests` — all direct constructor call-sites need `NullLoggerFactory.Instance` added; new integration tests for migration scenarios
- No new NuGet dependencies — uses `Npgsql` (already present) and `information_schema` (standard PostgreSQL)
