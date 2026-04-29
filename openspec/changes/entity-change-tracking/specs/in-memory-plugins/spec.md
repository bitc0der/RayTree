## ADDED Requirements

### Requirement: In-memory plugin assembly
The system SHALL provide in-memory implementations as a separate assembly (`RayTree.Plugins.InMemory`) with zero external dependencies beyond the core library.

#### Scenario: Reference in-memory assembly
- **WHEN** a project references `RayTree.Plugins.InMemory`
- **THEN** in-memory repository, outbox, and queue implementations SHALL be available without pulling in database or queue broker dependencies

#### Scenario: Zero external dependencies
- **WHEN** `RayTree.Plugins.InMemory` is referenced
- **THEN** no additional NuGet packages beyond `RayTree.Core` SHALL be required

### Requirement: In-memory repository
The system SHALL provide an in-memory repository implementation that stores entities in concurrent collections.

#### Scenario: Store entity
- **WHEN** an entity is saved via the in-memory repository
- **THEN** the entity SHALL be stored in a thread-safe in-memory collection

#### Scenario: Retrieve entity
- **WHEN** an entity is retrieved by ID from the in-memory repository
- **THEN** the stored entity SHALL be returned

#### Scenario: Update entity
- **WHEN** an existing entity is updated via the in-memory repository
- **THEN** the stored entity SHALL be replaced with the updated version

#### Scenario: Delete entity
- **WHEN** an entity is deleted via the in-memory repository
- **THEN** the entity SHALL be removed from the in-memory collection

#### Scenario: Thread safety
- **WHEN** multiple threads access the in-memory repository concurrently
- **THEN** all operations SHALL complete without data corruption or exceptions

### Requirement: In-memory outbox
The system SHALL provide an in-memory outbox implementation that stores change entries in concurrent collections.

#### Scenario: Write change to outbox
- **WHEN** a change is written to the in-memory outbox
- **THEN** the change entry SHALL be stored in a thread-safe in-memory collection

#### Scenario: Query unpublished changes
- **WHEN** unpublished changes are requested from the in-memory outbox
- **THEN** all entries where `published = false` SHALL be returned ordered by timestamp

#### Scenario: Mark as published
- **WHEN** a change is marked as published in the in-memory outbox
- **THEN** the entry's `published` flag SHALL be set to true

#### Scenario: Outbox cleanup
- **WHEN** cleanup is run on the in-memory outbox with a retention period
- **THEN** published entries older than the retention period SHALL be removed from memory

#### Scenario: Transaction simulation
- **WHEN** an in-memory outbox write is requested within a simulated transaction
- **THEN** the write SHALL succeed, and rollback SHALL remove the entry if the transaction fails

### Requirement: In-memory queue publisher
The system SHALL provide an in-memory queue implementation that publishes messages to in-memory subscribers within the same process.

#### Scenario: Publish message
- **WHEN** a message is published to the in-memory queue
- **THEN** the message SHALL be delivered to all registered subscribers for that entity type

#### Scenario: Subscribe to entity changes
- **WHEN** a subscriber registers for `Order` entity changes via `.Subscribe<Order>()`
- **THEN** the subscriber SHALL receive all published `Order` change messages

#### Scenario: Subscribe with change type filter
- **WHEN** a subscriber registers with a filter for `ChangeType.Update`
- **THEN** the subscriber SHALL only receive update messages, not inserts or deletes

#### Scenario: Multiple subscribers
- **WHEN** multiple subscribers register for the same entity type
- **THEN** each subscriber SHALL receive a copy of every published message

#### Scenario: Subscriber receives deserialized change
- **WHEN** a subscriber receives a message from the in-memory queue
- **THEN** the message SHALL be the deserialized `EntityChange` object (no serialization/compression overhead)

#### Scenario: Subscriber deregistration
- **WHEN** a subscriber calls `.Unsubscribe()` or disposes its subscription handle
- **THEN** the subscriber SHALL no longer receive messages

### Requirement: In-memory queue as publisher destination
The in-memory queue SHALL be usable as a publisher target in the change tracking configuration.

#### Scenario: Configure in-memory queue as publisher
- **WHEN** `.UseInMemoryQueue()` is called on the configuration builder
- **THEN** the outbox publisher SHALL publish changes to the in-memory queue instead of an external broker

#### Scenario: Outbox publisher with in-memory queue
- **WHEN** the outbox publisher is configured with an in-memory queue
- **THEN** changes SHALL be marked as published immediately after delivery to in-memory subscribers

### Requirement: In-memory subscriber integration
The subscriber configuration SHALL support consuming from the in-memory queue.

#### Scenario: Configure in-memory subscriber
- **WHEN** `.ConsumeFromInMemory()` is called on the subscriber builder
- **THEN** the subscriber SHALL receive messages from the in-memory queue

#### Scenario: Entity handler with in-memory queue
- **WHEN** `.ConsumeEntity<Order>()` is configured with in-memory queue
- **THEN** registered `Order` handlers SHALL be invoked when changes are published to the in-memory queue

### Requirement: Use cases: testing and development
The in-memory plugins SHALL be suitable for unit testing, integration testing, and local development.

#### Scenario: Unit test with in-memory storage
- **WHEN** a test uses in-memory repository and outbox
- **THEN** the test SHALL run without a database connection and produce deterministic results

#### Scenario: Integration test with in-memory queue
- **WHEN** an integration test uses in-memory queue publisher and subscriber
- **THEN** the test SHALL verify end-to-end message flow without RabbitMQ or Kafka

#### Scenario: Development mode
- **WHEN** an application is started with in-memory plugins in development mode
- **THEN** the application SHALL function fully without external infrastructure

### Requirement: In-memory configuration API
The system SHALL provide fluent API methods for configuring in-memory plugins.

#### Scenario: Configure in-memory repository
- **WHEN** `.UseInMemoryRepository()` is called
- **THEN** the in-memory repository SHALL be used for entity storage

#### Scenario: Configure in-memory outbox
- **WHEN** `.UseInMemoryOutbox()` is called
- **THEN** the in-memory outbox SHALL be used for change storage

#### Scenario: Configure in-memory queue
- **WHEN** `.UseInMemoryQueue()` is called
- **THEN** the in-memory queue SHALL be used for message distribution

#### Scenario: Mixed configuration
- **WHEN** in-memory repository is combined with RabbitMQ queue
- **THEN** entities SHALL be stored in memory but changes SHALL be published to RabbitMQ
