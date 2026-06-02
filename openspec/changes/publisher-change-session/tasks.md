## 1. Core transaction seam

- [ ] 1.1 Add `IOutboxTransaction : IAsyncDisposable` (`CommitAsync`, `RollbackAsync`) to `src/RayTree.Core/Plugins/Outbox/`. Build `RayTree.Core`.
- [ ] 1.2 Add two default-implemented members to `IOutbox` (`src/RayTree.Core/Plugins/Outbox/IOutbox.cs`): `BeginTransactionAsync` (default returns `null`) and a `WriteAsync<TEntity>(change, IOutboxTransaction?, ct)` overload (default delegates to existing `WriteAsync`). Build `RayTree.Core` and confirm existing `IOutbox` implementations still compile.

## 2. ChangeSession

- [ ] 2.1 Factor the entity-id derivation out of `EntityChangeTracker` (`GetEntityId<TEntity>`) into a shared internal helper reusable by `ChangeSession`. Build `RayTree.Core`.
- [ ] 2.2 Implement `ChangeSession : IAsyncDisposable` in `src/RayTree.Core/Tracking/`: settable/defaulted `CorrelationId`; `TrackInsertAsync`/`TrackUpdateAsync`/`TrackDeleteAsync<TEntity>` that buffer `EntityChange` (stamping `CorrelationId`); `CommitAsync` that mints one `IOutboxTransaction` from the first outbox, writes all buffered changes via reflection dispatch (`MakeGenericMethod` on the transactional `WriteAsync`), commits, and rolls back on failure; guards for already-committed/disposed and empty-session no-op; homogeneity/`null`-transaction → `InvalidOperationException`. Build `RayTree.Core`.
- [ ] 2.3 Add `EntityChangeTracker.BeginSession(Guid? correlationId = null)` returning a `ChangeSession`. Build `RayTree.Core`.
- [ ] 2.4 Write Core unit tests (`tests/RayTree.Core.Tests`) using `InMemoryOutbox`-style fakes for: begin/default correlation, track buffers (no write before commit), atomic commit across entity types, rollback-on-failure writes nothing, correlation override applied to all, dispose-without-commit discards, commit-after-dispose/double-commit throws, no-transaction-support throws. Build and run `dotnet test tests/RayTree.Core.Tests`.

## 3. InMemory outbox transaction

- [ ] 3.1 Implement `InMemoryOutboxTransaction` and override `BeginTransactionAsync` + transactional `WriteAsync` in `src/RayTree.Plugins.InMemory/InMemoryOutbox.cs` (stage writes, atomic commit, discard on rollback/dispose). Build `RayTree.Plugins.InMemory`.
- [ ] 3.2 Add `tests/RayTree.Plugins.InMemory.Tests` covering commit applies staged writes and rollback/dispose discards them. Build and run `dotnet test tests/RayTree.Plugins.InMemory.Tests`.

## 4. PostgreSQL transactional writes

- [ ] 4.1 Implement `PostgreSqlOutboxTransaction : IOutboxTransaction` wrapping `NpgsqlConnection` + `NpgsqlTransaction` in `src/RayTree.Plugins.PostgreSQL/Outbox/`. Build `RayTree.Plugins.PostgreSQL`.
- [ ] 4.2 Override `BeginTransactionAsync` (open one connection + `BeginTransactionAsync`) and the transactional `WriteAsync` overload (use the handle's connection/transaction when supplied; otherwise existing self-connection path) in `PostgreSqlOutbox<TEntity>`. Confirm `correlation_id` is written from the change. Build `RayTree.Plugins.PostgreSQL`.
- [ ] 4.3 Add PostgreSQL integration tests (`tests/RayTree.Plugins.PostgreSQL.Tests`, Testcontainers, `[NonParallelizable]`, unique table names): cross-table atomic commit, rollback leaves both tables empty, shared correlation persisted, and (with `UseNotificationChannel`) notification observed only after commit. Build and run `dotnet test tests/RayTree.Plugins.PostgreSQL.Tests`.

## 5. Docs & finalize

- [ ] 5.1 Update `CLAUDE.md` (Core + PostgreSQL plugin sections + a Key Design Decision) to describe `BeginSession`, `IOutboxTransaction`, the buffer-then-flush model, the homogeneity constraint, and the persistence-not-delivery scope boundary.
- [ ] 5.2 Build the full solution in Release (`dotnet build RayTree.slnx -c Release`) and run all unit-test projects to confirm no warnings-as-errors regressions.
