## ADDED Requirements

### Requirement: Queue publisher abstraction
The system SHALL define an `IQueuePublisher` interface that accepts serialized and optionally compressed change messages and publishes them to a configured queue.

#### Scenario: Publish change message
- **WHEN** a serialized change message is sent to the queue publisher
- **THEN** the message SHALL be published to the configured queue destination

#### Scenario: Publisher connection failure
- **WHEN** the queue publisher cannot connect to the queue broker
- **THEN** the publish operation SHALL fail and the outbox entry SHALL remain unpublished

### Requirement: Polling-based outbox distribution
The system SHALL poll outbox tables at a configurable interval and publish unpublished changes to the configured queue.

#### Scenario: Poll and publish
- **WHEN** the polling interval elapses and unpublished changes exist
- **THEN** the system SHALL fetch unpublished changes and publish them to the queue

#### Scenario: Configurable polling interval
- **WHEN** the polling interval is set to 5 seconds
- **THEN** the system SHALL check for unpublished changes every 5 seconds

#### Scenario: Batch publishing
- **WHEN** multiple unpublished changes exist and batch size is 100
- **THEN** the system SHALL publish up to 100 changes per poll cycle

### Requirement: Post-publish confirmation
After a message is successfully published to the queue, the system SHALL mark the corresponding outbox entry as published.

#### Scenario: Mark as published
- **WHEN** a change message is successfully published to the queue
- **THEN** the outbox entry's published flag SHALL be set to true

#### Scenario: Failed publish does not mark
- **WHEN** publishing a change message to the queue fails
- **THEN** the outbox entry SHALL remain unpublished and available for retry

### Requirement: Serialization pipeline
Before publishing, each change SHALL pass through a serialization pipeline: entity change → serialize → compress → publish.

#### Scenario: JSON serialization
- **WHEN** the configured serializer is JSON
- **THEN** the change SHALL be serialized to a JSON string

#### Scenario: Gzip compression
- **WHEN** the configured compressor is Gzip
- **THEN** the serialized message SHALL be compressed using gzip before publishing

#### Scenario: No compression
- **WHEN** the configured compressor is None
- **THEN** the serialized message SHALL be published without compression
