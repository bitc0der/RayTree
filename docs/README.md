# RayTree - Entity Change Tracking System

A modular .NET 8.0 entity change tracking system with outbox pattern support, queue distribution, per-entity plugin configuration, and `System.IO.Pipelines` for zero-allocation serialization/compression.

## Features

- **Outbox Pattern** - Reliable change distribution with at-least-once delivery guarantees
- **Dual Distribution** - PostgreSQL `NOTIFY/LISTEN` (low-latency) with automatic fallback polling
- **Per-Entity Plugins** - Override outbox, queue, serializer, and compressor per entity type
- **Zero-Allocation Pipelines** - `System.IO.Pipelines` for serialization and compression
- **Modular Plugins** - Each serializer and compressor in its own package
- **In-Memory Testing** - Full in-memory implementation for development and testing
- **Auto-Initialization** - Automatic database schema initialization on `Build()` / `BuildAsync()`
- **Structured Logging** - `Microsoft.Extensions.Logging` throughout; pass `ILoggerFactory` to `EntityChangeTracker.Create()` or let `AddChangeTracking` wire it from DI automatically
- **OpenTelemetry Metrics** - `System.Diagnostics.Metrics` instruments on a `"RayTree"` meter for outbox writes, publish/subscribe latency, payload size, queue depth, and retry shape. Zero OTel SDK dependency unless the optional `RayTree.OpenTelemetry` package is referenced. See [OpenTelemetry Metrics Guide](opentelemetry-metrics.md).

## Quick Start

```csharp
// Optional: pass ILoggerFactory for structured log output (defaults to NullLoggerFactory)
var builder = EntityChangeTracker.Create(loggerFactory);

builder.ForEntity<Product>(e => e
    .UsePostgreSqlOutbox(new PostgreSqlOutboxOptions
    {
        ConnectionString = connectionString
        // OutboxTableName defaults to "product_outbox"
    })
    .UsePublisher(new InMemoryQueue())
    .UseSerializer(new JsonSerializerPlugin())
    .UseCompressor(new GzipCompressorPlugin()));

// Build() automatically initializes database schema (creates or migrates) and starts publisher services
var tracker = builder.Build();

// Track changes manually
await tracker.TrackInsertAsync(new Product { Id = 1, Name = "Widget" });
await tracker.TrackUpdateAsync(new Product { Id = 1, Name = "Widget Pro" });
await tracker.TrackDeleteAsync(new Product { Id = 1, Name = "Widget Pro" });
```

## Global Serializer / Compressor

Set a serializer or compressor for all entity types at once using builder extension methods. Per-entity calls inside `ForEntity` override the global default:

```csharp
var builder = EntityChangeTracker.Create();
builder.UseJsonSerializer();
builder.UseGzipCompressor();

builder.ForEntity<Product>(e => e
    .UsePostgreSqlOutbox(new PostgreSqlOutboxOptions
    {
        ConnectionString = connectionString
    })
    .UsePublisher(new InMemoryQueue()));
// Inherits JsonSerializer + GzipCompressor from global defaults

var tracker = builder.Build();
```

## Auto-Initialization and Schema Migration

`Build()` and `BuildAsync()` automatically initialize the tracker, which:

- **Creates** outbox and source tables if they do not exist (`CREATE TABLE IF NOT EXISTS` with all columns and indexes in one statement)
- **Migrates** existing tables on every startup — adds missing `state_*` columns, syncs indexes (creates new, drops and recreates changed definitions), and logs `Warning` for orphan columns/indexes and type mismatches
- Sets up PostgreSQL NOTIFY triggers if `UseNotificationChannel = true`
- Starts one `OutboxPublisherService` per registered entity type

No manual migration step is needed for initial setup or when adding new entity properties — RayTree handles both automatically. See [Database Migration Guide](database-migration.md) for the full schema evolution reference.

```csharp
// Sync (blocks until initialized)
var tracker = builder.Build();

// Async
var tracker = await builder.BuildAsync();
```

## PostgreSQL Schema Customization

`RayTree.Plugins.PostgreSQL` derives the outbox table schema automatically from the entity's public properties. The defaults can be overridden using standard `System.ComponentModel.DataAnnotations` and `System.ComponentModel.DataAnnotations.Schema` attributes — no extra dependencies required.

