## Context

Every change today is written by `EntityChangeTracker.TrackChangeAsync` → `IOutbox.WriteAsync<TEntity>`, and `PostgreSqlOutbox<TEntity>.WriteAsync` opens a fresh `NpgsqlConnection` and autocommits a single `INSERT` (`src/RayTree.Plugins.PostgreSQL/Outbox/PostgreSqlOutbox.cs:156`). Each entity type is a **separate** `PostgreSqlOutbox<TEntity>` instance writing to its own table, all resolved through `ChangePublisher.GetOutbox(Type)`. There is no shared transaction and no way to group changes.

`EntityChange` (`src/RayTree.Core/Models/EntityChange.cs`) already carries `Guid CorrelationId` defaulting to `Guid.NewGuid()`, and the PostgreSQL outbox already persists a `correlation_id` column. The consumer dedups on `CorrelationId`, so a session-wide correlation override must remain *unique per logical operation* but *shared across that operation's changes* — which is exactly the requested semantics.

The pattern for adding optional capabilities to `IOutbox` already exists: `IsConnectionFault` / `ConnectionComponent` / `ConnectionEndpoint` are default-implemented members that third-party outboxes inherit as no-ops (`src/RayTree.Core/Plugins/Outbox/IOutbox.cs:64-78`). We reuse that approach.

## Goals / Non-Goals

**Goals:**
- Buffer multiple `TrackInsert/Update/Delete` calls across any entity types and persist them in **one** database transaction (all-or-nothing).
- Provide a session-level `CorrelationId` (default new `Guid`, overridable) stamped onto every change in the session.
- Keep the change additive and non-breaking: no existing public signatures change; new `IOutbox` members are default-implemented.
- Keep the DB transaction window short and locks minimal.
- Support unit testing without Docker via `InMemoryOutbox`.

**Non-Goals:**
- Atomic *delivery* (consumer-side aggregation) — explicitly out of scope; publisher loops still publish each row independently.
- Cross-provider or cross-database atomicity (would require 2PC/MSDTC; Npgsql lacks portable support). Fail fast instead.
- Enrolling the outbox write into the caller's *business* `DbContext`/ambient transaction. This is the stronger "true outbox" variant and is left as a follow-up; the `IOutboxTransaction` seam is designed so it can later accept an externally-owned transaction.

## Decisions

### Decision 1: Buffer-then-flush, not streaming-open-transaction
`ChangeSession.TrackXxxAsync` buffers `EntityChange` objects in memory; the DB transaction is opened only inside `CommitAsync` and held just for the flush.

- **Why:** keeps the transaction (and its row locks) short — user code between `Track` calls (validation, I/O) does not hold a DB connection. Centralizes the cross-outbox transaction in one place.
- **Alternative considered:** open the transaction in `BeginSession` and write through immediately. Rejected — holds a connection/transaction open across arbitrary user code, increasing lock contention and connection-pool pressure. Trade-off accepted: buffered changes are not DB-validated until commit, and a very large session buffers in memory (fine for typical session sizes).

### Decision 2: Opaque `IOutboxTransaction` owned above the per-entity outbox
Add to Core:
```csharp
public interface IOutboxTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct = default);
    Task RollbackAsync(CancellationToken ct = default);
}
```
Add two default-implemented `IOutbox` members:
```csharp
Task<IOutboxTransaction?> BeginTransactionAsync(CancellationToken ct = default)
    => Task.FromResult<IOutboxTransaction?>(null);          // null = no session support
Task WriteAsync<TEntity>(EntityChange<TEntity> change, IOutboxTransaction? tx,
    CancellationToken ct = default) where TEntity : class
    => WriteAsync(change, ct);                              // default ignores tx
```
At commit, the session asks the **first touched outbox** to mint one `IOutboxTransaction`, then passes that *same* handle to every entity-type outbox's transactional `WriteAsync`. Because all `PostgreSqlOutbox<T>` instances share the connection string, they write to their own tables inside the one connection/transaction.

- **Why opaque:** `IOutbox` lives in Core and must stay Npgsql-free. The Postgres plugin implements `PostgreSqlOutboxTransaction` carrying the `NpgsqlConnection`/`NpgsqlTransaction`; each outbox casts the handle to its own concrete type.
- **Why default-implemented:** existing/third-party outboxes compile unchanged (LSP/OCP) and degrade by returning `null` (→ session throws a clear error rather than silently writing non-atomically).
- **Alternative considered:** `System.Transactions.TransactionScope` ambient enlistment. Rejected — multiple connections to the same DB under one scope escalate toward distributed transactions, which Npgsql does not support portably (no MSDTC off-Windows).

