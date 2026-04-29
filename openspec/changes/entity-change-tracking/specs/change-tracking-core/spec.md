## ADDED Requirements

### Requirement: Entity change detection lifecycle
The system SHALL detect, capture, and store entity changes through a defined lifecycle: detection → capture → store → distribute → confirm.

#### Scenario: New entity is detected
- **WHEN** a new entity instance is added to a tracked repository
- **THEN** the system SHALL capture the entity state as an Insert change type

#### Scenario: Existing entity is modified
- **WHEN** an existing entity's tracked properties are modified
- **THEN** the system SHALL capture the before and after state as an Update change type

#### Scenario: Entity is deleted
- **WHEN** a tracked entity is marked for deletion
- **THEN** the system SHALL capture the entity state as a Delete change type

### Requirement: Entity configuration
The system SHALL allow configuration of entity tracking including: repository type, outbox type, and queue pub/sub type per entity.

#### Scenario: Configure entity with specific repository
- **WHEN** a user configures an entity with a specific repository implementation
- **THEN** the system SHALL use that repository for all change detection operations on that entity

#### Scenario: Configure entity with specific outbox
- **WHEN** a user configures an entity with a specific outbox implementation
- **THEN** the system SHALL store changes for that entity using the configured outbox

#### Scenario: Configure entity with specific queue
- **WHEN** a user configures an entity with a specific queue publisher
- **THEN** the system SHALL publish changes for that entity to the configured queue

### Requirement: Change metadata
Each captured entity change SHALL include metadata: change type (Insert/Update/Delete), timestamp, version, correlation ID, and entity type name.

#### Scenario: Change record includes metadata
- **WHEN** an entity change is captured
- **THEN** the change record SHALL include change_type, timestamp, version, correlation_id, and entity_type fields

#### Scenario: Correlation ID propagation
- **WHEN** multiple changes occur within a single SaveChanges call
- **THEN** all changes SHALL share the same correlation ID

### Requirement: Thread safety
The change tracking system SHALL be thread-safe and support concurrent change detection from multiple threads.

#### Scenario: Concurrent entity changes
- **WHEN** multiple threads modify tracked entities simultaneously
- **THEN** the system SHALL capture all changes without data loss or corruption

### Requirement: Transaction context awareness
The change tracking system SHALL detect active transactions and coordinate outbox writes accordingly.

#### Scenario: Detect ambient transaction
- **WHEN** an `IEntityChangeTracker` detects an active `Transaction.Current`
- **THEN** it SHALL register the outbox write to participate in that transaction

#### Scenario: Register outbox write for pending transaction
- **WHEN** a change is tracked within an ambient transaction
- **THEN** the outbox write SHALL be queued until the transaction commits

#### Scenario: Transaction committed
- **WHEN** the ambient transaction completes successfully
- **THEN** all queued outbox writes SHALL be committed

#### Scenario: Transaction aborted
- **WHEN** the ambient transaction is rolled back
- **THEN** all queued outbox writes SHALL be discarded
