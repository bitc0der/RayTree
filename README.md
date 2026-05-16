# RayTree

A modular .NET 10 entity change-tracking library built on the **outbox pattern**. Track inserts, updates, and deletes on any entity type, persist them reliably via an outbox, and fan them out to RabbitMQ, Kafka, PostgreSQL NOTIFY, or any custom broker — with built-in serialization, compression, deduplication, and retry.

## Pipeline

```
EntityChangeTracker.TrackInsertAsync / TrackUpdateAsync / TrackDeleteAsync
  └─ IOutbox          (persist change before publishing)
  └─ OutboxPublisherService  (background poll: serialize → compress → publish)
       └─ IQueuePublisher    (broker-specific: RabbitMQ, Kafka, …)
            ↓ MessageEnvelope (headers + compressed byte[] payload)
       └─ IQueueConsumer
  └─ ChangeSubscriber (dedup → decompress → deserialize → dispatch handlers)
       └─ ChangeHandlerAsync<TEntity>(EntityChange<TEntity>, CancellationToken)
```

## Packages

| Package | Purpose |
|---|---|
| `RayTree.Core` | Core abstractions, `EntityChangeTracker`, fluent builders |
| `RayTree.Hosting` | `AddChangeTracking` for .NET Generic Host / ASP.NET Core |
| `RayTree.EntityFrameworkCore` | `EntityChangeInterceptor` — auto-track EF Core `SaveChanges` |
| `RayTree.OpenTelemetry` | OTel SDK wiring (`AddRayTreeMetrics`, `RayTreeInstrumentation` constants) |
| `RayTree.Plugins.InMemory` | In-memory outbox, queue, and repository (tests / local dev) |
| `RayTree.Plugins.PostgreSQL` | PostgreSQL outbox + NOTIFY/LISTEN fast-path publisher. Schema is derived from entity properties and managed automatically — tables are created on first run and migrated on subsequent runs (new columns added, index definitions kept in sync). Customisable via `[Table]`, `[Column]`, `[NotMapped]`, `[Required]`, `[MaxLength]`, and `[StringLength]` attributes. See [Database Migration Guide](docs/database-migration.md). |
| `RayTree.Plugins.RabbitMQ` | RabbitMQ publisher and consumer |
| `RayTree.Plugins.Kafka` | Kafka publisher and consumer |
| `RayTree.Plugins.Serializers.Json` | JSON serializer |
| `RayTree.Plugins.Serializers.MessagePack` | MessagePack serializer |
| `RayTree.Plugins.Serializers.Protobuf` | Protobuf serializer |
| `RayTree.Plugins.Compressors.Gzip` | Gzip compressor |
| `RayTree.Plugins.Compressors.Brotli` | Brotli compressor |
| `RayTree.Plugins.Compressors.Lz4` | LZ4 compressor |

## Quick start

### Standalone (no DI)

```csharp
using RayTree.Core.Tracking;
using RayTree.Plugins.InMemory;
using RayTree.Plugins.RabbitMQ;
using RayTree.Plugins.Serializers.Json;

var tracker = await new ChangeTrackingBuilder()
    .UseSerializer<JsonSerializerPlugin>(_ => new JsonSerializerPlugin())
    .UseCompressor<NoOpCompressorPlugin>(_ => new NoOpCompressorPlugin())
    .ForEntity<Order>(e => e
        .UseOutbox(new InMemoryOutbox())
        .UsePublisher(new RabbitMqPublisher(publisherOptions))
        .UseConsumer(new RabbitMqConsumer(consumerOptions))
        .OnInsert(async (change, ct) =>
        {
            Console.WriteLine($"Order {change.EntityId} inserted");
        }))
    .BuildAsync(); // initializes and starts publisher loops

// Publish a change
await tracker.TrackInsertAsync(new Order { Id = 1, Total = 49.99m });

// Consume (blocking — run on a background task)
await tracker.ConsumeFromConsumerAsync(consumer, cancellationToken);

tracker.Dispose();
```

### .NET Generic Host / ASP.NET Core

