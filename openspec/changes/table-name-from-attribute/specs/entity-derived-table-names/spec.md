## ADDED Requirements

### Requirement: Repository table name derived from entity type
`PostgreSqlRepository<TEntity>` SHALL derive its source-table name exclusively from `typeof(TEntity)` via `EntityColumnMapper.GetTableName`. `PostgreSqlRepositoryOptions` SHALL NOT expose any property that lets callers override the table name.

#### Scenario: Entity with [Table] attribute uses the attribute value
- **WHEN** `TEntity` is annotated with `[Table("orders")]` and a `PostgreSqlRepository<TEntity>` is constructed
- **THEN** the repository targets the `orders` table for INSERT, UPDATE, DELETE, and SELECT statements

#### Scenario: Entity without [Table] attribute uses snake_case convention
- **WHEN** `TEntity` is class `CustomerProfile` with no `[Table]` attribute and a `PostgreSqlRepository<TEntity>` is constructed
- **THEN** the repository targets the `customer_profile` table

#### Scenario: PostgreSqlRepositoryOptions exposes no table-name knob
- **WHEN** a developer inspects the public surface of `PostgreSqlRepositoryOptions`
- **THEN** no property exists that accepts a table name (no `TableName`, no equivalent override)

### Requirement: Outbox table name derived from entity type
`PostgreSqlOutbox<TEntity>` SHALL derive its outbox-table name exclusively as `"{EntityColumnMapper.GetTableName(typeof(TEntity))}_outbox"`. `PostgreSqlOutboxOptions` SHALL NOT expose any property that lets callers override the outbox table name.

#### Scenario: Entity with [Table] attribute drives outbox name
- **WHEN** `TEntity` is annotated with `[Table("orders")]` and a `PostgreSqlOutbox<TEntity>` is constructed
- **THEN** the outbox reads from and writes to the `orders_outbox` table

#### Scenario: Entity without [Table] attribute uses snake_case + _outbox suffix
- **WHEN** `TEntity` is class `CustomerProfile` with no `[Table]` attribute and a `PostgreSqlOutbox<TEntity>` is constructed
- **THEN** the outbox targets the `customer_profile_outbox` table

#### Scenario: PostgreSqlOutboxOptions exposes no outbox-table-name knob
- **WHEN** a developer inspects the public surface of `PostgreSqlOutboxOptions`
- **THEN** no property exists that accepts an outbox table name (no `OutboxTableName`, no equivalent override)

### Requirement: Repository extension forwards no table name into outbox options
`PostgreSqlRepositoryExtensions.UsePostgreSqlRepository<TEntity>` SHALL construct `PostgreSqlOutboxOptions` without setting any table-name field on it. The outbox SHALL resolve its own name from `typeof(TEntity)`.

#### Scenario: Extension constructs outbox options without table name
- **WHEN** a caller invokes `builder.UsePostgreSqlRepository<TEntity>(o => o.ConnectionString = "...")`
- **THEN** the `PostgreSqlOutboxOptions` instance passed to `PostgreSqlOutbox<TEntity>` carries only the connection string (and any unrelated options), and the outbox-table name is resolved from `typeof(TEntity)` inside the outbox constructor
