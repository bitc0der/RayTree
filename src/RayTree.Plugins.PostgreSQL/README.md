# RayTree.Plugins.PostgreSQL

PostgreSQL repository and outbox plugins for entity change tracking. Uses [Npgsql](https://github.com/npgsql/npgsql) for
database access and supports PostgreSQL NOTIFY/LISTEN for low-latency change notification.

## Features

- **Outbox** -- writes entity changes to an outbox table for reliable, at-least-once delivery
- **Source Repository** -- manages source tables that store entity key data with auto-migration
- **NOTIFY/LISTEN** -- instant change notification via PostgreSQL async triggers with automatic fallback polling
- **Auto-migration** -- creates outbox tables, source tables, indexes, and schema columns on initialization
- **Entity property columns** -- stores selected entity state directly in the outbox row for immediate access

## Package

The project is in `src/RayTree.Plugins.PostgreSQL/`. Add it to your solution or reference via:

```xml
<ProjectReference Include="..\RayTree.Plugins.PostgreSQL\RayTree.Plugins.PostgreSQL.csproj" />
```

Requires the `Npgsql` package (version managed in `Directory.Packages.props`).

## Usage

### Outbox

```csharp
services.UsePostgreSqlOutbox<Order>(options =>
{
    options.ConnectionString = "Host=...;Database=...";
    options.OutboxTableName  = "orders_outbox";
});
```

Or through the change tracking builder:

```csharp
builder.UsePostgreSqlOutbox(entityType =>
{
    if (entityType == typeof(Order))
        return new PostgreSqlOutboxOptions
        {
            ConnectionString  = "Host=...",
            OutboxTableName   = "orders_outbox"
        };
    // ...
});
```

### Source Repository (key table)

```csharp
builder.UsePostgreSqlRepository<Order>(options =>
{
    options.ConnectionString = "Host=...;Database=...";
    options.TableName        = "orders_source";
});
```

This creates a source table with infrastructure columns (`id`, `created_at`, `updated_at`, `version`) plus the entity's
`[Key]`-annotated properties. Columns are automatically added on startup; type changes and orphan columns emit warnings
instead of making destructive changes.

### NOTIFY/LISTEN Publisher

Enable instant push-based change delivery:

```csharp
services.UsePostgreSqlOutbox<Order>(options =>
{
    options.ConnectionString = "Host=...;Database=...";
})
.UseNotificationChannel("order_changes");
```

This generates a `pg_notify` trigger on the outbox table. At startup, the `NotificationBasedPublisher` connects via
`LISTEN` and publishes changes as they arrive. A fallback polling loop drains any records missed while the listener was
unhealthy, and also processes pre-existing unpublished records on first start.

Configure concurrency and fallback interval:

```csharp
services.UsePostgreSqlOutbox<Order>(options => ...)
    .UseNotificationChannel("order_changes")
    .WithFallbackPolling(TimeSpan.FromSeconds(15));
```

The `NotificationBasedPublisher` can be started/stopped via `StartAsync` / `StopAsync`. It integrates with
`EntityChangeTracker.Publisher` for serialization, compression, and queue publishing.

## Connection recovery

The plugin observes two distinct Postgres connection surfaces, with different recovery shapes:

**`postgres.notification` — LISTEN connection** (`NotificationBasedPublisher.ListenLoopAsync`)

A single long-lived `NpgsqlConnection`. When `WaitAsync` throws an exception classified by the
internal `PostgresFault` helper (transient `NpgsqlException`, `SocketException` / `IOException`
inner, `08xxx` / `57P0x` SQL state, or `ObjectDisposedException`), the loop:

1. Emits `raytree.connection.disconnects{component="postgres.notification", endpoint={ChannelName}}` once per fault cycle, logs `Warning`.
2. Runs an inline exponential-backoff loop bounded by `NotificationBasedPublisherOptions.ConnectionRecovery` — disposes the broken connection, opens a fresh one, re-attaches the `Notification` handler, re-issues `LISTEN`.
3. On success emits `raytree.connection.recoveries{outcome="succeeded"}` + duration, flips `_listenerHealthy = true` on the next `WaitAsync` wake.
4. On `MaxAttempts` exhaustion emits `raytree.connection.recoveries{outcome="exhausted"}` and exits the LISTEN loop (fallback polling continues).

The fallback polling loop continues running throughout, providing best-effort delivery during the
reconnect window. `TryClaimForPublishingAsync` prevents double-publish races between the two paths.

**`postgres.outbox` — pooled outbox connections** (`OutboxPublisherService.ProcessBatchAsync`, `NotificationBasedPublisher.FallbackPollingLoopAsync`)

Short-lived per-call `NpgsqlConnection` from the pool. **No reconnect code** — Npgsql's pool
handles transient TCP errors, and the polling cadence is the retry. Instead, the consumers
classify each batch failure via `IOutbox.IsConnectionFault` (which `PostgreSqlOutbox` overrides
to delegate to `PostgresFault`) and:

- Emit `raytree.connection.disconnects{component="postgres.outbox"}` once per transition.
- Demote the per-batch `Error` log to `Warning` so a transient Postgres blip looks like one fault cycle.
- On the first subsequent successful batch, emit `raytree.connection.recoveries{outcome="succeeded"}` + duration.

Non-connection-fault exceptions (e.g. unique-violation `23505`, syntax errors) bypass this path
entirely — they still log at `Error` and surface to operators as application bugs.

**Write paths (`WriteAsync`, `PostgreSqlRepository`)** are unchanged: exceptions propagate to the
caller. Outbox writes typically run inside the caller's EF Core transaction; the library cannot
retry just the outbox row without breaking transaction atomicity. The caller's unit-of-work owns
write-side retry.

## Options

| Option | Default | Description |
|---|---|---|
| `PostgreSqlOutboxOptions.ConnectionString` | `""` | PostgreSQL connection string |
| `PostgreSqlOutboxOptions.OutboxTableName` | `"{entity}_outbox"` | Outbox table name |
| `PostgreSqlOutboxOptions.UseNotificationChannel` | `false` | Enable NOTIFY trigger |
| `PostgreSqlOutboxOptions.NotificationChannel` | `null` | Channel name for NOTIFY |
| `PostgreSqlOutboxOptions.FallbackPollingInterval` | `null` | Polling interval when LISTEN is down |
| `NotificationBasedPublisherOptions.ConnectionRecovery` | `new()` (Enabled, 1s→30s, ×2, ±20% jitter, unlimited) | Tunes LISTEN reconnect backoff. Set `Enabled = false` to skip reconnect and rely on fallback polling only. |
| `PostgreSqlOutboxOptions.CleanupBatchSize` | `1000` | Rows per cleanup batch |
| `PostgreSqlRepositoryOptions.ConnectionString` | `""` | PostgreSQL connection string |
| `PostgreSqlRepositoryOptions.TableName` | `"{entity}_source"` | Source table name |

## Outbox Table Schema

| Column | Type | Notes |
|---|---|---|
| `id` | `BIGSERIAL` | Primary key |
| `entity_id` | `TEXT` | Logical entity identifier |
| `change_type` | `VARCHAR(10)` | Created / Updated / Deleted |
| `timestamp` | `TIMESTAMPTZ` | Default: `NOW()` |
| `published` | `BOOLEAN` | Default: `FALSE` |
| `version` | `INTEGER` | Entity version |
| `correlation_id` | `UUID` | Default: `gen_random_uuid()` |
| `entity_type` | `TEXT` | Fully-qualified type name |
| `state_*` | *varies* | One column per entity property |

Indexes are created for unpublished changes, cleanup, and entity-type queries.

## Testing

Integration tests require Docker:

```
dotnet test tests/RayTree.Plugins.PostgreSQL.Tests
```

## Project Structure

```
Outbox/
  PostgreSqlOutbox.cs           — IOutbox implementation
  PostgreSqlOutboxOptions.cs    — Outbox configuration
  EntityColumnMapper.cs         — Entity-to-column mapping
  Schema/
    OutboxTableSchema.cs        — Outbox table model
    OutboxSchemaGenerator.cs    — DDL generation
    OutboxColumn.cs / OutboxIndex.cs / EntityPropertyColumn.cs
  Notification/
    NotificationBasedPublisher.cs         — LISTEN/NOTIFY publisher
    NotificationBasedPublisherOptions.cs  — Publisher configuration
    NotificationPayload.cs                — Trigger payload DTO

Repository/
  PostgreSqlRepository.cs        — IRepository implementation
  PostgreSqlRepositoryOptions.cs — Repository configuration
  Schema/
    SourceTableDdlGenerator.cs   — Source table DDL generation
    SourceTableSchema.cs / SourceTableColumn.cs / SourceTableIndex.cs

Schema/
  SchemaInspector.cs             — information_schema introspection
  SchemaMigrator.cs              — Column diff & migration
  IndexMigrator.cs               — Index diff & migration
  PostgreSqlTypeNormalizer.cs    — PostgreSQL type normalization

Extensions/
  BuilderExtensions.cs           — IServiceCollection / IChangeTrackingBuilder extensions
  RepositoryExtensions.cs        — Convenience extension for outbox + repository setup
```