### Decision 3: Reuse `CorrelationId` for the session identifier (no new column)
The session sets `change.CorrelationId = session.CorrelationId` on every buffered change instead of introducing a separate `SessionId` field/column.

- **Why:** the requested semantics ("override correlation id for all entity changes") *are* a shared correlation value; the column and consumer dedup path already exist. Avoids a schema migration and keeps the data model lean (YAGNI).
- **Consequence to document:** since the consumer dedups on `CorrelationId`, callers MUST NOT reuse a single correlation value across changes that the consumer would treat as distinct messages requiring independent processing within the *same* shared-handler dedup scope. In practice changes are distinguished by entity type/id at dispatch, so a shared correlation across *different* entities is safe; the risk is only same-entity-type duplicate suppression. This trade-off is called out in Risks.
- **Alternative considered:** add a nullable `session_id UUID` column (relies on `SchemaMigrator` `ADD COLUMN IF NOT EXISTS`). Deferred — not needed for the stated requirement; can be added later if consumer-side grouping (the delivery half) is built.

### Decision 4: `ChangeSession` entry point on the tracker
`EntityChangeTracker.BeginSession()` returns `new ChangeSession(_publisher)`. The session uses the existing reflection dispatch pattern (`MethodInfo.MakeGenericMethod`, already used in `OutboxPublisherService`/`ChangeSubscriber`) to call the typed transactional `WriteAsync` for each buffered change's runtime type. `EntityId`/`State`/`EntityType` derivation reuses the same logic as `EntityChangeTracker.TrackTypedAsync` (factor the `GetEntityId` helper so both share it).

### Decision 5: `InMemoryOutbox` gets a trivial transaction
`InMemoryOutbox.BeginTransactionAsync` returns an `InMemoryOutboxTransaction` that stages writes in a list and commits them atomically (single lock-guarded append) on `CommitAsync`, discarding on rollback/dispose.

- **Why:** enables Core unit tests for buffering, commit, rollback, correlation override, and the "no transaction support" path without Docker.

## Risks / Trade-offs

- **Correlation reuse vs consumer dedup** → A shared `CorrelationId` across changes the subscriber treats as the same message could suppress redelivery. *Mitigation:* document that the shared correlation is for grouping/tracing across *different* entities; if independent dedup per change is needed, do not share. Revisit with a dedicated `SessionId` column when the consumer-aggregation feature lands.
- **Homogeneity constraint surprises callers** → A session spanning a Postgres outbox and an InMemory/third-party outbox throws at commit. *Mitigation:* throw `InvalidOperationException` early in `CommitAsync` with a message naming the offending entity type/provider; document the constraint.
- **Large in-memory buffers** → A session buffering huge numbers of changes holds them in memory until commit. *Mitigation:* documented guidance; sessions are meant for a single logical business operation, not bulk loads.
- **Connection ownership / disposal** → The minted `IOutboxTransaction` owns the connection; `ChangeSession` must `await using` it and dispose on both success and failure. *Mitigation:* `ChangeSession` implements `IAsyncDisposable` and wraps commit in try/rollback/dispose.
- **NOTIFY timing** → With `UseNotificationChannel`, `pg_notify` from the trigger is buffered by PostgreSQL until COMMIT, so the fast-path sees session rows only after commit. This composes correctly with no extra work; verified by an integration scenario.

## Migration Plan

Additive, no breaking changes — no migration required. New `IOutbox` members are default-implemented; existing implementations and the existing per-call `TrackXxxAsync` path are untouched. Rollback is trivial (the feature is opt-in via `BeginSession`).

## Open Questions

- Should `BeginSession` accept an optional `Guid correlationId` parameter for convenience, in addition to the settable property? (Leaning yes — `BeginSession(Guid? correlationId = null)`.)
- Should an empty-commit emit a debug log, or stay completely silent? (Leaning silent/no-op per the spec.)
- Future: when the EF Core "same business transaction" variant is built, `IOutboxTransaction` should gain a way to wrap an externally-owned `DbTransaction` — confirm the interface shape stays sufficient.
