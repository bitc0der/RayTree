# Entity Content Tracking Specification

### Requirement: EntityChange SHALL include typed entity state
The EntityChange model SHALL be generic (`EntityChange<TEntity>`) with a `State` property of type `TEntity` to store the full typed entity state.

#### Scenario: EntityChange<T> contains typed State property
- **WHEN** a new `EntityChange<TEntity>` is created
- **THEN** it SHALL have a `State` property of type `TEntity`

#### Scenario: Generic State defaults to default(TEntity)
- **WHEN** an `EntityChange<TEntity>` is instantiated without entity state
- **THEN** `State` SHALL be `default(TEntity)`

### Requirement: Insert changes SHALL capture entity state
When an entity is inserted, the system SHALL capture the entity state after insertion.

#### Scenario: Insert captures entity state as typed State
- **WHEN** an entity is inserted and `EntityChange<TEntity>` is used
- **THEN** `State` SHALL contain the entity state after insertion as type `TEntity`

### Requirement: Update changes SHALL capture entity state
When an entity is updated, the system SHALL capture the entity state after update.

#### Scenario: Update captures entity state as typed State
- **WHEN** an entity is updated and `EntityChange<TEntity>` is used
- **THEN** `State` SHALL contain the entity state after update as type `TEntity`

### Requirement: Delete changes SHALL capture entity state
When an entity is deleted, the system SHALL capture the entity state before deletion.

#### Scenario: Delete captures entity state as typed State
- **WHEN** an entity is deleted and `EntityChange<TEntity>` is used
- **THEN** `State` SHALL contain the entity state before deletion as type `TEntity`

### Requirement: Outbox SHALL persist typed entity state
The outbox storage SHALL persist the typed `State` alongside change metadata.

#### Scenario: Write change with typed State to outbox
- **WHEN** an `EntityChange<TEntity>` with `State` is written to the outbox
- **THEN** the state SHALL be stored and retrievable with the change

#### Scenario: Read change with typed State from outbox
- **WHEN** an `EntityChange<TEntity>` is retrieved from the outbox
- **THEN** `State` SHALL contain the typed entity state that was stored

### Requirement: Serialization pipeline SHALL handle typed entity state
The serialization and compression pipeline SHALL include the typed `State` in the serialized output.

#### Scenario: Serialize change with typed State
- **WHEN** an `EntityChange<TEntity>` with `State` is serialized
- **THEN** the serialized output SHALL include the typed `State` value

#### Scenario: Deserialize change with typed State
- **WHEN** a serialized `EntityChange<TEntity>` with `State` is deserialized
- **THEN** the resulting `EntityChange<TEntity>` SHALL have the typed `State` restored
