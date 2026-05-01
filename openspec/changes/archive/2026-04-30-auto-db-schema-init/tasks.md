## 1. DDL Generator Enhancements#

- [x] 1.1 Update `SourceTableDdlGenerator` to output `CREATE TABLE IF NOT EXISTS` syntax#
- [x] 1.2 Update `OutboxTableDdlGenerator` to output `CREATE TABLE IF NOT EXISTS` syntax#
- [x] 1.3 Update trigger DDL generation to use `CREATE OR REPLACE FUNCTION` and check trigger existence before `CREATE TRIGGER`#
- [x] 1.4 Add helper method to check PostgreSQL trigger existence in DDL generators#

## 2. PostgreSqlDdlExecutor Updates#

- [x] 2.1 Ensure existing `ExecuteAsync()` handles `IF NOT EXISTS` syntax correctly#
- [x] 2.2 Add logging for DDL operations (DEBUG level)#

## 3. IQueuePublisher Interface Update#

- [x] 3.1 Add `InitializeAsync()` method to `IQueuePublisher` interface#
- [x] 3.2 Implement `InitializeAsync()` in `RabbitMqPublisher` (create exchange/queue)#
- [x] 3.3 Implement `InitializeAsync()` in `KafkaPublisher` (create topic if not exists)#
- [x] 3.4 Implement `InitializeAsync()` in `InMemoryQueue` (no-op)#

## 4. EntityChangeTracker Extensions#

- [x] 4.1 Modify `InitializeStorageAsync()` extension to execute DDL against storage (not just generate)#
- [x] 4.2 Create `InitializeQueuesAsync()` extension that calls `InitializeAsync()` on all registered publishers#
- [x] 4.3 Auto-detect registered entities via `GetOutboxes().Keys`#
- [x] 4.4 Auto-detect registered publishers via `GetPublishers()`#

## 5. Builder API - Standalone Configuration#

- [x] 5.1 Modify `ChangeTrackingConfiguration.Build()` to call storage + queue init automatically#
- [x] 5.2 Add `BuildAsync()` method that builds tracker and initializes storage + queues#
- [x] 5.3 Update `ChangeTrackingConfiguration` to store publisher config for initialization#

## 6. Builder API - DI Configuration#

- [x] 6.1 Modify `AddChangeTracking()` to trigger storage + queue initialization after building tracker#
- [x] 6.2 Integrate initialization into existing `OutboxPublisherHostedService` startup#

## 7. Integration Tests#

- [x] 7.1 Add test for `Build()` with automatic storage initialization (Testcontainers - PostgreSQL)#
- [x] 7.2 Add test for `BuildAsync()` with automatic queue initialization (RabbitMQ)#
- [x] 7.3 Add test for idempotent DDL (call `Build()` multiple times)#
- [x] 7.4 Add test for startup integration with DI-hosted service#
- [x] 7.5 Verify generated tables AND queues exist after `Build()` completes#

## 8. Documentation#

- [x] 8.1 Update README with automatic initialization guide (storage + queues)#
- [x] 8.2 Document that auto-init happens on `Build()` / `BuildAsync()`#
- [x] 8.3 Update API documentation for new behavior#

---

## Implementation Summary

### Completed
- Added `InitializeAsync()` to `IRepository`, `IOutbox`, `IQueuePublisher` interfaces
- Implemented `InitializeAsync()` in all concrete repositories, outboxes, and queue publishers
- Modified `EntityChangeTracker.Build()` and `BuildAsync()` to auto-call initialization
- Added `GetRepositories()` and `GetPublishers()` methods to `IEntityChangeTracker`
- Updated `ChangeTrackingBuilder` and `ChangeTrackingConfiguration` with repository support and `BuildAsync()`
- Added logging to `PostgreSqlDdlExecutor`
- Created integration tests in `AutoInitializationTests.cs`
- **Triggers are now initialized via `PostgreSqlRepository.InitializeAsync()`** (source table + triggers)

### Trigger Initialization
Triggers are created when `PostgreSqlRepository.InitializeAsync()` runs:
1. Creates source table (`CREATE TABLE IF NOT EXISTS`)
2. Creates trigger function `fn_trg_entity_change_outbox()` (CREATE OR REPLACE)
3. Creates trigger `trg_entity_change_outbox` on source table
4. Creates notification function `fn_trg_outbox_notify()` (CREATE OR REPLACE)
5. Creates trigger `trg_outbox_notify` on outbox table

### Key Design Decisions
- Auto-initialization happens automatically on `Build()` / `BuildAsync()` - no separate enable/disable API
- No `GetConnectionString()` in `IOutbox` - initialization is self-contained within each component
- `Build()` blocks on async initialization (calls `GetAwaiter().GetResult()`)
- `BuildAsync()` is the preferred method for async contexts
- **Triggers require both repository AND outbox to be registered** (repository creates source table + triggers, outbox creates outbox table)

### Files Modified
- `src/RayTree.Core/Plugins/IRepository.cs` - Added `InitializeAsync()`
- `src/RayTree.Core/Plugins/IOutbox.cs` - Added `InitializeAsync()`
- `src/RayTree.Core/Plugins/IQueuePublisher.cs` - Added `InitializeAsync()`
- `src/RayTree.Core/Tracking/EntityChangeTracker.cs` - Added auto-init logic
- `src/RayTree.Core/Plugins/ChangeTrackingBuilder.cs` - Added `BuildAsync()` and repository support
- `src/RayTree.Core/Configuration/ChangeTrackingConfiguration.cs` - Added `BuildAsync()`
- `src/RayTree.Core/Configuration/DatabaseInitializationExtensions.cs` - Added `InitializeStorageAsync()` and `InitializeQueuesAsync()` extensions
- `src/RayTree.Hosting/ServiceCollectionExtensions.cs` - Updated for auto-init
- `src/RayTree.Plugins.PostgreSQL/Outbox/PostgreSqlOutbox.cs` - Implemented `InitializeAsync()` (outbox table only)
- `src/RayTree.Plugins.PostgreSQL/Repository/PostgreSqlRepository.cs` - Implemented `InitializeAsync()` (source table + triggers)
- `src/RayTree.Plugins.InMemory/InMemoryOutbox.cs` - Implemented `InitializeAsync()` (no-op)
- `src/RayTree.Plugins.InMemory/InMemoryRepository.cs` - Implemented `InitializeAsync()` (no-op)
- `src/RayTree.Plugins.PostgreSQL/Initialization/PostgreSqlDdlExecutor.cs` - Added logging
- `tests/RayTree.Core.Tests/AutoInitializationTests.cs` - Created integration tests
- `docs/README.md` - Updated with auto-init documentation
- `docs/configuration.md` - Updated with auto-init documentation
