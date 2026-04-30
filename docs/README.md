# RayTree - Entity Change Tracking System

A modular .NET 8.0 entity change tracking system with outbox pattern support, queue distribution, per-entity plugin configuration, and `System.IO.Pipelines` for zero-allocation serialization/compression.

## Features

- **EF Core Integration** - Automatic change detection via `ISaveChangesInterceptor`
- **Outbox Pattern** - Atomic writes within EF Core transactions, reliable distribution
- **Dual Distribution** - PostgreSQL `NOTIFY/LISTEN` (low-latency) with fallback polling
- **Per-Entity Plugins** - Override repository, outbox, queue, serializer, and compressor per entity type
- **Zero-Allocation Pipelines** - `System.IO.Pipelines` for serialization and compression
- **Modular Plugins** - Each serializer and compressor in its own NuGet package
- **In-Memory Testing** - Full in-memory implementation for development and testing
- **Subscriber Framework** - Deduplication, error handling, dead-letter support

## Quick Start

```csharp
var builder = WebApplication.CreateBuilder(args);

// 1. Add EF Core
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

// 2. Add change tracking with plugins
builder.Services.AddChangeTracking(tracking =>
{
    // Register entity types
    tracking.ForEntity<Product>()
        .UsePostgreSqlOutbox(builder.Configuration.GetConnectionString("Default"), "products")
        .UseRabbitMqQueue(builder.Configuration.GetConnectionString("RabbitMq"), "products", "product_exchange");

    tracking.ForEntity<Order>()
        .UsePostgreSqlOutbox(builder.Configuration.GetConnectionString("Default"), "orders")
        .UseKafkaQueue(builder.Configuration.GetConnectionString("Kafka"), "orders");

    // Global defaults
    tracking.UseJsonSerializer();
    tracking.UseGzipCompressor();
});

// 3. Add subscriber (optional - for consuming changes)
builder.Services.AddChangeSubscriber(subscriber =>
{
    subscriber.ForEntity<Product>()
        .FromRabbitMq("product_exchange", "product_queue")
        .UseJsonSerializer()
        .UseGzipCompressor()
        .OnInsert(p => Console.WriteLine($"New product: {p.Name}"))
        .OnUpdate(p => Console.WriteLine($"Updated product: {p.Name}"));
});

var app = builder.Build();

// 4. Initialize database (creates source + outbox tables, triggers)
await using var scope = app.Services.CreateAsyncScope();
await scope.ServiceProvider.InitializeRayTreeDatabaseAsync(options =>
{
    options.UseAttributeBasedSchema = true; // Reads [Table], [Column] attributes
});

app.Run();
```

## Standalone Mode (no DI)

```csharp
var config = new ChangeTrackingConfiguration()
    .UseInMemoryOutbox()
    .UseInMemoryQueue()
    .UseJsonSerializer()
    .UseNoOpCompressor();

var tracker = config.Build();

// Track changes manually
await tracker.TrackChangesAsync(new[]
{
    new EntityChange
    {
        EntityType = typeof(Product).AssemblyQualifiedName!,
        EntityId = "1",
        ChangeType = ChangeType.Insert,
        Timestamp = DateTime.UtcNow
    }
});
```
