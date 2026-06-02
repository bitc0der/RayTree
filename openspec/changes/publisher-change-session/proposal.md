## Why

Today each `TrackInsertAsync` / `TrackUpdateAsync` / `TrackDeleteAsync` call writes one outbox row on its own short-lived connection with implicit autocommit (`PostgreSqlOutbox.WriteAsync` opens a fresh `NpgsqlConnection` per call). When a logical business operation mutates several entities, there is no way to persist those change records as a single unit — if the process dies partway through the calls, the outbox ends up with a half-written change set. There is also no way to stamp a shared correlation identifier across the changes that belong to the same logical operation, so downstream consumers cannot tell which changes originated together.

## What Changes

- Introduce a **publisher-side change session**: a scoped object obtained from `EntityChangeTracker.BeginSession()` that buffers multiple `TrackInsert/Update/Delete` calls across any entity types and persists all of them in **one database transaction** on `CommitAsync` (all-or-nothing). Disposing without committing rolls back / discards.
- Add a **shared correlation override**: the session exposes a `CorrelationId` (defaulted to a new `Guid`, overridable by the caller) that is stamped onto every `EntityChange` written during the session, so all changes in one logical operation carry the same correlation value.
- Add an opaque `IOutboxTransaction` abstraction in Core and two **default-implemented** `IOutbox` members (`BeginTransactionAsync`, a `WriteAsync` overload taking the transaction) so existing and third-party outbox implementations compile unchanged and degrade gracefully when they do not support sessions.
- Implement transactional writes in `PostgreSqlOutbox<TEntity>` (shared `NpgsqlConnection` + `NpgsqlTransaction` across all entity-type outboxes pointing at the same database) and a trivial in-memory transaction in `InMemoryOutbox` for tests.
- Clarify scope explicitly: this provides atomic **persistence** of a change set, **not** atomic **delivery** — the per-entity `OutboxPublisherService` loops still publish each row independently.

This change is **additive and non-breaking**: no existing public method signatures change; the new `IOutbox` members are default-implemented.

## Capabilities

### New Capabilities
- `publisher-change-session`: Atomic, multi-entity publisher-side change sessions — buffering changes across entity types, persisting them in a single outbox transaction, and applying a shared (overridable) correlation id to every change in the session.

### Modified Capabilities
<!-- None. Existing TrackXxxAsync behaviour is unchanged; the session is a new, additive entry point. -->

## Impact

- **RayTree.Core**: new `IOutboxTransaction` interface; new `ChangeSession` type; `EntityChangeTracker.BeginSession()` entry point; two default-implemented members added to `IOutbox`; new nullable `SessionId`/correlation handling on the change-write path. New public API surface.
- **RayTree.Plugins.PostgreSQL**: `PostgreSqlOutbox<TEntity>` gains transaction-aware writes and a `PostgreSqlOutboxTransaction` implementation; outbox INSERT/schema reflects the shared correlation override (no new column strictly required — reuses the existing `correlation_id` column, since the session overrides it rather than introducing a separate identifier).
- **RayTree.Plugins.InMemory**: `InMemoryOutbox` gains a trivial transaction for unit testing sessions without Docker.
- **Constraints**: a session requires all participating entity outboxes to be the same provider pointing at the same database (cross-provider / cross-DB atomicity would require 2PC and is out of scope — fail fast with a clear exception).
- **Tests**: Core unit tests (InMemory) for buffering, commit, rollback, and correlation override; PostgreSQL integration test for cross-table atomic commit and rollback.
- **Docs**: CLAUDE.md architecture notes and a short usage section.
