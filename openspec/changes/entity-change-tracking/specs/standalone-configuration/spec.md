## ADDED Requirements

### Requirement: Fluent configuration builder
The system SHALL provide a fluent configuration builder that allows setting up change tracking without dependency injection.

#### Scenario: Build configuration
- **WHEN** a user creates a `ChangeTrackingConfiguration` via the builder
- **THEN** the configuration SHALL contain all registered plugins and entity settings

#### Scenario: Configure repository
- **WHEN** `.UseRepository<T>()` is called on the builder
- **THEN** the specified repository implementation SHALL be used for entity operations

#### Scenario: Configure outbox
- **WHEN** `.UseOutbox<T>()` is called on the builder
- **THEN** the specified outbox implementation SHALL be used for change storage

#### Scenario: Configure queue
- **WHEN** `.UseQueue<T>()` is called on the builder
- **THEN** the specified queue publisher SHALL be used for message distribution

#### Scenario: Configure serializer
- **WHEN** `.UseSerializer<T>()` is called on the builder
- **THEN** the specified serializer SHALL be used for message serialization

#### Scenario: Configure compressor
- **WHEN** `.UseCompressor<T>()` is called on the builder
- **THEN** the specified compressor SHALL be used for message compression

### Requirement: Entity-specific configuration
The system SHALL allow configuring specific plugins per entity type.

#### Scenario: Entity-specific queue
- **WHEN** an entity is configured with a specific queue publisher
- **THEN** changes for that entity SHALL be published to the configured queue

#### Scenario: Entity-specific outbox
- **WHEN** an entity is configured with a specific outbox
- **THEN** changes for that entity SHALL be stored in the configured outbox

### Requirement: Manual tracker instantiation
The system SHALL allow creating an `IEntityChangeTracker` instance directly from a configuration object.

#### Scenario: Create tracker from config
- **WHEN** a configuration is built and `.Build()` is called
- **THEN** an `IEntityChangeTracker` instance SHALL be returned ready for use

#### Scenario: Tracker lifecycle
- **WHEN** the tracker is no longer needed
- **THEN** calling `Dispose()` SHALL release all resources and stop background operations

### Requirement: Standalone outbox publisher
The system SHALL allow running the outbox publisher as a standalone component without a hosted service.

#### Scenario: Start standalone publisher
- **WHEN** `.StartPublisher()` is called on the configuration
- **THEN** the publisher SHALL begin polling for unpublished changes in the background

#### Scenario: Stop standalone publisher
- **WHEN** `.StopPublisher()` is called
- **THEN** the publisher SHALL stop polling and complete in-flight operations
