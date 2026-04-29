## ADDED Requirements

### Requirement: Per-entity source and outbox tables
For each tracked entity type, the system SHALL maintain a source table and an outbox table in the configured database.

#### Scenario: Source table creation
- **WHEN** an entity type is registered for tracking
- **THEN** a source table named `{entity}_source` SHALL be created with the entity's columns

#### Scenario: Outbox table creation
- **WHEN** an entity type is registered for tracking
- **THEN** an outbox table named `{entity}_outbox` SHALL be created with the entity's columns plus metadata columns (change_type, timestamp, published, version, correlation_id)

#### Scenario: Outbox contains source columns
- **WHEN** an entity change is written to the outbox
- **THEN** the outbox row SHALL contain all source table column values plus the metadata columns

### Requirement: Atomic outbox write
Writing to the outbox table SHALL occur in the same database transaction as the entity change.

#### Scenario: Transactional outbox write
- **WHEN** an entity is saved via EF Core
- **THEN** the outbox entry SHALL be written in the same transaction and rolled back if the save fails

#### Scenario: Outbox write failure
- **WHEN** the outbox write fails within a transaction
- **THEN** the entire entity save SHALL be rolled back

### Requirement: Outbox query interface
The system SHALL provide an interface to query outbox entries by: published status, entity type, change type, and date range.

#### Scenario: Query unpublished changes
- **WHEN** the publisher requests unpublished changes for an entity type
- **THEN** the system SHALL return all outbox entries where published = false, ordered by timestamp

#### Scenario: Query by date range
- **WHEN** the system queries outbox entries with a date range filter
- **THEN** only entries within the specified date range SHALL be returned

### Requirement: Transaction scope support
When an ambient `TransactionScope` or explicit database transaction is active, the outbox write SHALL participate in that transaction.

#### Scenario: TransactionScope participation
- **WHEN** entity changes are made within an active `TransactionScope`
- **THEN** the outbox writes SHALL be deferred until `TransactionScope.Complete()` is called

#### Scenario: TransactionScope rollback
- **WHEN** a `TransactionScope` is disposed without calling `Complete()`
- **THEN** all outbox writes within that scope SHALL be rolled back

#### Scenario: Explicit database transaction
- **WHEN** an `IDbTransaction` is passed to the repository during entity operations
- **THEN** the outbox write SHALL use the same transaction

#### Scenario: No active transaction
- **WHEN** entity changes are made outside any transaction scope
- **THEN** the outbox write SHALL be committed immediately in its own implicit transaction

### Requirement: Outbox cleanup
The system SHALL support configurable cleanup of published outbox entries older than a specified retention period.

#### Scenario: Cleanup published entries
- **WHEN** the cleanup job runs with a 7-day retention period
- **THEN** all published entries older than 7 days SHALL be deleted

#### Scenario: Cleanup preserves unpublished entries
- **WHEN** the cleanup job runs
- **THEN** unpublished entries SHALL NOT be deleted regardless of age

### Requirement: PostgreSQL notification trigger
When NOTIFY mode is enabled for a PostgreSQL outbox, the system SHALL generate a trigger that sends a `pg_notify` message after each outbox insert.

#### Scenario: Trigger generation
- **WHEN** `.UseNotificationChannel("entity_changes")` is configured on a PostgreSQL outbox
- **THEN** a trigger function and trigger SHALL be generated that calls `pg_notify` after each outbox row insert

#### Scenario: Notification payload
- **WHEN** an outbox row is inserted and NOTIFY mode is enabled
- **THEN** the notification payload SHALL contain entity type, outbox row ID, and change type as JSON

#### Scenario: Multiple outbox tables, single channel
- **WHEN** multiple entity outbox tables share the same notification channel
- **THEN** the notification payload SHALL include the entity type so listeners can distinguish sources

### Requirement: Notification DDL generation
The system SHALL provide SQL scripts to create, update, and drop notification triggers for PostgreSQL outbox tables.

#### Scenario: Generate notification DDL
- **WHEN** the migration generator is called with NOTIFY enabled
- **THEN** it SHALL output `CREATE TRIGGER` and `CREATE FUNCTION` statements for notification

#### Scenario: Drop notification DDL
- **WHEN** NOTIFY mode is disabled
- **THEN** the migration generator SHALL output `DROP TRIGGER` and `DROP FUNCTION` statements

### Requirement: Connection resilience for notifications
The system SHALL detect PostgreSQL connection drops and recover the LISTEN subscription automatically.

#### Scenario: Connection drop detection
- **WHEN** the PostgreSQL connection used for LISTEN is lost
- **THEN** the system SHALL detect the drop and log a warning

#### Scenario: Automatic reconnection
- **WHEN** the connection is lost during LISTEN
- **THEN** the system SHALL reconnect and re-issue the LISTEN command

#### Scenario: Backlog scan on reconnect
- **WHEN** the notification listener reconnects after a disconnection
- **THEN** the system SHALL scan for unpublished entries created during the outage and process them before resuming LISTEN
