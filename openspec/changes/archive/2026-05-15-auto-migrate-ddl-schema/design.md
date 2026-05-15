## Context

`PostgreSqlOutbox<TEntity>` and `PostgreSqlRepository<TEntity>` create their tables at startup using `CREATE TABLE IF NOT EXISTS`. Once a table exists, the schema is frozen — adding or removing a property on the entity has no effect on the live table, causing silent write failures or missing state on reads.

The library already has all information needed to drive a diff at startup: `EntityColumnMapper.GetColumns(Type)` produces the desired column set at compile time, and `information_schema.columns` exposes the actual column set at runtime. The gap is the diff and the `ALTER TABLE` application.

Both classes currently have no logger, which prevents emitting migration warnings. The Kafka and RabbitMQ consumers in this codebase already establish the pattern: `(Options, ILoggerFactory)` constructor signature for runtime service classes that need logging.

## Goals / Non-Goals

**Goals:**
- Additive migrations (new columns) applied automatically when `AutoMigrate = true`
- Warnings logged for removed columns and type mismatches — operator visibility without data loss
- Fail-fast with a clear `InvalidOperationException` for NOT NULL columns without defaults on non-empty tables
- Constructor logging parity with `KafkaConsumer` and `RabbitMqConsumer`
- Zero new NuGet dependencies

**Non-Goals:**
- Auto-dropping removed columns (data loss risk)
- Auto-casting type mismatches (correctness risk)
- Cross-schema PostgreSQL support (always queries `public` schema)
- Non-PostgreSQL databases
- Transactional DDL wrapping (PostgreSQL supports it but adds significant complexity for minimal benefit at startup)

## Decisions

### D1: `(Options, ILoggerFactory)` constructor signature — not `ILogger<T>`

`Activator.CreateInstance` is used in `BuilderExtensions.UsePostgreSqlOutbox` to construct `PostgreSqlOutbox<TEntity>` with a runtime generic type. Passing a non-generic `ILoggerFactory` keeps this reflection call straightforward. Passing `ILogger<PostgreSqlOutbox<TEntity>>` would require generic type construction via reflection, adding noise with no benefit. This matches the existing pattern in `KafkaConsumer` and `RabbitMqConsumer`.

### D2: `AutoMigrate` defaults to `false` — opt-in only

Schema changes at startup are a deployment-time event. Operators must be able to audit what runs against their database. Defaulting to `false` ensures existing deployments are unaffected and all migration behavior is intentional.

### D3: New classes `SchemaInspector` and `PostgreSqlTypeNormalizer` — not inline in `PostgreSqlOutbox`

Schema introspection and type normalization are reusable across both `PostgreSqlOutbox` and `PostgreSqlRepository`. Keeping them as separate static classes follows the SRP principle in `CLAUDE.md` and makes each independently testable without a live database (type normalization has no DB dependency).

### D4: Fail-fast before DDL for NOT NULL + non-empty table

PostgreSQL itself would reject `ALTER TABLE ADD COLUMN col TYPE NOT NULL` on a non-empty table without a default. We check proactively (`SELECT EXISTS(SELECT 1 FROM {table} LIMIT 1)`) and throw `InvalidOperationException` with a clear remediation message rather than letting the Npgsql exception surface. This gives operators actionable information without needing to parse a PostgreSQL error string.

### D5: Warn on removed columns, never auto-drop

A column present in the DB but absent from the entity might still contain data referenced by external systems, audit logs, or old consumer code. Auto-dropping violates the LSP (existing outbox consumers that read those columns would break). Operators receive a `Warning` log and can drop manually after confirming safety.

### D6: `GenerateAddColumn` added to existing generator classes

`OutboxSchemaGenerator` and `SourceTableDdlGenerator` already own DDL generation for their respective table types. Adding a `GenerateAddColumn` method keeps DDL generation co-located with table creation DDL rather than introducing a separate migrator class with overlapping concerns.

### D7: Type comparison uses `information_schema` with a normalizer

`information_schema.columns` is the standard, portable introspection surface. `pg_catalog` is more precise for arrays and custom types but is PostgreSQL-internal. We handle arrays via `udt_name` (leading `_` prefix = array element type) which is well-documented PostgreSQL behaviour. Unrecognised types produce a `Debug` log rather than a false-positive warning.

## Risks / Trade-offs

- **Concurrent startup race**: Two instances starting simultaneously both see the table exists, both query the schema, both attempt `ALTER TABLE ADD COLUMN`. PostgreSQL serialises DDL — the second attempt will succeed (column already exists) or fail with a "column already exists" error. Mitigation: catch `PostgresException` with `SqlState = "42701"` (duplicate column) on the `ALTER TABLE` and treat it as success.
- **Table name injection**: `tableName` is constructed from entity type names and options, never from user input. No parameterisation needed for DDL table names, but code review should confirm no user-controlled string flows into the table name.
- **`information_schema` visibility**: Requires the connecting user to have `SELECT` on `information_schema.columns` for the target database. This is granted by default in PostgreSQL. No mitigation needed for standard deployments.
- **Constructor breaking change**: All call-sites that construct `PostgreSqlOutbox` or `PostgreSqlRepository` directly (tests, manual DI wiring) must add `NullLoggerFactory.Instance`. This is mechanical but unavoidable.

## Migration Plan

1. Deploy updated library version — existing tables are unchanged (`AutoMigrate` defaults to `false`)
2. Operator sets `AutoMigrate = true` in options for a given entity
3. On next application startup, `InitializeAsync` diffs the schema and applies `ALTER TABLE ADD COLUMN` for new columns
4. Operator monitors logs for `Warning` entries about removed or mismatched columns and acts manually

**Rollback**: Set `AutoMigrate = false` and restart. Added columns remain in the DB but cause no harm — the library reads them selectively by column name, not by ordinal position for the outbox (and `PostgreSqlRepository.MapEntity` skips unknown columns by design).

## Open Questions

- Should the `information_schema` query be scoped to a configurable schema name rather than hard-coded `'public'`? Deferred — no current caller uses a non-public schema, and adding a config option now would be YAGNI.
