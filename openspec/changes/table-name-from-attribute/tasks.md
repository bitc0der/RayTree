## 1. Remove options surface

- [ ] 1.1 Delete `TableName` property from `src/RayTree.Plugins.PostgreSQL/Repository/PostgreSqlRepositoryOptions.cs`
- [ ] 1.2 Delete `OutboxTableName` property from `src/RayTree.Plugins.PostgreSQL/Outbox/PostgreSqlOutboxOptions.cs`

## 2. Update runtime to read from entity type only

- [ ] 2.1 In `PostgreSqlRepository<TEntity>` constructor, remove the `if (string.IsNullOrWhiteSpace(options.TableName)) options.TableName = ...` block; resolve the table name into a private `_tableName` field via `EntityColumnMapper.GetTableName(typeof(TEntity))` and replace all `_options.TableName` usages with `_tableName`
- [ ] 2.2 In `PostgreSqlOutbox<TEntity>` constructor, remove the `if (string.IsNullOrWhiteSpace(options.OutboxTableName)) options.OutboxTableName = ...` block; resolve the outbox name into a private `_outboxTableName` field as `EntityColumnMapper.GetTableName(typeof(TEntity)) + "_outbox"` and replace all `_options.OutboxTableName` usages with `_outboxTableName`
- [ ] 2.3 In `PostgreSqlRepositoryExtensions.UsePostgreSqlRepository`, drop the `OutboxTableName = options.TableName + "_outbox"` initializer — pass only `ConnectionString` to `PostgreSqlOutboxOptions`

## 3. Update tests and examples

- [ ] 3.1 Remove `TableName =` / `OutboxTableName =` initializers from tests and examples. Known hits: `examples/Kafka.Microservices/OrderService/Program.cs`, `examples/RabbitMQ.Microservices/OrderService/Program.cs`, and the matching `README.md` snippets in each example. Also sweep `tests/` for the same patterns. Where a non-conventional name is required, annotate the entity DTO with `[Table("...")]` instead.
- [ ] 3.2 Add a unit test in `tests/RayTree.Plugins.PostgreSQL.Tests` (or extend an existing one) that constructs `PostgreSqlRepository<T>` and `PostgreSqlOutbox<T>` for an entity annotated with `[Table("orders")]` and asserts the generated SQL targets `orders` / `orders_outbox`
- [ ] 3.3 Add a unit test that does the same for an entity *without* `[Table]` and asserts the snake_case + `_outbox` convention

## 4. Documentation

- [ ] 4.1 Update CLAUDE.md `RayTree.Plugins.PostgreSQL` plugin row to state that table names come from `[Table]` / snake_case convention only, and that `PostgreSqlRepositoryOptions` / `PostgreSqlOutboxOptions` no longer expose a table-name override
- [ ] 4.2 Update CLAUDE.md "Key Design Decisions" PostgreSQL section to drop any reference to `TableName` / `OutboxTableName` overrides

## 5. Verify

- [ ] 5.1 `dotnet build RayTree.slnx -c Release` passes with `TreatWarningsAsErrors=true`
- [ ] 5.2 `dotnet test tests/RayTree.Plugins.PostgreSQL.Tests` passes (requires Docker)
- [ ] 5.3 `openspec validate table-name-from-attribute --strict` passes
