## Why

Currently, EntityChange only captures metadata about changes (entity type, ID, change type) without tracking the actual entity content. Consumers cannot reconstruct entity state or determine what fields changed without additional queries. Adding content tracking enables event sourcing patterns, audit trails, and downstream systems to process complete change information.

## What Changes

- Extend EntityChange model to include entity content payload (before/after state)
- Add configuration options to control content tracking behavior (track before, after, or both)
- Support selective field tracking for updates to minimize payload size
- Update serialization/compression pipeline to handle content payloads
- Add outbox schema updates to persist content alongside change metadata

## Capabilities

### New Capabilities
- `entity-content-tracking`: Capture and persist entity content (state) as part of change tracking, including before/after snapshots and changed fields

### Modified Capabilities

## Impact

- **Core Models**: EntityChange model extended with content payload properties
- **Outbox Schema**: Database schema updates to store content (JSON/BSON columns)
- **Serialization**: Updated pipeline to serialize/compress content payloads
- **Configuration**: New options in ChangeTrackingConfiguration for content tracking behavior
- **Storage**: Increased outbox storage requirements due to content payloads
- **Plugins**: PostgreSQL, InMemory, and EntityFrameworkCore plugins need updates to handle content
