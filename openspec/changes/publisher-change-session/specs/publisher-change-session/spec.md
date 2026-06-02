## ADDED Requirements

### Requirement: Session creation and lifecycle

The system SHALL expose `EntityChangeTracker.BeginSession()` returning a `ChangeSession` that buffers entity changes and is committed or discarded as a unit. `ChangeSession` SHALL implement `IAsyncDisposable`; disposing a session that has not been committed SHALL discard all buffered changes without persisting them.

Example:

```csharp
// Atomic multi-entity persistence: all rows written, or none.
await using var session = tracker.BeginSession();

await session.TrackInsertAsync(order);
await session.TrackInsertAsync(orderLine);
await session.TrackUpdateAsync(inventory);

await session.CommitAsync(ct);
// Leaving the `await using` block without CommitAsync discards the buffered changes.
```

Expected public surface:

```csharp
public sealed class ChangeSession : IAsyncDisposable
{
    public Guid CorrelationId { get; set; }   // defaulted to a new Guid, overridable before first track

    public Task TrackInsertAsync<TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class;
    public Task TrackUpdateAsync<TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class;
    public Task TrackDeleteAsync<TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class;

    public Task CommitAsync(CancellationToken ct = default);
    public ValueTask DisposeAsync();
}

// on EntityChangeTracker:
public ChangeSession BeginSession(Guid? correlationId = null);
```

#### Scenario: Begin a session

- **WHEN** a caller invokes `tracker.BeginSession()`
- **THEN** a new `ChangeSession` is returned with a non-empty `CorrelationId` and no buffered changes

#### Scenario: Dispose without commit discards changes

- **WHEN** a caller buffers one or more changes in a session and disposes it without calling `CommitAsync`
- **THEN** no rows are written to any outbox

#### Scenario: Commit after dispose is rejected

- **WHEN** a caller calls `CommitAsync` on a session that has already been disposed or already committed
- **THEN** the system throws `ObjectDisposedException` (disposed) or `InvalidOperationException` (already committed)

### Requirement: Atomic multi-entity persistence

On `CommitAsync`, the system SHALL persist all changes buffered during the session in a single database transaction so that either all change records are written or none are. If persisting any buffered change fails, the system SHALL roll back the transaction and SHALL NOT leave any partial change records, and SHALL propagate the original exception.

#### Scenario: All buffered changes commit together

- **WHEN** a session buffers changes for several entities (including across different entity types) and `CommitAsync` succeeds
- **THEN** every buffered change is present in its outbox after commit

#### Scenario: A failure during commit writes nothing

- **WHEN** writing one of the buffered changes fails during `CommitAsync`
- **THEN** none of the session's changes are present in any outbox
- **AND** the original exception is propagated to the caller

#### Scenario: Empty session commit is a no-op

- **WHEN** a caller calls `CommitAsync` on a session with no buffered changes
- **THEN** no transaction is opened and no rows are written

### Requirement: Shared correlation override across session changes

A `ChangeSession` SHALL carry a `CorrelationId` that defaults to a newly generated `Guid` and MAY be overridden by the caller before any change is buffered. Every change buffered in the session SHALL be stamped with the session's `CorrelationId`, overriding the per-change default.

Example:

```csharp
// Option A: supply the correlation id at creation.
await using var session = tracker.BeginSession(correlationId: command.RequestId);

// Option B: set it before tracking anything.
await using var session = tracker.BeginSession();
session.CorrelationId = command.RequestId;

await session.TrackInsertAsync(order);
await session.TrackUpdateAsync(inventory);
await session.CommitAsync(ct);
// → every persisted change carries command.RequestId as its CorrelationId.
```

#### Scenario: Default correlation is applied to all changes

- **WHEN** a caller buffers multiple changes in a session without overriding the correlation id
- **THEN** all persisted changes carry the same session-generated `CorrelationId`

#### Scenario: Caller-supplied correlation is applied to all changes

- **WHEN** a caller sets the session `CorrelationId` to a specific value and then buffers changes
- **THEN** all persisted changes carry that supplied `CorrelationId`

### Requirement: Session change tracking API

