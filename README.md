# RayTree

A modular .NET 8 entity change-tracking library built on the **outbox pattern**. Track inserts, updates, and deletes on any entity type, persist them reliably via an outbox, and fan them out to RabbitMQ, Kafka, PostgreSQL NOTIFY, or any custom broker — with built-in serialization, compression, deduplication, and retry.

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
| `RayTree.Plugins.InMemory` | In-memory outbox, queue, and repository (tests / local dev) |
| `RayTree.Plugins.PostgreSQL` | PostgreSQL outbox + NOTIFY/LISTEN fast-path publisher. Schema is derived from entity properties; customisable via `[Table]`, `[Column]`, `[NotMapped]`, `[Required]`, `[MaxLength]`, and `[StringLength]` attributes. |
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
var consumer = new RabbitMqConsumer(consumerOptions);

var tracker = new ChangeTrackingBuilder()
    .UseSerializer<JsonSerializerPlugin>(_ => new JsonSerializerPlugin())
    .UseCompressor<NoOpCompressorPlugin>(_ => new NoOpCompressorPlugin())
    .ForEntity<Order>(e => e
        .UseOutbox(new InMemoryOutbox())
        .UseQueue(new RabbitMqPublisher(publisherOptions))
        .UseConsumer(consumer)
        .OnInsert(async (change, ct) =>
        {
            Console.WriteLine($"Order {change.EntityId} inserted");
        }))
    .Build(); // initializes and starts publisher loops

// Publish a change
await tracker.TrackInsertAsync(new Order { Id = 1, Total = 49.99m });

// Consume (blocking — run on a background task)
await tracker.ConsumeFromConsumerAsync(consumer, cancellationToken);

tracker.Dispose();
```

### .NET Generic Host / ASP.NET Core

```csharp
builder.Services.AddChangeTracking(builder.Configuration, b => b
    .UseSerializer<JsonSerializerPlugin>(_ => new JsonSerializerPlugin())
    .UseCompressor<NoOpCompressorPlugin>(_ => new NoOpCompressorPlugin())
    .ForEntity<Order>(e => e
        .UseOutbox(new PostgreSqlOutbox<Order>(connectionString))
        .UseQueue(new RabbitMqPublisher(publisherOptions))
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
    .AddInterceptors(new EntityChangeInterceptor(tracker)));
```

Changes are automatically tracked on `SaveChangesAsync` based on EF change-tracker state.

### Publisher-only builder

Use `ChangePublisherBuilder` directly when you only need to publish (no subscriber):

```csharp
var publisher = new ChangePublisherBuilder()
    .UseOutbox<InMemoryOutbox>(_ => new InMemoryOutbox())
    .UseQueue<KafkaPublisher>(_ => new KafkaPublisher(options))
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
        .UseQueue(new KafkaConsumer(consumerOptions))
        .OnChange(changeType: null, async (change, ct) => { /* handle any */ }))
    .Build();
```

## Deduplication

Register a deduplication store to suppress duplicate deliveries:

```csharp
// In-memory (default, single process)
// No configuration needed — InMemoryDeduplicationStore is used automatically.

// Redis (distributed)
builder.ForEntity<Order>(e => e.UseDeduplicationStore(new RedisDeduplicationStore(redis)));
```

## Running tests

```bash
# Unit tests (no Docker required)
dotnet test tests/RayTree.Core.Tests
dotnet test tests/RayTree.Plugins.InMemory.Tests
dotnet test tests/RayTree.EntityFrameworkCore.Tests

# Integration tests (requires Docker — Testcontainers spins up containers automatically)
dotnet test tests/RayTree.Plugins.PostgreSQL.Tests
dotnet test tests/RayTree.Plugins.RabbitMQ.Tests
dotnet test tests/RayTree.Plugins.Kafka.Tests
```
