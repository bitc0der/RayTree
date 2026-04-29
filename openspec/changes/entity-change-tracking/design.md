## Context

We need a modular entity change tracking system for .NET applications that detects changes to entities and distributes them via message queues. The system should work both as a standalone library and integrated with .NET Generic Host. It must support multiple database backends, queue providers, and serialization formats through a plugin architecture.

## Goals / Non-Goals

**Goals:**
- Detect entity changes automatically via EF Core interceptors or manual registration
- Store changes in an outbox table alongside source entity tables
- Distribute changes reliably via configurable queue providers (RabbitMQ, Kafka)
- Support plugin architecture for repository, outbox, queue, serialization, and compression
- Work standalone (fluent config) or via .NET DI/IHostedService
- Support DB-level triggers as optional change detection mechanism

**Non-Goals:**
- No built-in UI or admin dashboard
- No CDC (Change Data Capture) at the database WAL level — trigger-based or EF Core only
- No support for NoSQL databases in initial release
- No built-in retry/dead-letter handling beyond outbox pattern semantics

## Decisions

### 1. Architecture: Layered Plugin System
- **Core layer**: Abstractions (`IEntityChangeTracker`, `IOutbox`, `IQueuePublisher`, `IChangeSerializer`, `IChangeCompressor`)
- **Plugin layer**: Provider implementations registered via `IChangeTrackingBuilder`
- **Integration layer**: EF Core interceptor (`ISaveChangesInterceptor`) and HostedService (`IHostedService`)

**Rationale**: Clean separation allows swapping any component without affecting others. The builder pattern provides fluent configuration.

**Alternatives considered**:
- MediatR-style mediator — too heavy, adds indirection not needed here
- Single monolithic service — would lock users into specific providers

### 2. Outbox Pattern: Per-Entity Source + Outbox Tables
- For each tracked entity type `T`, maintain: `{T}_source` (original or metadata) and `{T}_outbox` (pending changes)
- Outbox table contains: source columns + metadata (`change_type`, `timestamp`, `published`, `version`, `correlation_id`)
- Changes written in same transaction as entity save (EF Core interceptor)

**Rationale**: Co-located outbox ensures atomicity. Separate tables per entity avoid schema contention and allow entity-specific column tracking.

**Alternatives considered**:
- Single shared outbox table — simpler but loses entity column fidelity, harder to query per-entity
- Separate outbox database — better isolation but adds cross-db transaction complexity

### 3. Change Detection: EF Core Interceptor + Optional DB Triggers
- Primary: `ISaveChangesInterceptor` captures `EntityEntry` changes during `SaveChanges`
- Optional: Database triggers populate outbox directly (for non-EF Core consumers or mixed access)
- Trigger mode requires separate outbox poller since EF Core won't know about trigger-written changes

**Rationale**: EF Core interceptor is the most natural fit for .NET apps. Trigger support covers edge cases where entities change outside EF Core.

### 4. Queue Distribution: HostedService with Polling
- `OutboxPublisherHostedService` polls outbox tables for unpublished changes
- Uses serialization → compression → publish pipeline
- Configurable polling interval, batch size
- Marks changes as published after successful queue publish

**Rationale**: Polling is simpler and more reliable than event-driven for outbox. Works with any queue provider.

**Alternatives considered**:
- Event-driven (notify after write) — tighter coupling, harder to guarantee delivery
- Database NOTIFY/LISTEN (PostgreSQL-specific) — not portable across providers

### 5. Configuration: Dual Mode (Standalone + DI)
- Standalone: `ChangeTrackingConfiguration` builder with `.UseRepository()`, `.UseOutbox()`, `.UseQueue()`, etc.
- DI: Extension methods on `IServiceCollection` — `AddChangeTracking()`, returns builder for plugin registration
- Both share the same underlying configuration objects

**Rationale**: Supports both simple console app scenarios and full .NET host applications.

### 6. Serialization & Compression: Pluggable Pipeline
- `IChangeSerializer` interface — built-in: JSON (System.Text.Json)
- `IChangeCompressor` interface — built-in: Gzip, None
- Pipeline: entity change → serialize → compress → publish
- Configurable per-entity or globally

**Rationale**: Allows users to optimize for their queue payload limits and consumer requirements.

## Risks / Trade-offs

| Risk | Mitigation |
|------|-----------|
| Outbox table grows unbounded if publisher is down | Configurable retention/cleanup job; max outbox rows per entity |
| DB triggers add overhead to write operations | Triggers are optional; users enable only when needed |
| Polling interval vs latency trade-off | Configurable interval; can be tuned per deployment |
| Per-entity tables create schema management burden | Provide migration generator; support table-per-type or single-table mode as future enhancement |
| Serialization format changes break consumers | Version field in outbox schema; support multiple serializers side-by-side during migration |
| EF Core interceptor misses non-EF changes | Document limitation; offer trigger-based mode as alternative |

## Migration Plan

1. Deploy core library + PostgreSQL plugin
2. Run schema migrations to create source/outbox tables for tracked entities
3. Enable EF Core interceptor in application
4. Start outbox publisher hosted service
5. Monitor outbox queue depth and publisher health
6. **Rollback**: Disable interceptor, stop hosted service, drop outbox tables (safe — source tables unaffected)

## Open Questions

- Should outbox support a "compensating delete" pattern (tombstone messages)?
- Should we support batch publishing to queues for throughput, or one-by-one for ordering guarantees?
- How to handle schema evolution of tracked entities (added/removed columns)?
