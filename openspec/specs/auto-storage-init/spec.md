## ADDED Requirements

### Requirement: Automatic storage AND queue initialization
The system SHALL automatically initialize storage schemas (source tables, outbox tables, notification triggers, DDL procedures) AND queue infrastructure (exchanges, topics, queues) for all registered entity types when `EntityChangeTracker` is built via `Build()` or `BuildAsync()`.

#### Scenario: Initialize storage schemas on Build()
- **WHEN** `Build()` is called on `ChangeTrackingConfiguration` or the tracker is built via DI
- **THEN** the system SHALL generate and execute DDL for all registered entity types

#### Scenario: Initialize queue infrastructure on Build()
- **WHEN** `Build()` is called and queue publishers are registered
- **THEN** the system SHALL initialize queue infrastructure (create exchanges/topics/queues) via `IQueuePublisher.InitializeAsync()`

#### Scenario: Idempotent initialization
- **WHEN** `Build()` is called multiple times
- **THEN** the system SHALL use `CREATE IF NOT EXISTS` patterns and not fail on subsequent calls

#### Scenario: Detect registered entities and publishers
- **WHEN** the system performs initialization
- **THEN** it SHALL discover entity types via `EntityChangeTracker.GetOutboxes()` and publishers via `GetPublishers()`

### Requirement: Queue initialization interface
The system SHALL add `InitializeAsync()` method to `IQueuePublisher` interface for queue infrastructure setup.

#### Scenario: RabbitMQ queue initialization
- **WHEN** a RabbitMQ publisher is registered and `Build()` is called
- **THEN** the system SHALL ensure the exchange and queue exist (create if not exists)

#### Scenario: Kafka topic initialization
- **WHEN** a Kafka publisher is registered and `Build()` is called
- **THEN** the system SHALL ensure the topic exists (auto-create or explicit creation)

#### Scenario: InMemory queue (no init needed)
- **WHEN** an InMemory queue publisher is registered
- **THEN** `InitializeAsync()` SHALL complete immediately (no-op)

### Requirement: Connection string and config discovery
The system SHALL discover the appropriate connection string from the outbox configuration and queue config from publisher for each entity type.

#### Scenario: Single outbox connection
- **WHEN** all entities use outboxes with the same connection string
- **THEN** the system SHALL use that connection for DDL execution

#### Scenario: Multiple outbox connections
- **WHEN** entities use outboxes with different connection strings
- **THEN** the system SHALL execute DDL on each respective storage connection

### Requirement: DDL executor enhancement
The `PostgreSqlDdlExecutor` SHALL support idempotent DDL execution using `IF NOT EXISTS` patterns.

#### Scenario: Create table if not exists
- **WHEN** DDL contains `CREATE TABLE` statements
- **THEN** the generated DDL SHALL use `CREATE TABLE IF NOT EXISTS` syntax

#### Scenario: Create function if not exists
- **WHEN** DDL contains `CREATE FUNCTION` statements
- **THEN** the generated DDL SHALL use `CREATE OR REPLACE FUNCTION` syntax

#### Scenario: Create trigger if not exists
- **WHEN** DDL contains `CREATE TRIGGER` statements
- **THEN** the system SHALL check trigger existence before creation or use `CREATE OR REPLACE` equivalent
