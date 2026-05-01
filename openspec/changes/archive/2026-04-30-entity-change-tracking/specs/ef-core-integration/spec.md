## ADDED Requirements

### Requirement: EF Core SaveChanges interceptor
The system SHALL implement `ISaveChangesInterceptor` to automatically detect and capture entity changes during `SaveChanges` and `SaveChangesAsync` calls.

#### Scenario: Intercept SaveChanges
- **WHEN** `SaveChanges` is called on a DbContext with tracked entities
- **THEN** the interceptor SHALL detect all Added, Modified, and Deleted entity entries

#### Scenario: Intercept SaveChangesAsync
- **WHEN** `SaveChangesAsync` is called on a DbContext with tracked entities
- **THEN** the interceptor SHALL detect all Added, Modified, and Deleted entity entries asynchronously

#### Scenario: Only tracked entities are captured
- **WHEN** SaveChanges includes entities not registered for change tracking
- **THEN** only changes to registered entities SHALL be captured in the outbox

### Requirement: Entity type registration
The system SHALL allow registering entity types for EF Core change tracking via fluent configuration.

#### Scenario: Register single entity type
- **WHEN** an entity type `Order` is registered for tracking
- **THEN** changes to `Order` entities during SaveChanges SHALL be captured

#### Scenario: Register multiple entity types
- **WHEN** multiple entity types are registered for tracking
- **THEN** changes to all registered types SHALL be captured during SaveChanges

### Requirement: Outbox write in same transaction
The EF Core integration SHALL write outbox entries within the same database transaction as the entity changes.

#### Scenario: Transactional consistency
- **WHEN** entity changes and outbox writes are part of the same SaveChanges call
- **THEN** both succeed or both are rolled back together

#### Scenario: Rollback on outbox failure
- **WHEN** the outbox write fails during SaveChanges
- **THEN** the entity changes SHALL be rolled back

### Requirement: DbContext integration
The system SHALL integrate with DbContext through DI registration and automatic interceptor attachment.

#### Scenario: Automatic interceptor attachment
- **WHEN** `AddChangeTracking()` is called on IServiceCollection
- **THEN** the change tracking interceptor SHALL be automatically attached to registered DbContexts

#### Scenario: Multiple DbContext support
- **WHEN** an application has multiple DbContexts
- **THEN** each DbContext SHALL be independently configurable for change tracking
