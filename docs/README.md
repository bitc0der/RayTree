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
- **Structured Logging** - `Microsoft.Extensions.Logging` throughout; pass `ILoggerFactory` to `ChangeTrackingBuilder` or let `AddChangeTracking` wire it from DI automatically

## Quick Start

```csharp
// Optional: pass ILoggerFactory for structured log output (defaults to NullLoggerFactory)
var builder = new ChangeTrackingBuilder(loggerFactory);

builder.ForEntity<Product>(e => e
    .UseOutbox(new PostgreSqlOutbox<Product>(new PostgreSqlOutboxOptions
    {
        ConnectionString = connectionString
        // OutboxTableName defaults to "product_outbox"
    }))
    .UseQueue(new InMemoryQueue())
    .UseSerializer(new JsonSerializerPlugin())
    .UseCompressor(new GzipCompressorPlugin()));

// Build() automatically initializes database schema and starts publisher services
var tracker = builder.Build();

// Track changes manually
await tracker.TrackInsertAsync(new Product { Id = 1, Name = "Widget" });
await tracker.TrackUpdateAsync(new Product { Id = 1, Name = "Widget Pro" });
await tracker.TrackDeleteAsync(new Product { Id = 1, Name = "Widget Pro" });
```

## Global Serializer / Compressor

Set a serializer or compressor for all entity types at once using builder extension methods. Per-entity calls inside `ForEntity` override the global default:

```csharp
var builder = new ChangeTrackingBuilder();
builder.UseJsonSerializer();
builder.UseGzipCompressor();

builder.ForEntity<Product>(e => e
    .UseOutbox(new PostgreSqlOutbox<Product>(new PostgreSqlOutboxOptions
    {
        ConnectionString = connectionString
    }))
    .UseQueue(new InMemoryQueue()));
// Inherits JsonSerializer + GzipCompressor from global defaults

var tracker = builder.Build();
```

## Auto-Initialization

`Build()` and `BuildAsync()` automatically call `tracker.InitializeAsync()`, which:

- Creates outbox tables (`CREATE TABLE IF NOT EXISTS`)
- Creates source tables if a repository is registered
- Sets up PostgreSQL NOTIFY triggers if `UseNotificationChannel = true`
- Starts one `OutboxPublisherService` per registered entity type

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
    public string DisplayLabel => $"#{Id}";   // computed — not persisted
}
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
    public string DisplayLabel => $"Order #{Id}";
}
```

Generated outbox columns (alongside the fixed metadata columns):

| Property | Column | Type | Nullable |
|---|---|---|---|
| `Id` | `state_order_id` | `INTEGER` | NO |
| `CustomerEmail` | `state_customer_email` | `VARCHAR(200)` | NO |
| `Total` | `state_total` | `NUMERIC(18,4)` | NO |
| `LineItemsJson` | `state_line_items_json` | `JSONB` | YES |
| `DisplayLabel` | — | — | excluded |

## In-Memory Mode (Testing)

```csharp
var builder = new ChangeTrackingBuilder();

builder.ForEntity<Product>(e => e
    .UseOutbox(new InMemoryOutbox())
    .UseQueue(new InMemoryQueue())
    .UseSerializer(new JsonSerializerPlugin())
    .UseCompressor(new GzipCompressorPlugin()));

var tracker = builder.Build();
```

## Subscribing to Changes

`RayTree.Subscriber` receives `MessageEnvelope` messages from any `IQueueConsumer`, deserializes the entity state, and dispatches to typed handlers. Use `ChangeSubscriberBuilder` to configure global defaults and per-entity overrides. The subscriber is the mirror of the publisher — use the same serializer and compressor on both sides.

```csharp
var queue = new InMemoryQueue(); // or KafkaConsumer / RabbitMqConsumer

var subscriber = new ChangeSubscriberBuilder()
    .UseSerializer(new JsonSerializerPlugin())
    .UseCompressor(new GzipCompressorPlugin())
    .ForEntity<Product>(e => e
        .UseQueue(queue)
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

// Start consuming (blocks until cancellation)
await subscriber.ConsumeFromConsumerAsync(queue, cancellationToken);
```

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

## Cleanup

`EntityChangeTracker` implements `IDisposable`. Disposing it stops all publisher services:

```csharp
using var tracker = builder.Build();
// ... use tracker ...
// Dispose() stops publisher services automatically
```
