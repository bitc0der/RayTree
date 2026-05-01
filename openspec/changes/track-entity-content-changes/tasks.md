## 1. Core Model Updates

- [ ] 1.1 Make `EntityChange` generic (`EntityChange<TEntity>`) with `State` property of type `TEntity` in `src/RayTree.Core/Models/EntityChange.cs`
- [ ] 1.2 Keep non-generic `EntityChange` class for backward compatibility (with no State property)
- [ ] 1.3 Update `EntityChange<TEntity>` to include a parameterless constructor that sets `State` to `default(TEntity)`

## 2. Tracker Implementation

- [ ] 2.1 Update `EntityChangeTracker.TrackChangeAsync` to create `EntityChange<TEntity>` with typed `State` property
- [ ] 2.2 Add logic to capture entity state after insert/update as typed `TEntity` object
- [ ] 2.3 Add logic to capture entity state before delete as typed `TEntity` object
- [ ] 2.4 Update `TrackChangesAsync` to pass correlation ID and handle typed state for batch operations

## 3. Outbox Schema Updates

- [ ] 3.1 Add columns to outbox table schema for each entity property in `OutboxTableSchema.cs` (using plain columns approach)
- [ ] 3.2 Update `IOutbox.WriteAsync` to persist typed `State` properties as plain columns
- [ ] 3.3 Update `IOutbox.GetUnpublishedAsync` to retrieve plain columns and reconstruct `State`
- [ ] 3.4 Update PostgreSQL plugin outbox implementation to handle plain columns for entity state
- [ ] 3.5 Update InMemory plugin outbox implementation to store/retrieve typed `State`
- [ ] 3.6 Update EntityFrameworkCore plugin outbox implementation if applicable

## 4. Serialization Pipeline Updates

- [ ] 4.1 Update `IChangeSerializer.SerializeAsync` to include typed `State` in serialized output
- [ ] 4.2 Update `IChangeSerializer.DeserializeAsync` to restore typed `State` from serialized input
- [ ] 4.3 Update JSON serializer implementation (`RayTree.Plugins.Serializers.Json`) to handle typed `State`
- [ ] 4.4 Update MessagePack serializer implementation if applicable
- [ ] 4.5 Update Protobuf serializer implementation if applicable
- [ ] 4.6 Ensure compression pipeline (`IChangeCompressor`) correctly handles updated serialization output

## 5. Integration and Testing

- [ ] 5.1 Add unit tests for generic `EntityChange<TEntity>` model with `State` property
- [ ] 5.2 Add unit tests for non-generic `EntityChange` backward compatibility
- [ ] 5.3 Add integration tests for track with typed state (insert)
- [ ] 5.4 Add integration tests for track with typed state (update)
- [ ] 5.5 Add integration tests for track with typed state (delete)
- [ ] 5.6 Add integration tests for outbox persistence with typed state
- [ ] 5.7 Add integration tests for serialization/deserialization with typed state
- [ ] 5.8 Verify backward compatibility: non-generic EntityChange still works

## 6. Documentation and Cleanup

- [ ] 6.1 Update XML documentation comments on modified interfaces and classes
- [ ] 6.2 Add usage examples for generic EntityChange with typed State in code comments or docs
- [ ] 6.3 Verify all plugin projects compile with updates
- [ ] 6.4 Run existing tests to ensure no regressions
