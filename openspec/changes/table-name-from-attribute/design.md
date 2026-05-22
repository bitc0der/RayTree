## Context

`EntityColumnMapper.GetTableName(Type)` already encapsulates the canonical table-name resolution rule: honour `[Table("name")]` if present, otherwise snake_case the type's short name. Both `PostgreSqlOutbox<TEntity>` and `PostgreSqlRepository<TEntity>` already call this helper when their corresponding options-bag fields are empty, so the helper is the canonical source.

The options bags also expose a `TableName` / `OutboxTableName` setter. This dual path means two registrations of the same entity can disagree on storage location — the entity DTO says one thing, the options bag says another. The reconciliation today is "options win", which inverts the desired layering (entity metadata should be authoritative).

## Goals / Non-Goals

**Goals:**
- One source of truth for table names: the entity DTO (`[Table]` attribute or snake_case convention).
- Smaller, less error-prone options surface for the PostgreSQL plugin.
- Compile-time failure for callers that previously set `TableName` / `OutboxTableName`, so the breaking change is obvious.

**Non-Goals:**
- Changing the snake_case convention or the `[Table]` attribute semantics.
- Changing how column names are derived (`[Column]` still applies).
- Introducing a runtime override hook (e.g., per-registration delegate) — that would re-create the same drift problem.

## Decisions

**Decision: Delete the properties outright; no deprecation period.**
Rationale: this library is pre-1.0 and the two properties are trivially replaced by adding `[Table("name")]` to the entity class. A deprecation shim (`[Obsolete]` + ignore-the-value) would silently change behavior for callers who don't read warnings. A hard compile break is louder and safer.
Alternatives considered: (a) keep properties but throw if set — still adds dead surface; (b) `[Obsolete(error: true)]` — same compile break with extra noise.

**Decision: `PostgreSqlOutboxOptions` no longer carries a table name at all; `PostgreSqlOutbox<TEntity>` computes `${EntityColumnMapper.GetTableName(typeof(TEntity))}_outbox` once in its constructor and stores it in a private field.**
Rationale: the constructor already has `typeof(TEntity)`, so no extra parameter or lookup is needed. Keeping the resolved name in a private field preserves the hot-path SQL string construction.

**Decision: `RepositoryExtensions.UsePostgreSqlRepository` no longer threads any table name into `PostgreSqlOutboxOptions`.**
Rationale: the outbox computes its own name from `typeof(TEntity)`, so forwarding is dead code.

## Risks / Trade-offs

- **Risk**: Callers who relied on `TableName` for a custom schema have to add `[Table("name")]` to their entity class. → Mitigation: this is exactly the contract we want; document it in the breaking-change note and the CLAUDE.md plugin row.
- **Risk**: Existing deployments may have outbox tables with custom names that no longer match the derived name. → Mitigation: this is a code-only change; operators must either rename the table or add `[Table]` to match. Call this out in the proposal Impact section. Note: the existing schema migrator does not rename tables — a mismatch would result in a fresh table being created alongside the old one, which is loud enough to catch.
- **Trade-off**: Less flexibility at the registration site. Acceptable: registration-site flexibility was the source of the bug this change fixes.