### Table name — `[Table]`

By default the outbox table is named `<snake_case_entity>_outbox` and the source table `<snake_case_entity>`. Decorate the entity class with `[Table]` to change the base name:

```csharp
[Table("orders")]
public class Order
{
    public int Id { get; set; }
    public decimal Total { get; set; }
}

// Outbox table: "orders_outbox"  (was "order_outbox" without the attribute)
// Source table: "orders"         (was "order"        without the attribute)
```

The explicit `OutboxTableName` / `TableName` options still take precedence if set.

### Column name — `[Column]`

Each entity property maps to an outbox column named `state_<snake_case>`. Use `[Column]` to control the suffix:

```csharp
public class Order
{
    [Column("order_id")]
    public int Id { get; set; }         // → column "state_order_id"

    public decimal Total { get; set; }  // → column "state_total" (default)
}
```

The `state_` prefix is always kept to prevent collisions with the fixed outbox metadata columns (`id`, `entity_id`, `change_type`, `timestamp`, `published`, `version`, `correlation_id`, `entity_type`).

### PostgreSQL type — `[Column(TypeName = "...")]`

Override the auto-mapped PostgreSQL type with an exact type string:

```csharp
public class Order
{
    [Column(TypeName = "JSONB")]
    public string? Metadata { get; set; }   // → JSONB instead of TEXT

    [Column(TypeName = "NUMERIC(18,4)")]
    public decimal Total { get; set; }      // → NUMERIC(18,4) instead of NUMERIC
}
```

### Variable-length strings — `[MaxLength]` / `[StringLength]`

By default `string` properties map to `TEXT`. Add a length constraint to emit `VARCHAR(n)`:

```csharp
public class Order
{
    [MaxLength(100)]
    public string? Reference { get; set; }      // → VARCHAR(100)

    [StringLength(50)]
    public string? StatusCode { get; set; }     // → VARCHAR(50)
}
```

### Not-null constraint — `[Required]`

Reference-type properties are nullable by default. Mark them `[Required]` to emit `NOT NULL`:

```csharp
public class Order
{
    [Required]
    public string CustomerEmail { get; set; } = string.Empty;   // → TEXT NOT NULL
}
```

### Excluding properties — `[NotMapped]`

Properties decorated with `[NotMapped]` are excluded from the outbox schema entirely:

```csharp
public class Order
{
    public int Id { get; set; }

    [NotMapped]
    public string? CachedSummary { get; set; }   // populated in-process, not persisted
}
```

### Primary key — `[Key]`

Mark the business primary key with `[Key]`. `PostgreSqlRepository` uses it to build `WHERE` clauses for `UpdateAsync`, `DeleteAsync`, and `GetByIdAsync`, and adds a `UNIQUE` index on the corresponding column(s) in the source table. Falls back to a property named `Id` when no `[Key]` annotation is present; throws at construction time if neither exists.

```csharp
public class Order
{
    [Key]
    public int OrderId { get; set; }   // WHERE state_order_id = @K0

    public decimal Total { get; set; }
}
```

`GetByIdAsync` takes `object[]` — one element per key, in the same order as declared:

```csharp
var order = await repo.GetByIdAsync([42]);
```

#### Composite primary keys

Apply `[Key]` to multiple properties and use `[Column(Order = n)]` to control the column order:

```csharp
public class OrderLine
{
    [Key, Column(Order = 0)]
    public int OrderId { get; set; }

    [Key, Column(Order = 1)]
    public int LineNumber { get; set; }

    public string? Product { get; set; }
}
```

Generated source table gets a `UNIQUE (state_order_id, state_line_number)` index. `GetByIdAsync` receives both values:

```csharp
var line = await repo.GetByIdAsync([orderId, lineNumber]);
```

### Complete example

```csharp
[Table("orders")]
public class Order
{
    [Column("order_id")]
    public int Id { get; set; }

    [Required]
    [MaxLength(200)]
    public string CustomerEmail { get; set; } = string.Empty;

    [Column(TypeName = "NUMERIC(18,4)")]
    public decimal Total { get; set; }

    [Column(TypeName = "JSONB")]
    public string? LineItemsJson { get; set; }

    [NotMapped]
    public string? CachedSummary { get; set; }
}
```