```csharp
using RayTree.Core.Tracking;
using RayTree.Hosting;
using RayTree.Plugins.PostgreSQL.Outbox;
using RayTree.Plugins.PostgreSQL.Extensions;
using RayTree.Plugins.RabbitMQ;
using RayTree.Plugins.Serializers.Json;

builder.Services.AddChangeTracking(builder.Configuration, b => b
    .UseSerializer<JsonSerializerPlugin>(_ => new JsonSerializerPlugin())
    .UseCompressor<NoOpCompressorPlugin>(_ => new NoOpCompressorPlugin())
    .UsePostgreSqlOutbox(entityType => new PostgreSqlOutboxOptions
    {
        ConnectionString = connectionString,
        OutboxTableName = $"{entityType.Name.ToLower()}_outbox"
    })
    .ForEntity<Order>(e => e
        .UsePublisher(new RabbitMqPublisher(publisherOptions))
        .UseConsumer(new RabbitMqConsumer(consumerOptions))
        .OnInsert(async (change, ct) => { /* handle insert */ })
        .OnUpdate(async (change, ct) => { /* handle update */ })
        .OnDelete(async (change, ct) => { /* handle delete */ })));
```

`AddChangeTracking` registers `EntityChangeTracker` as a singleton and `ChangeTrackingHostedService` as a hosted service. Publisher loops start during `Build()`; consumer loops start when the host starts.

Optional `appsettings.json` overrides:

```json
{
  "ChangeTracking": {
    "Publisher": { "PollingInterval": "00:00:01" },
    "Subscriber": { "MaxRetries": 3, "RetryDelay": "00:00:05", "SkipOnFailure": true }
  }
}
```

### EF Core interceptor

```csharp
services.AddDbContext<AppDbContext>(options => options
    .UseNpgsql(connectionString)
    .AddInterceptors(new EntityChangeInterceptor(tracker, new[] { typeof(Order), typeof(Customer) })));
```

Changes are automatically tracked on `SaveChangesAsync` based on EF change-tracker state. Pass the entity types you want the interceptor to observe.

### Publisher-only builder

Use `ChangePublisherBuilder` directly when you only need to publish (no subscriber):

```csharp
var publisher = new ChangePublisherBuilder()
    .UseOutbox<InMemoryOutbox>(_ => new InMemoryOutbox())
    .UsePublisher<KafkaPublisher>(_ => new KafkaPublisher(options))
    .UseSerializer<JsonSerializerPlugin>(_ => new JsonSerializerPlugin())
    .UseCompressor<NoOpCompressorPlugin>(_ => new NoOpCompressorPlugin())
    .ForEntity<Order>(e => e.UseOutbox(new InMemoryOutbox()))
    .Build();
```

### Subscriber-only builder

Use `ChangeSubscriberBuilder` directly when you only need to consume:

```csharp
var subscriber = new ChangeSubscriberBuilder()
    .UseSerializer(new JsonSerializerPlugin())
    .UseCompressor(new NoOpCompressorPlugin())
    .ForEntity<Order>(e => e
        .UseConsumer(new KafkaConsumer(consumerOptions))
        .OnChange(changeType: null, async (change, ct) => { /* handle any */ }))
    .Build();
```

## Deduplication

Register a deduplication store to suppress duplicate deliveries:

```csharp
// In-memory (default, single process)
// No configuration needed — InMemoryDeduplicationStore is used automatically.

// Custom store
builder.ForEntity<Order>(e => e.UseSubscriberOptions(opts => opts.MaxRetries = 3));
// Then register your custom IDeduplicationStore implementation via UseDeduplicationStore()
```

## Running tests

```bash
# Unit tests (no Docker required)
dotnet test tests/RayTree.Core.Tests
dotnet test tests/RayTree.Plugins.InMemory.Tests
dotnet test tests/RayTree.EntityFrameworkCore.Tests
dotnet test tests/RayTree.OpenTelemetry.Tests
dotnet test tests/RayTree.Plugins.Serializers.Json.Tests
dotnet test tests/RayTree.Plugins.Serializers.MessagePack.Tests
dotnet test tests/RayTree.Plugins.Serializers.Protobuf.Tests
dotnet test tests/RayTree.Plugins.Compressors.Gzip.Tests
dotnet test tests/RayTree.Plugins.Compressors.Brotli.Tests
dotnet test tests/RayTree.Plugins.Compressors.Lz4.Tests

# Integration tests (requires Docker — Testcontainers spins up containers automatically)
dotnet test tests/RayTree.Plugins.PostgreSQL.Tests
dotnet test tests/RayTree.Plugins.RabbitMQ.Tests
dotnet test tests/RayTree.Plugins.Kafka.Tests
```
