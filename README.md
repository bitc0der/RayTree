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
| `RayTree.Plugins.Deduplication.Redis` | Redis-backed `IDeduplicationStore` for distributed deployments |

## Quick start

### Standalone (no DI)

```csharp
using RayTree.Core.Tracking;
using RayTree.Plugins.InMemory;
using RayTree.Plugins.RabbitMQ;
using RayTree.Plugins.Serializers.Json;

var tracker = await EntityChangeTracker.Create()
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

### Isolated handler mode

Give each named handler its own broker subscription, retry budget, and deduplication namespace. The consumer factory is called once per unique name at `Build()` time.

```csharp
// For testing/local dev — InMemoryBroadcastQueue fans out to every subscriber
var broadcast = new InMemoryBroadcastQueue();

var tracker = EntityChangeTracker.Create()
    .ForEntity<Order>(e => e
        .UseOutbox(new InMemoryOutbox())
        .UsePublisher(broadcast)
        .UseSerializer(new JsonSerializerPlugin())
        .UseCompressor(new NoOpCompressorPlugin())
        .UseConsumerFactory(_ => broadcast.Subscribe())     // one consumer per name
        .OnInsert("read-model", async (change, ct) =>
        {
            // dedup key: "{correlationId}:read-model"
            await UpdateReadModelAsync(change.State!);
        })
        .OnInsert("notifier", async (change, ct) =>
        {
            // dedup key: "{correlationId}:notifier" — independent of read-model
            await SendNotificationAsync(change.State!);
        }, options: new SubscriberOptions { MaxRetries = 1, SkipOnFailure = true }))
    .Build();
```

`ChangeTrackingHostedService` starts one consume loop per `(entity type, handler name)` pair automatically. Renaming a handler is equivalent to creating a new broker subscription (the old name's offset/messages are preserved under the original name).

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
        .OnChange(ChangeType.Insert, async (change, ct) => { /* handle insert */ }))
    .Build();
```

## Delivery guarantees

By default, `RabbitMqConsumer` and `KafkaConsumer` acknowledge each message at delivery time — before the handler runs. This is **at-most-once**: lowest latency, but a process crash between delivery and handler completion loses the message (broker has already removed it / committed past it).

Opt in to **at-least-once** per consumer:

```csharp
// RabbitMQ — defer BasicAck until handler succeeds; NACK requeues on retry exhaustion
new RabbitMqConsumer(new RabbitMqConsumerOptions
{
    QueueName       = "orders",
    AckAfterHandler = true,
});

// Kafka — defer offset commit; NACK seeks back so the message is redelivered
// in the same consumer process (not just on restart)
new KafkaConsumer(new KafkaConsumerOptions
{
    Topic           = "orders",
    GroupId         = "order-processor",
    AckAfterHandler = true,
}, loggerFactory);
```

| Scenario | At-most-once (default) | At-least-once (opt-in) |
|---|---|---|
| Crash between delivery and handler completion | Message lost | Message redelivered |
| Handler exhausts retries with `SkipOnFailure = false` | ACKed (gone) | NACKed → requeued / seek-back |
| Latency | Lowest | +1 broker round-trip after handler |
| Duplicate delivery | Possible (rare) | Possible (expected — combine with `IDeduplicationStore`) |

**Kafka caveat:** when `AckAfterHandler = true`, set `SubscriberOptions.MaxDegreeOfParallelism = 1` per partition — Kafka offset commits are monotonic and out-of-order commits could advance the offset past in-flight messages, undoing the guarantee.

Under the hood, `IQueueConsumer` exposes optional `AcknowledgeAsync` / `NegativeAcknowledgeAsync` default-no-op methods. `ChangeSubscriber` calls them after each dispatched message; consumers that don't override them inherit the at-most-once behaviour. Broker-private correlation state (delivery tag, `ConsumeResult`) travels with the envelope via `MessageEnvelope.Metadata` and is consumed on first read so double-ACK attempts are silent no-ops rather than broker errors.

## Deduplication

Register a deduplication store to suppress duplicate deliveries. The `CorrelationId` in each
`MessageEnvelope` is used as the dedup key.

```csharp
// In-memory (default) — single process, cleared on restart
// No configuration needed: InMemoryDeduplicationStore is used automatically.

// Redis — distributed, survives restarts, shared across multiple subscriber instances
using StackExchange.Redis;
using RayTree.Plugins.Deduplication.Redis;

var multiplexer = await ConnectionMultiplexer.ConnectAsync("localhost:6379");

var tracker = EntityChangeTracker.Create()
    .UseRedisDeduplication(multiplexer)                     // default options
    // or with custom options:
    .UseRedisDeduplication(multiplexer, opt =>
    {
        opt.KeyPrefix       = "my-service";   // namespaces keys on a shared Redis instance
        opt.RetentionPeriod = TimeSpan.FromHours(48);
        opt.Database        = 1;              // logical Redis DB index; -1 = default
    })
    .ForEntity<Order>(e => e /* ... */)
    .Build();

// Custom store
var tracker = EntityChangeTracker.Create()
    .UseDeduplicationStore(new MyCustomStore())
    .ForEntity<Order>(e => e /* ... */)
    .Build();
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
dotnet test tests/RayTree.Plugins.Deduplication.Redis.Tests
```
