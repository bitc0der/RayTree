## Why

Currently, storage schema initialization (creating source tables, outbox tables, triggers) and queue initialization (creating exchanges, topics, queues) requires users to manually execute scripts or use external tools. The framework should handle both automatically when the tracker is built, reducing setup friction and ensuring consistency.

## What Changes

- Automatic storage schema initialization happens when `EntityChangeTracker` is built via `Build()` or `BuildAsync()`
- Automatic queue initialization happens at the same time (create exchanges, topics, queues)
- Framework detects entity configurations and creates necessary infrastructure during initialization
- Support both annotation-based and convention-based schema generation
- Integrate initialization into both DI-based and standalone configurations

## Capabilities

### New Capabilities
- `auto-init`: Automatic storage schema AND queue initialization during tracker build (tables, triggers, exchanges, topics, queues)

### Modified Capabilities
- `storage-initialization`: Extend existing DDL generation capabilities with automatic execution support during `Build()`
- `queue-init`: New capability for automatic queue infrastructure initialization (RabbitMQ exchanges, Kafka topics, etc.)

## Impact

- `EntityChangeTracker`: `Build()` and `BuildAsync()` methods execute storage AND queue initialization automatically
- `ChangeTrackingConfiguration` (standalone): `Build()` and `BuildAsync()` handle auto-init
- `EntityChangeInterceptor` / DI integration: Tracker build triggers initialization
- `PostgreSqlDdlExecutor`: Enhance with idempotent DDL execution (CREATE IF NOT EXISTS patterns)
- New: Queue plugin interfaces need `InitializeAsync()` method
- New dependency: May require additional PostgreSQL privileges and queue broker privileges
