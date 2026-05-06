## Context

RayTree is a change tracking system implementing the outbox pattern. The current `EntityChange` model (`src/RayTree.Core/Models/EntityChange.cs`) captures change metadata:
- EntityType (string)
- EntityId (string)
- ChangeType (Insert/Update/Delete)
- Timestamp, Version, CorrelationId, Published flag

However, it does not capture the actual entity content. Consumers receiving these changes cannot:
- Reconstruct entity state at the time of change
- Determine which fields changed during an update
- Build event sourcing workflows without querying the source system

The outbox pipeline flows: EntityChange → Serialize → Compress → Publish/Store

## Goals / Non-Goals

**Goals:**
- Extend EntityChange to carry entity content payload (before/after state)
- Support configurable content tracking (before, after, or both)
- Enable field-level change tracking for updates
- Update outbox schema to persist content alongside metadata
- Maintain backward compatibility for existing consumers

**Non-Goals:**
- Entity diffing/comparison logic (consumers handle this)
- Automatic change detection from POCO entities
- Content versioning or historical queries
- Schema migration tools for existing outbox tables

## Decisions

### 1. Content Storage Format: JSON Serialized Content

**Decision**: Store entity content as JSON strings in EntityChange (`BeforeContent` and `AfterContent` properties).

**Rationale**: JSON provides human-readable format, works across all plugins (PostgreSQL JSONB, InMemory strings), and aligns with existing serializer pipeline.

**Alternatives Considered**:
- Binary serialization: More compact but less portable and debuggable
- Separate content table: Cleaner normalization but adds JOIN complexity
- Field-level tracking only: Loses complete state context

### 2. Content Tracking Configuration

**Decision**: Add `ContentTrackingOptions` to `ChangeTrackingConfiguration` with three modes:
- `None`: No content tracked (default, backward compatible)
- `AfterOnly`: Track state after change (insert/update)
- `BeforeAndAfter`: Track both before and after state (update only)

**Rationale**: Allows opt-in per deployment. `AfterOnly` covers most use cases (event sourcing). `BeforeAndAfter` enables diff scenarios.

### 3. Update Change Tracking: Before State Capture

**Decision**: For updates with `BeforeAndAfter` mode, capture entity state via `IRepository.GetByIdAsync` before applying the update.

**Rationale**: Repository pattern already exists (`IRepository<TEntity>`). Single round-trip to fetch current state.

**Alternatives Considered**:
- Intercept DbContext changes: Tightly coupled to EF Core
- Snapshot in ChangeTracker: Requires holding state in memory

### 4. Null Content for Deletes

**Decision**: For delete operations, `AfterContent` is always null. `BeforeContent` contains the entity state before deletion (if configured).

**Rationale**: After deletion, there is no "after" state. Before state is valuable for audit/deletion events.

## Risks / Trade-offs

- **[Storage Growth]** → Content payloads increase outbox table size significantly. Mitigation: Configure retention policies and cleanup jobs.
- **[Serialization Overhead]** → Serializing large entities impacts performance. Mitigation: Use efficient serializers (MessagePack/Protobuf) and compression.
- **[Sensitive Data]** → Content may include PII or secrets. Mitigation: Document that content tracking should respect data privacy requirements; consider field-level filtering in future.
- **[Plugin Updates Required]** → PostgreSQL, InMemory, and EF Core plugins need schema updates. Mitigation: Add nullable columns with defaults to minimize migration impact.
