## ADDED Requirements

### Requirement: Constructor requires ILoggerFactory
`PostgreSqlOutbox<TEntity>` and `PostgreSqlRepository<TEntity>` SHALL each require an `ILoggerFactory` as their second constructor parameter. No internal null fallback is permitted. Builder-context extension methods MAY default to `NullLoggerFactory.Instance`.

#### Scenario: Direct construction without logger fails to compile
- **WHEN** a caller constructs `PostgreSqlOutbox<TEntity>` with only an options parameter
- **THEN** the code SHALL NOT compile

#### Scenario: Direct construction with NullLoggerFactory succeeds
- **WHEN** a caller constructs `PostgreSqlOutbox<TEntity>` with options and `NullLoggerFactory.Instance`
- **THEN** the instance is created without error

#### Scenario: Builder extension defaults to NullLoggerFactory
- **WHEN** `UsePostgreSqlOutbox` extension is called without a `loggerFactory` argument
- **THEN** `PostgreSqlOutbox` is constructed successfully using `NullLoggerFactory.Instance`

---

### Requirement: AutoMigrate option controls schema diffing
`PostgreSqlOutboxOptions` and `PostgreSqlRepositoryOptions` SHALL each expose a `bool AutoMigrate` property defaulting to `false`. When `false`, `InitializeAsync` behaviour is identical to the previous release. When `true`, schema diffing and migration run after table creation.

#### Scenario: AutoMigrate defaults to false
- **WHEN** `PostgreSqlOutboxOptions` is constructed with no explicit `AutoMigrate` value
- **THEN** `AutoMigrate` SHALL equal `false`

#### Scenario: Migration skipped when AutoMigrate is false
- **WHEN** `AutoMigrate = false` and the entity has a new property that is absent from the table
- **THEN** `InitializeAsync` completes without altering the table schema

---

### Requirement: New columns are added automatically
When `AutoMigrate = true` and a property exists on the entity but has no corresponding column in the table, `InitializeAsync` SHALL execute `ALTER TABLE ADD COLUMN` for that column and log at `Information` level.

#### Scenario: Nullable new column added to outbox table
- **WHEN** `AutoMigrate = true` and the entity has a new nullable property absent from the outbox table
- **THEN** `InitializeAsync` adds the column via `ALTER TABLE ADD COLUMN`
- **THEN** subsequent `WriteAsync` calls populate the new column

#### Scenario: Nullable new column added to source table
- **WHEN** `AutoMigrate = true` and the entity has a new nullable key column absent from the source table
- **THEN** `InitializeAsync` adds the column via `ALTER TABLE ADD COLUMN`

#### Scenario: NOT NULL new column added to empty table
- **WHEN** `AutoMigrate = true`, the entity has a new `[Required]` property with no default, and the table has zero rows
- **THEN** `InitializeAsync` adds the column as `NOT NULL`

#### Scenario: Information log emitted for added column
- **WHEN** `AutoMigrate = true` and a column is added
- **THEN** a message at `Information` level is logged naming the column and table

---

### Requirement: NOT NULL column on non-empty table fails fast
When `AutoMigrate = true`, a desired column is NOT NULL with no default, and the table already contains rows, `InitializeAsync` SHALL throw `InvalidOperationException` before issuing any DDL. The exception message SHALL name the column, the table, and instruct the operator to add a DEFAULT or migrate manually.

#### Scenario: Fail-fast thrown before ALTER TABLE
- **WHEN** `AutoMigrate = true`, entity has a new `[Required]` property with no default, and the table has at least one row
- **THEN** `InitializeAsync` throws `InvalidOperationException`
- **THEN** no `ALTER TABLE` statement is executed

#### Scenario: Exception message is actionable
- **WHEN** the fail-fast exception is thrown
- **THEN** the message SHALL contain the column name, the table name, and guidance to add a DEFAULT or migrate manually

---

### Requirement: Removed columns produce a warning
When `AutoMigrate = true` and a column with the `state_` prefix exists in the table but has no matching entity property, `InitializeAsync` SHALL log a `Warning` naming the column and table. The column SHALL NOT be dropped.

#### Scenario: Orphan column is not dropped
- **WHEN** `AutoMigrate = true` and a `state_*` column exists in the DB with no matching entity property
- **THEN** the column remains in the table after `InitializeAsync`

#### Scenario: Warning logged for orphan column
- **WHEN** `AutoMigrate = true` and an orphan `state_*` column is detected
- **THEN** a message at `Warning` level is logged naming the column and table

---

### Requirement: Type mismatches produce a warning
When `AutoMigrate = true` and a column exists in both the entity definition and the table but their PostgreSQL types differ after normalization, `InitializeAsync` SHALL log a `Warning` naming the column, the expected type, and the actual type. No DDL is applied.

#### Scenario: Type mismatch logged, no DDL applied
- **WHEN** `AutoMigrate = true` and a column's type in the DB does not match the entity-derived type
- **THEN** a message at `Warning` level is logged with expected and actual types
- **THEN** no `ALTER TABLE` statement is executed for that column

---

### Requirement: Concurrent startup duplicate-column race is handled
When two application instances start simultaneously and both attempt `ALTER TABLE ADD COLUMN` for the same new column, the second attempt SHALL NOT throw an unhandled exception. PostgreSQL error code `42701` (duplicate column) SHALL be caught and treated as success.

#### Scenario: Second ADD COLUMN on existing column is silently absorbed
- **WHEN** two instances race and the second `ALTER TABLE ADD COLUMN` receives a `42701` error
- **THEN** `InitializeAsync` completes without throwing