`ChangeSession` SHALL expose `TrackInsertAsync<TEntity>`, `TrackUpdateAsync<TEntity>`, and `TrackDeleteAsync<TEntity>` (each `where TEntity : class`) that buffer the corresponding change without writing to the outbox until `CommitAsync` is called. These methods SHALL derive `EntityType`, `EntityId`, and `State` consistently with the existing `EntityChangeTracker.TrackXxxAsync` methods.

Example (realistic service method):

```csharp
public async Task PlaceOrderAsync(PlaceOrder cmd, CancellationToken ct)
{
    await using var session = tracker.BeginSession(correlationId: cmd.CommandId);

    var order = new Order { Id = cmd.OrderId, CustomerId = cmd.CustomerId, Total = cmd.Total };
    await session.TrackInsertAsync(order);

    foreach (var line in cmd.Lines)
        await session.TrackInsertAsync(new OrderLine { Id = line.Id, OrderId = order.Id, Sku = line.Sku });

    var stock = await _inventory.ReserveAsync(cmd.Lines, ct); // user I/O — no DB txn held yet
    await session.TrackUpdateAsync(stock);

    await session.CommitAsync(ct); // order + lines + inventory committed in one transaction
}
```

#### Scenario: Track methods buffer rather than write immediately

- **WHEN** a caller invokes `TrackInsertAsync` on a session
- **THEN** no outbox write occurs until `CommitAsync` is called

#### Scenario: Buffered change captures entity identity and state

- **WHEN** a caller buffers a change for an entity
- **THEN** the resulting persisted change has the entity's type, id, change type, and state populated the same way as the non-session track methods

### Requirement: Provider support and homogeneity constraints

The outbox transaction mechanism SHALL be expressed through an opaque `IOutboxTransaction` abstraction and default-implemented `IOutbox` members so that existing and third-party outbox implementations remain source- and binary-compatible. A session SHALL require that all participating entity outboxes support transactions and belong to the same provider and database; otherwise the system SHALL throw `InvalidOperationException` with a clear message and SHALL NOT silently fall back to non-atomic writes.

#### Scenario: Outbox without transaction support is rejected

- **WHEN** a session attempts to commit changes whose outbox returns no transaction from `BeginTransactionAsync`
- **THEN** the system throws `InvalidOperationException` indicating the provider does not support sessions

#### Scenario: Mixed providers in one session are rejected

- **WHEN** a session buffers changes routed to outboxes of different providers (incompatible transaction handles)
- **THEN** the system throws `InvalidOperationException` and writes nothing

#### Scenario: Existing outbox implementations remain compatible

- **WHEN** the new `IOutbox` members are added
- **THEN** existing implementations that do not override them continue to compile and behave as non-session writers

### Requirement: PostgreSQL transactional session writes

`PostgreSqlOutbox<TEntity>` SHALL implement transaction-aware writes such that all entity-type outboxes sharing the same connection string participate in a single `NpgsqlConnection`/`NpgsqlTransaction` for the duration of a session commit. When the PostgreSQL NOTIFY/LISTEN fast-path is enabled, notifications for session changes SHALL only be observable after the session transaction commits.

#### Scenario: Cross-table atomic commit

- **WHEN** a session commits changes for two different entity types backed by PostgreSQL outboxes on the same database
- **THEN** both outbox tables contain the changes after commit, written within one transaction

#### Scenario: Rollback leaves both tables unchanged

- **WHEN** a PostgreSQL session commit fails after writing to one table
- **THEN** neither table contains any of the session's changes

#### Scenario: Notifications fire only on commit

- **WHEN** `UseNotificationChannel` is enabled and a session is in progress
- **THEN** no NOTIFY for the session's changes is delivered until the session transaction commits

### Requirement: Scope boundary — persistence not delivery

The publisher change session SHALL guarantee atomic persistence of the change set only. It SHALL NOT guarantee atomic delivery: the per-entity `OutboxPublisherService` loops continue to publish each persisted change independently.

#### Scenario: Messages are still published independently

- **WHEN** a session commits multiple changes
- **THEN** each change is subsequently published as an independent message by the normal publisher loops, sharing the session's correlation id
