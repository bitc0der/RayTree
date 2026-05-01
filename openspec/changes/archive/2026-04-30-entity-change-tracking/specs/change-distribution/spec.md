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

### Requirement: PostgreSQL NOTIFY-based distribution
When a PostgreSQL outbox is configured with a notification channel, the system SHALL use `LISTEN`/`NOTIFY` to trigger immediate publishing instead of polling.

#### Scenario: Notification triggers publish
- **WHEN** a `pg_notify` message is received on the configured channel
- **THEN** the system SHALL fetch the referenced outbox entry and publish it immediately

#### Scenario: Notification payload parsing
- **WHEN** a notification payload is received
- **THEN** the system SHALL extract entity type and outbox row ID from the JSON payload

#### Scenario: Immediate publish latency
- **WHEN** NOTIFY mode is active and an outbox entry is inserted
- **THEN** the publish SHALL begin within 100ms of the database commit

### Requirement: Fallback polling with NOTIFY
When NOTIFY mode is enabled, the system SHALL maintain a fallback polling loop that activates on connection loss and runs periodically as a safety net.

#### Scenario: Fallback activation on connection loss
- **WHEN** the PostgreSQL LISTEN connection is lost
- **THEN** the fallback poller SHALL activate and process unpublished entries at the configured fallback interval

#### Scenario: Fallback deactivation on reconnect
- **WHEN** the PostgreSQL LISTEN connection is restored
- **THEN** the fallback poller SHALL deactivate and return to passive monitoring

#### Scenario: Configurable fallback interval
- **WHEN** `.WithFallbackPolling(TimeSpan.FromSeconds(30))` is configured
- **THEN** the fallback poller SHALL check for unpublished entries every 30 seconds when active

### Requirement: Notification listener lifecycle
The system SHALL manage the PostgreSQL connection and LISTEN subscription as a managed lifecycle with graceful startup and shutdown.

#### Scenario: Start notification listener
- **WHEN** the notification-based publisher starts
- **THEN** it SHALL open a dedicated PostgreSQL connection and issue the LISTEN command

#### Scenario: Graceful shutdown
- **WHEN** the application is shutting down
- **THEN** the notification listener SHALL unlisten, close the connection, and complete in-flight publish operations

#### Scenario: Multiple channels
- **WHEN** multiple notification channels are configured
- **THEN** the system SHALL subscribe to all channels and route notifications to the correct entity outbox handlers
