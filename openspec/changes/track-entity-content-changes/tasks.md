## 1. Core Model Updates

- [ ] 1.1 Add `BeforeContent` and `AfterContent` properties (string, nullable) to EntityChange model in `src/RayTree.Core/Models/EntityChange.cs`
- [ ] 1.2 Create `ContentTrackingMode` enum in `src/RayTree.Core/Tracking/` with values: `None`, `AfterOnly`, `BeforeAndAfter`
- [ ] 1.3 Create `ContentTrackingOptions` class in `src/RayTree.Core/Configuration/` with `Mode` property of type `ContentTrackingMode`

## 2. Configuration Updates

- [ ] 2.1 Add `ContentTrackingOptions` property to `ChangeTrackingConfiguration` class
- [ ] 2.2 Add `WithContentTracking(ContentTrackingMode mode)` method to `ChangeTrackingConfiguration`
- [ ] 2.3 Update `ChangeTrackingBuilder` to store and propagate content tracking options per entity type
- [ ] 2.4 Add `GetContentTrackingMode(Type entityType)` method to `EntityChangeTracker` or builder

## 3. Repository Enhancements

- [ ] 3.1 Update `IRepository<TEntity>` interface to include method for fetching entity as JSON string
- [ ] 3.2 Add `GetAsJsonAsync(object id, CancellationToken)` method to `IRepository<TEntity>` for content capture
- [ ] 3.3 Implement `GetAsJsonAsync` in repository implementations (if any base implementation exists)

## 4. Tracker Implementation

- [ ] 4.1 Update `EntityChangeTracker.TrackChangeAsync` to accept optional before/after content parameters
- [ ] 4.2 Add logic to capture before state via repository when mode is `BeforeAndAfter` and change type is Update
- [ ] 4.3 Add logic to capture after state via serialization when mode is `AfterOnly` or `BeforeAndAfter`
- [ ] 4.4 Handle delete operations: capture before content only (if configured), after content remains null
- [ ] 4.5 Handle insert operations: capture after content only (if configured), before content remains null
- [ ] 4.6 Update `TrackChangesAsync` to pass correlation ID and handle content for batch operations

## 5. Outbox Schema Updates

- [ ] 5.1 Add `BeforeContent` (text/string) and `AfterContent` (text/string) columns to outbox table schema in `OutboxTableSchema.cs`
- [ ] 5.2 Update `IOutbox.WriteAsync` to persist content properties
- [ ] 5.3 Update `IOutbox.GetUnpublishedAsync` to retrieve content properties
- [ ] 5.4 Update PostgreSQL plugin outbox implementation to handle new columns (add nullable columns with defaults)
- [ ] 5.5 Update InMemory plugin outbox implementation to store/retrieve content
- [ ] 5.6 Update EntityFrameworkCore plugin outbox implementation if applicable

## 6. Serialization Pipeline Updates

- [ ] 6.1 Update `IChangeSerializer.SerializeAsync` to include `BeforeContent` and `AfterContent` in serialized output
- [ ] 6.2 Update `IChangeSerializer.DeserializeAsync` to restore `BeforeContent` and `AfterContent` from serialized input
- [ ] 6.3 Update JSON serializer implementation (`RayTree.Plugins.Serializers.Json`) to handle content properties
- [ ] 6.4 Update MessagePack serializer implementation if applicable
- [ ] 6.5 Update Protobuf serializer implementation if applicable
- [ ] 6.6 Ensure compression pipeline (`IChangeCompressor`) correctly handles updated serialization output

## 7. Integration and Testing

- [ ] 7.1 Add unit tests for EntityChange model with content properties
- [ ] 7.2 Add unit tests for content tracking configuration
- [ ] 7.3 Add integration tests for track with content (AfterOnly mode)
- [ ] 7.4 Add integration tests for track with content (BeforeAndAfter mode)
- [ ] 7.5 Add integration tests for outbox persistence with content
- [ ] 7.6 Add integration tests for serialization/deserialization with content
- [ ] 7.7 Add tests for delete operations with content tracking
- [ ] 7.8 Verify backward compatibility: changes without content still work (mode = None)

## 8. Documentation and Cleanup

- [ ] 8.1 Update XML documentation comments on modified interfaces and classes
- [ ] 8.2 Add usage examples for content tracking in code comments or docs
- [ ] 8.3 Verify all plugin projects compile with updates
- [ ] 8.4 Run existing tests to ensure no regressions
