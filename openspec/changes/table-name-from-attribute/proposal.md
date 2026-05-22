## Why

Today `PostgreSqlRepositoryOptions.TableName` and `PostgreSqlOutboxOptions.OutboxTableName` let callers pin a table name at registration time. The same entity type can therefore be registered with two different table names in different services — drift that bypasses the `[Table]` attribute on the entity DTO and silently produces split storage. The entity type is the single source of truth for where its rows live; the options bag should not be able to override it.

## What Changes

- **BREAKING**: Remove `PostgreSqlRepositoryOptions.TableName`. Table name is always derived from the entity type via `EntityColumnMapper.GetTableName(typeof(TEntity))` — `[Table("name")]` if present, snake_case of the type name otherwise.
- **BREAKING**: Remove `PostgreSqlOutboxOptions.OutboxTableName`. Outbox table name is always `${EntityColumnMapper.GetTableName(typeof(TEntity))}_outbox`.
- Update `PostgreSqlRepository<TEntity>` and `PostgreSqlOutbox<TEntity>` constructors to read the table name directly from the entity type — no more fallback assignment back onto the options bag.
- Update `PostgreSqlRepositoryExtensions.UsePostgreSqlRepository` to stop forwarding `options.TableName` into the outbox options.
- Update CLAUDE.md to reflect the new contract (entity DTO + `[Table]` is the only knob).

## Capabilities

### New Capabilities
- `entity-derived-table-names`: Table names for PostgreSQL outbox and repository plugins are derived exclusively from the entity DTO (via `[Table]` attribute or snake_case convention), with no per-registration override.

### Modified Capabilities
<!-- none -->

## Impact

- **Code**: `PostgreSqlRepositoryOptions`, `PostgreSqlOutboxOptions`, `PostgreSqlRepository<TEntity>`, `PostgreSqlOutbox<TEntity>`, `PostgreSqlRepositoryExtensions`, and any tests / examples that set `TableName` or `OutboxTableName`.
- **Public API**: Two breaking property removals. Callers must migrate by adding `[Table("name")]` to the entity class if they were relying on a custom name.
- **Docs**: CLAUDE.md plugin reference for `RayTree.Plugins.PostgreSQL`.
