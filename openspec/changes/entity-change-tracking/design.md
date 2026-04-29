## Context

We need a modular entity change tracking system for .NET applications that detects changes to entities and distributes them via message queues. The system should work both as a standalone library and integrated with .NET Generic Host. It must support multiple database backends, queue providers, and serialization formats through a plugin architecture.

## Goals / Non-Goals

**Goals:**
- Detect entity changes automatically via EF Core interceptors or manual registration
- Store changes in an outbox table alongside source entity tables
- Distribute changes reliably via configurable queue providers (RabbitMQ, Kafka)
- Support plugin architecture for repository, outbox, and queue providers
- Support separate plugin assemblies for serializers and compressors
- Work standalone (fluent config) or via .NET DI/IHostedService
- Support DB-level triggers as optional change detection mechanism

**Non-Goals:**
- No built-in UI or admin dashboard
- No CDC (Change Data Capture) at the database WAL level — trigger-based or EF Core only
- No support for NoSQL databases in initial release
- No built-in retry/dead-letter handling beyond outbox pattern semantics

## Decisions

### 1. Architecture: Layered Plugin System with Separate Assemblies
- **Core layer** (`RayTree.Core`): Abstractions (`IEntityChangeTracker`, `IOutbox`, `IQueuePublisher`, `IChangeSerializer`, `IChangeCompressor`)
- **Plugin layer** (`RayTree.Plugins.*`):
  - `RayTree.Plugins.PostgreSQL`: Repository and outbox implementations
  - `RayTree.Plugins.RabbitMQ`: Queue publisher
  - `RayTree.Plugins.Kafka`: Queue publisher
  - `RayTree.Plugins.Serializers`: JSON, Protobuf, MessagePack serializers
  - `RayTree.Plugins.Compressors`: Gzip, Brotli, LZ4, NoOp compressors
- **Integration layer** (`RayTree.EntityFrameworkCore`, `RayTree.Hosting`): EF Core interceptor and IHostedService

**Rationale**: Separate assemblies for serializers and compressors allow consumers to reference only the plugins they need. A lightweight consumer might only need the core + one serializer without pulling in database or queue dependencies. The builder pattern provides fluent configuration across all assemblies.

**Alternatives considered**:
- MediatR-style mediator — too heavy, adds indirection not needed here
- Single monolithic assembly — would force all dependencies on every consumer
- Serialization/compression in plugin assembly — would mix concerns and force serializer consumers to pull in database plugins

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

### 4. Queue Distribution: Dual Mode (Polling + NOTIFY)
- **Default**: `OutboxPublisherHostedService` polls outbox tables for unpublished changes
- **NOTIFY mode** (PostgreSQL only): Dedicated connection issues `LISTEN` on configured channel; trigger on outbox table fires `pg_notify` on insert
- **Fallback poller**: Activates on connection loss or as periodic safety net; configurable interval
- Uses serialization → compression → publish pipeline
- Configurable polling interval, batch size
- Marks changes as published after successful queue publish

**Rationale**: Polling works universally but introduces latency and DB load. NOTIFY gives near-instant publishing with zero DB load when idle, but is PostgreSQL-specific and requires connection management. Fallback poller ensures no messages are lost on reconnect.

**Alternatives considered**:
- Event-driven (notify after write) — tighter coupling, harder to guarantee delivery
- Database NOTIFY/LISTEN without fallback — lost notifications on reconnect with no recovery

### 5. Configuration: Dual Mode (Standalone + DI)
- Standalone: `ChangeTrackingConfiguration` builder with `.UseRepository()`, `.UseOutbox()`, `.UseQueue()`, etc.
- DI: Extension methods on `IServiceCollection` — `AddChangeTracking()`, returns builder for plugin registration
- Both share the same underlying configuration objects

**Rationale**: Supports both simple console app scenarios and full .NET host applications.

### 6. Serialization & Compression: Separate Plugin Assemblies
- `IChangeSerializer` interface defined in core — implementations in `RayTree.Plugins.Serializers`: JSON (System.Text.Json), Protobuf (protobuf-net), MessagePack (MessagePack-CSharp)
- `IChangeCompressor` interface defined in core — implementations in `RayTree.Plugins.Compressors`: Gzip, Brotli, LZ4, NoOp
- Pipeline: entity change → serialize → compress → publish
- Configurable per-entity or globally

**Rationale**: Separate assemblies allow consumers to pick only the serializers/compressors they need without pulling in database or queue dependencies. A consumer using only Kafka + JSON + Gzip references just Core + Serializers + Compressors + Kafka plugins.

### 7. Subscriber Configuration: Mirror Publisher Model
- Subscriber builder uses same per-entity configuration pattern: `.ConsumeEntity<Order>()`, `.FromKafka()`, `.FromRabbitMq()`
- Per-entity serializer/compressor resolution matches publisher config
- Handlers registered via `.OnChange<T>(ChangeType, handler)`
- Deduplication via correlation_id with pluggable store (in-memory, Redis, DB)
- Error policies per entity: retry, dead-letter, skip
- `ChangeSubscriberHostedService` manages consume loop for all configured entities
- Shares `MessageEnvelope` schema and serializer/compressor interfaces with publisher

**Rationale**: Symmetric API reduces learning curve. Publisher and subscriber config use same building blocks. Consumers don't need to know envelope internals — the framework resolves serializer/compressor automatically.

**Alternatives considered**:
- Force consumers to handle raw envelope — gives control but increases boilerplate
- Use MassTransit directly — good but locks into one framework; we want queue-agnostic abstraction

### 8. In-Memory Plugins for Testing and Development
- Assembly: `RayTree.Plugins.InMemory` — zero external dependencies beyond `RayTree.Core`
- `InMemoryRepository`: stores entities in `ConcurrentDictionary<TKey, TEntity>`
- `InMemoryOutbox`: stores changes in `ConcurrentBag<EntityChange>` with thread-safe query and cleanup
- `InMemoryQueue`: in-process pub/sub using `Channel<T>` with per-entity-type broadcast
- Mixed configuration supported: e.g., in-memory repo + RabbitMQ queue
- Subscribers consume directly from `InMemoryQueue` — no serialization/compression overhead within the same process
- Suitable for: unit tests, integration tests, local development without infrastructure

**Rationale**: Testing change tracking requires infrastructure (PostgreSQL, RabbitMQ) which slows CI and makes local dev harder. In-memory plugins provide fast, deterministic tests with zero setup. The same code paths (interceptor → outbox → publisher → subscriber) are exercised, just with in-memory substitutes.

**Alternatives considered**:
- Mock interfaces in tests — doesn't exercise real integration between components
- Docker Compose for tests — slower, more complex, flaky in CI
- Testcontainers — good but adds dependency and startup overhead; in-memory is faster for unit-level tests

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