Generated outbox columns (alongside the fixed metadata columns):

| Property | Column | Type | Nullable |
|---|---|---|---|
| `Id` | `state_order_id` | `INTEGER` | NO |
| `CustomerEmail` | `state_customer_email` | `VARCHAR(200)` | NO |
| `Total` | `state_total` | `NUMERIC(18,4)` | NO |
| `LineItemsJson` | `state_line_items_json` | `JSONB` | YES |
| `CachedSummary` | — | — | excluded |

## In-Memory Mode (Testing)

```csharp
var builder = EntityChangeTracker.Create();

builder.ForEntity<Product>(e => e
    .UseOutbox(new InMemoryOutbox())
    .UsePublisher(new InMemoryQueue())
    .UseSerializer(new JsonSerializerPlugin())
    .UseCompressor(new GzipCompressorPlugin()));

var tracker = builder.Build();
```

## Subscribing to Changes

`RayTree` receives `MessageEnvelope` messages from any `IQueueConsumer`, deserializes the entity state, and dispatches to typed handlers. Configure the subscriber alongside the publisher via the unified `ChangeTrackingBuilder`. Use the same serializer and compressor on both sides.

```csharp
var queue = new InMemoryQueue(); // or KafkaConsumer / RabbitMqConsumer

var tracker = EntityChangeTracker.Create()
    .UseSerializer<JsonSerializerPlugin>(_ => new JsonSerializerPlugin())
    .UseCompressor<GzipCompressorPlugin>(_ => new GzipCompressorPlugin())
    .ForEntity<Product>(e => e
        .UseOutbox(new InMemoryOutbox())
        .UsePublisher(queue)
        .UseConsumer(queue)
        .OnInsert(async (change, ct) =>
        {
            var product = change.State;   // fully-typed Product
            Console.WriteLine($"Inserted: {product?.Name}");
        })
        .OnUpdate(async (change, ct) =>
            Console.WriteLine($"Updated: {change.EntityId}"))
        .OnDelete(async (change, ct) =>
            Console.WriteLine($"Deleted: {change.EntityId}")))
    .Build();

using var cts = new CancellationTokenSource();
await tracker.StartAsync(cts.Token); // starts consumer loops
```

> **Tip:** In a .NET Generic Host app, `ChangeTrackingHostedService` calls `tracker.StartAsync` / `tracker.StopAsync` automatically. Use `AddChangeTracking` and the hosted service manages the full lifecycle.

### ASP.NET Core (DI)

`AddChangeSubscriber` registers `ChangeSubscriber` as a singleton and starts `ChangeSubscriberHostedService` automatically. It returns `IChangeSubscriberBuilder`, so entity registrations chain directly off the call:

```csharp
builder.Services
    .AddChangeSubscriber(builder.Configuration)
    .UseRedisDeduplication("localhost:6379")
    .ForEntity<Product>(e => e
        .UseInMemoryQueue(productQueue)
        .UseSerializer(new JsonSerializerPlugin())
        .UseCompressor(new GzipCompressorPlugin())
        .OnInsert(async (change, ct) =>
            Console.WriteLine($"New product: {change.State?.Name}")));
```

`appsettings.json`:
```json
{
  "ChangeTracking": {
    "Subscriber": {
      "MaxRetries": 3,
      "RetryDelay": "00:00:01",
      "SkipOnFailure": false
    }
  }
}
```

## Outbox rotation

The outbox table grows as changes are written and published. Rotation automatically deletes old rows so the table stays bounded. It runs as part of the normal publisher poll loop — no extra hosted service or scheduler is needed.

### How it works

After every poll batch, `OutboxPublisherService` checks whether the configured interval has elapsed and, if so, deletes:

1. **Published rows** older than `CleanupRetentionPeriod` — rows that have already been sent to the broker and are safe to remove.
2. **Stale unpublished rows** older than `StaleUnpublishedThreshold` *(opt-in)* — rows that have been sitting in the outbox without being published, which usually indicates a stuck or dead queue.

Rotation fires **eagerly on the first tick** (so stale rows from before a restart are cleaned up immediately), then respects `CleanupInterval` for subsequent runs.

Cleanup errors are isolated: a transient database failure logs an error but does not abort the publish loop or stop the service. When either operation fails, the interval timer is not advanced, so the next poll tick retries immediately rather than waiting a full `CleanupInterval`.

