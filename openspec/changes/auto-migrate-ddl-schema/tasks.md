## 1. Options and Constructor Changes

- [ ] 1.1 Add `bool AutoMigrate { get; set; } = false` to `PostgreSqlOutboxOptions`
- [ ] 1.2 Add `bool AutoMigrate { get; set; } = false` to `PostgreSqlRepositoryOptions`
- [ ] 1.3 Add `ILoggerFactory loggerFactory` as second required parameter to `PostgreSqlOutbox<TEntity>` constructor; store as `_logger = loggerFactory.CreateLogger<PostgreSqlOutbox<TEntity>>()`
- [ ] 1.4 Add `ILoggerFactory loggerFactory` as second required parameter to `PostgreSqlRepository<TEntity>` constructor; store as `_logger = loggerFactory.CreateLogger<PostgreSqlRepository<TEntity>>()`
- [ ] 1.5 Update `BuilderExtensions.UsePostgreSqlOutbox` (both overloads) to accept `ILoggerFactory? loggerFactory = null`, normalise null to `NullLoggerFactory.Instance`, and pass it to `Activator.CreateInstance` / constructor

## 2. Schema Introspection

- [ ] 2.1 Create `Outbox/Schema/SchemaInspector.cs` — static class with `GetColumnsAsync(string connectionString, string tableName, CancellationToken ct)` returning `IReadOnlyDictionary<string, ExistingColumn>` (keyed by column name); query `information_schema.columns WHERE table_schema='public' AND table_name=@TableName`; record `ExistingColumn(string Name, string NormalizedType, bool IsNullable)`
- [ ] 2.2 Create `Outbox/Schema/PostgreSqlTypeNormalizer.cs` — static class with `Normalize(string dataType, string? udtName, int? charMaxLength) → string`; map `information_schema` type strings to canonical uppercase forms (INTEGER, BIGINT, SMALLINT, TEXT, BOOLEAN, UUID, TIMESTAMPTZ, REAL, DOUBLE PRECISION, NUMERIC, VARCHAR(n), and arrays via `_`-prefixed `udtName`)

## 3. DDL Generation

- [ ] 3.1 Add `static string GenerateAddColumn(string tableName, OutboxColumn col)` to `OutboxSchemaGenerator` — emits `ALTER TABLE {table} ADD COLUMN {name} {type} [NOT NULL] [DEFAULT {default}]`
- [ ] 3.2 Add `static string GenerateAddColumn(string tableName, SourceTableColumn col)` to `SourceTableDdlGenerator` — same pattern

## 4. Migration Logic in PostgreSqlOutbox

- [ ] 4.1 In `PostgreSqlOutbox.InitializeAsync`, after the existing CREATE TABLE block, add early-return when `AutoMigrate = false`
- [ ] 4.2 Call `SchemaInspector.GetColumnsAsync` to get actual columns for the outbox table
- [ ] 4.3 Compute desired columns as the `state_*` property columns from `_propertyColumns`
- [ ] 4.4 For each desired column not in actual: if `!IsNullable && Default == null`, execute `SELECT EXISTS(SELECT 1 FROM {table} LIMIT 1)` and throw `InvalidOperationException` if rows exist (message: "Cannot auto-migrate: column '{col}' is NOT NULL with no default and table '{table}' already has rows. Add a DEFAULT or migrate manually.")
- [ ] 4.5 For each desired column not in actual (passing the NOT NULL check): execute `GenerateAddColumn` DDL; catch `PostgresException` with `SqlState == "42701"` (duplicate column) and continue; log `Information` for successful adds
- [ ] 4.6 For each actual `state_*` column not in desired columns: log `Warning` naming the column and table
- [ ] 4.7 For each column present in both where `NormalizedType` differs from desired type (after `PostgreSqlTypeNormalizer.Normalize`): log `Warning` with expected and actual types

## 5. Migration Logic in PostgreSqlRepository

- [ ] 5.1 In `PostgreSqlRepository.InitializeAsync`, after the existing CREATE TABLE block, add early-return when `AutoMigrate = false`
- [ ] 5.2 Call `SchemaInspector.GetColumnsAsync` to get actual columns for the source table
- [ ] 5.3 Compute desired columns as the key columns from `_keyColumns`; infra columns (`id`, `created_at`, `updated_at`, `version`) are excluded from the diff
- [ ] 5.4 Apply the same diff logic as tasks 4.4–4.7 using `SourceTableDdlGenerator.GenerateAddColumn`

## 6. Fix Broken Call-Sites

- [ ] 6.1 Update all `new PostgreSqlOutbox<T>(options)` call-sites in `tests/RayTree.Plugins.PostgreSQL.Tests` to pass `NullLoggerFactory.Instance` as second argument
- [ ] 6.2 Update all `new PostgreSqlRepository<T>(options)` call-sites in `tests/RayTree.Plugins.PostgreSQL.Tests` to pass `NullLoggerFactory.Instance` as second argument

## 7. Integration Tests

- [ ] 7.1 Add `AutoMigrateOutboxTests` integration test class: create outbox, insert a row, then re-initialise with a new entity definition that has an extra nullable property and `AutoMigrate = true` — assert the new column exists and `WriteAsync` populates it
- [ ] 7.2 Add test: `AutoMigrate = true`, entity loses a property — assert the orphan column remains and a `Warning` is logged
- [ ] 7.3 Add test: `AutoMigrate = true`, entity property type changes — assert no DDL applied and a `Warning` is logged
- [ ] 7.4 Add test: `AutoMigrate = true`, new `[Required]` (NOT NULL, no default) property, table non-empty — assert `InvalidOperationException` thrown before any `ALTER TABLE`
- [ ] 7.5 Add test: `AutoMigrate = true`, new `[Required]` (NOT NULL, no default) property, table empty — assert column added successfully
- [ ] 7.6 Add test: `AutoMigrate = false` (default) with new property — assert schema unchanged
- [ ] 7.7 Add `AutoMigrateRepositoryTests` integration test class mirroring 7.1 and 7.4–7.6 for the source table
