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

## Quick Start

```csharp
var builder = new ChangeTrackingBuilder();

builder.ForEntity<Product>()
    .UseOutbox(new PostgreSqlOutbox<Product>(new PostgreSqlOutboxOptions
    {
        ConnectionString = connectionString
        // OutboxTableName defaults to "product_outbox"
    }))
    .UseQueue(new InMemoryQueue())
    .UseSerializer(new JsonSerializerPlugin())
    .UseCompressor(new GzipCompressorPlugin());

// Build() automatically initializes database schema and starts publisher services
var tracker = builder.Build();

// Track changes manually
await tracker.TrackInsertAsync(new Product { Id = 1, Name = "Widget" });
await tracker.TrackUpdateAsync(new Product { Id = 1, Name = "Widget Pro" });
await tracker.TrackDeleteAsync(new Product { Id = 1, Name = "Widget Pro" });
```

## Global Serializer / Compressor

Set a serializer or compressor for all entity types at once using builder extension methods:

```csharp
var builder = new ChangeTrackingBuilder();
builder.UseJsonSerializer();
builder.UseGzipCompressor();

builder.ForEntity<Product>()
    .UseOutbox(new PostgreSqlOutbox<Product>(new PostgreSqlOutboxOptions
    {
        ConnectionString = connectionString
    }))
    .UseQueue(new InMemoryQueue());

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

## In-Memory Mode (Testing)

```csharp
var builder = new ChangeTrackingBuilder();
builder.ForEntity<Product>()
    .UseOutbox(new InMemoryOutbox())
    .UseQueue(new InMemoryQueue())
    .UseSerializer(new JsonSerializerPlugin())
    .UseCompressor(new GzipCompressorPlugin());

var tracker = builder.Build();
```

## Cleanup

`EntityChangeTracker` implements `IDisposable`. Disposing it stops all publisher services:

```csharp
using var tracker = builder.Build();
// ... use tracker ...
// Dispose() stops publisher services automatically
```