### Configuration — `OutboxPublisherOptions`

Publisher options are global (not per entity) and are set on the top-level builder via `UsePublisherOptions`. When using the Generic Host they can alternatively be bound from `appsettings.json` via `AddChangeTracking` (see below).

```csharp
var builder = EntityChangeTracker.Create(loggerFactory);

// Rotation options are set at the builder level, not inside ForEntity.
builder.UsePublisherOptions(o =>
{
    // How old a published row must be before rotation removes it.
    o.CleanupRetentionPeriod = TimeSpan.FromDays(7);   // default: 7 days

    // How frequently rotation runs (first tick is always immediate).
    o.CleanupInterval = TimeSpan.FromHours(1);          // default: 1 h

    // Optional: remove unpublished rows older than this threshold.
    // Logs a Warning when any are found — treat this as an operator alert.
    // Disabled (null) by default.
    o.StaleUnpublishedThreshold = TimeSpan.FromDays(30);
});

builder.ForEntity<Order>(e => e
    .UsePostgreSqlOutbox(new PostgreSqlOutboxOptions
    {
        ConnectionString = connectionString
    })
    .UsePublisher(rabbitPublisher)
    .UseSerializer(new JsonSerializerPlugin())
    .UseCompressor(new GzipCompressorPlugin()));

var tracker = builder.Build();
```

| Option | Type | Default | Description |
|---|---|---|---|
| `CleanupRetentionPeriod` | `TimeSpan` | 7 days | Minimum age of a **published** row before it is deleted. |
| `CleanupInterval` | `TimeSpan` | 1 hour | How often the rotation check runs. First run is always immediate on startup. |
| `StaleUnpublishedThreshold` | `TimeSpan?` | `null` (disabled) | When set, **unpublished** rows older than this age are also removed. A `Warning` log is emitted whenever rows are deleted — use it as an alert for queue health issues. |

### `appsettings.json` (Generic Host)

```json
{
  "ChangeTracking": {
    "Publisher": {
      "CleanupRetentionPeriod": "7.00:00:00",
      "CleanupInterval": "01:00:00",
      "StaleUnpublishedThreshold": "30.00:00:00"
    }
  }
}
```

### PostgreSQL batch size — `PostgreSqlOutboxOptions.CleanupBatchSize`

The PostgreSQL outbox deletes in batches to avoid large single-statement locks and WAL spikes. Each rotation cycle issues repeated `DELETE … WHERE id IN (SELECT id … LIMIT @BatchSize)` statements until no rows remain.

```csharp
new PostgreSqlOutboxOptions
{
    ConnectionString = connectionString,
    CleanupBatchSize = 1000   // default: 1000 rows per DELETE statement
}
```

Reduce this value if you see lock contention or WAL pressure during cleanup on large, busy tables.

### Log messages

| Level | Event |
|---|---|
| `Debug` | Rotation starting (every cycle) |
| `Information` | Published rows deleted (count > 0) |
| `Debug` | No published rows to remove |
| `Warning` | Stale unpublished rows deleted — indicates a queue health problem |
| `Debug` | No stale unpublished rows found |
| `Error` | Rotation failed (isolated — publish loop continues) |

### Manual rotation

Call `tracker.RunCleanupAsync(retentionPeriod, ct)` directly for ad-hoc or scheduled cleanup outside the normal poll cycle (e.g. a maintenance endpoint or a Hangfire job):

```csharp
public class MaintenanceController(
    EntityChangeTracker tracker,
    IOptions<OutboxPublisherOptions> options) : ControllerBase
{
    [HttpPost("outbox/rotate")]
    public async Task<IActionResult> Rotate(CancellationToken ct)
    {
        var deleted = await tracker.RunCleanupAsync(options.Value.CleanupRetentionPeriod, ct);
        return Ok(new { deleted });
    }
}
```

`RunCleanupAsync` calls `CleanupPublishedAsync` on every registered outbox and returns the total number of rows deleted.

## Cleanup

`EntityChangeTracker` implements `IDisposable`. Disposing it stops all publisher services:

```csharp
using var tracker = builder.Build();
// ... use tracker ...
// Dispose() stops publisher services automatically
```
