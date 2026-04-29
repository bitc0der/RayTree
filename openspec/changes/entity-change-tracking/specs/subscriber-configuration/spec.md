## ADDED Requirements

### Requirement: Subscriber configuration builder
The system SHALL provide a subscriber configuration builder that mirrors the publisher's per-entity configuration model.

#### Scenario: Build subscriber configuration
- **WHEN** a user creates a `ChangeSubscriberConfiguration` via the builder
- **THEN** the configuration SHALL contain all registered consumers, serializers, compressors, and entity handlers

#### Scenario: Register entity consumer
- **WHEN** `.ConsumeEntity<Order>()` is called on the subscriber builder
- **THEN** the system SHALL configure a consumer pipeline for `Order` change messages

### Requirement: Per-entity consume source
Each tracked entity SHALL be configurable with its own queue/topic/exchange to consume from.

#### Scenario: Consume from Kafka topic
- **WHEN** `.ConsumeEntity<Order>(entity => entity.FromKafka("orders-topic"))` is called
- **THEN** the subscriber SHALL consume `Order` messages from the specified Kafka topic

#### Scenario: Consume from RabbitMQ queue
- **WHEN** `.ConsumeEntity<Customer>(entity => entity.FromRabbitMq("customer-changes"))` is called
- **THEN** the subscriber SHALL consume `Customer` messages from the specified RabbitMQ queue

#### Scenario: Mixed queue sources
- **WHEN** entities are configured with different queue sources
- **THEN** each entity SHALL be consumed from its configured source independently

### Requirement: Per-entity serializer/compressor resolution
Each entity SHALL resolve its serializer and compressor from configuration, matching what the publisher used.

#### Scenario: Resolve serializer from entity config
- **WHEN** an `Order` message is received and the entity is configured with `ProtobufSerializer`
- **THEN** the message SHALL be deserialized using the Protobuf serializer

#### Scenario: Resolve compressor from entity config
- **WHEN** an `InventoryItem` message is received and the entity is configured with `Lz4Compressor`
- **THEN** the message SHALL be decompressed using the LZ4 compressor

#### Scenario: Fallback to default serializer
- **WHEN** an entity has no serializer override configured
- **THEN** the global default serializer SHALL be used

### Requirement: Per-entity change handlers
Each entity SHALL support registering handlers for specific change types (Insert, Update, Delete).

#### Scenario: Register handler for specific change type
- **WHEN** `.OnChange<Order>(ChangeType.Update, handler)` is called
- **THEN** the handler SHALL be invoked only for `Order` update messages

#### Scenario: Register handler for all change types
- **WHEN** `.OnChange<Order>(handler)` is called without a change type filter
- **THEN** the handler SHALL be invoked for all `Order` change messages

#### Scenario: Multiple handlers per entity
- **WHEN** multiple handlers are registered for the same entity and change type
- **THEN** all handlers SHALL be invoked in registration order

### Requirement: Deduplication per entity
The system SHALL support configurable deduplication per entity using `correlation_id`.

#### Scenario: Enable deduplication
- **WHEN** `.WithDeduplication<Order>(TimeSpan.FromHours(1))` is called
- **THEN** duplicate `Order` messages with the same correlation_id within 1 hour SHALL be skipped

#### Scenario: Deduplication storage
- **WHEN** deduplication is enabled
- **THEN** processed correlation IDs SHALL be stored in the configured deduplication store (in-memory, Redis, or database)

### Requirement: Error handling per entity
Each entity SHALL support configurable error handling policies.

#### Scenario: Retry policy
- **WHEN** `.OnError<Order>(policy => policy.Retry(3))` is called
- **THEN** failed `Order` message handlers SHALL be retried up to 3 times

#### Scenario: Dead-letter queue
- **WHEN** `.OnError<Order>(policy => policy.DeadLetter("failed-orders"))` is called
- **THEN** messages that exhaust retries SHALL be moved to the dead-letter destination

#### Scenario: Skip on error
- **WHEN** `.OnError<Order>(policy => policy.Skip())` is called
- **THEN** failed messages SHALL be logged and skipped without retry

### Requirement: DI integration for subscribers
The system SHALL provide extension methods on `IServiceCollection` for subscriber registration.

#### Scenario: Register subscriber with DI
- **WHEN** `AddChangeSubscriber()` is called on IServiceCollection
- **THEN** all subscriber components SHALL be registered in the DI container

#### Scenario: Subscriber hosted service
- **WHEN** `AddChangeSubscriber()` is called
- **THEN** a `ChangeSubscriberHostedService` SHALL be registered to manage the consume loop

### Requirement: Standalone subscriber
The system SHALL allow creating a subscriber instance directly from configuration without DI.

#### Scenario: Create standalone subscriber
- **WHEN** a subscriber configuration is built via `.Build()`
- **THEN** an `IChangeSubscriber` instance SHALL be returned ready for use

#### Scenario: Start standalone subscriber
- **WHEN** `.StartAsync()` is called on the subscriber
- **THEN** the subscriber SHALL begin consuming messages from all configured sources

#### Scenario: Stop standalone subscriber
- **WHEN** `.StopAsync()` is called
- **THEN** the subscriber SHALL gracefully stop consuming and complete in-flight handlers
