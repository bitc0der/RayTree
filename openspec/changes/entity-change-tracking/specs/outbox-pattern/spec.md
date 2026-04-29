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

### Requirement: Outbox cleanup
The system SHALL support configurable cleanup of published outbox entries older than a specified retention period.

#### Scenario: Cleanup published entries
- **WHEN** the cleanup job runs with a 7-day retention period
- **THEN** all published entries older than 7 days SHALL be deleted

#### Scenario: Cleanup preserves unpublished entries
- **WHEN** the cleanup job runs
- **THEN** unpublished entries SHALL NOT be deleted regardless of age
