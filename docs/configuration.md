# Configuration Guide

The primary configuration API is `ChangeTrackingBuilder`. It registers per-entity plugins, sets global defaults, and produces an `EntityChangeTracker` via `Build()` / `BuildAsync()`.

## ChangeTrackingBuilder

### Per-Entity Configuration

`ForEntity<T>` accepts a callback that scopes all per-entity configuration. The parent builder is always returned, so multiple entity registrations chain cleanly:

```csharp
var builder = new ChangeTrackingBuilder();

builder
    .ForEntity<Product>(e => e
        .UseOutbox(new PostgreSqlOutbox<Product>(new PostgreSqlOutboxOptions
        {
            ConnectionString = connectionString
            // OutboxTableName defaults to "product_outbox"
        }))
        .UseQueue(new InMemoryQueue())
        .UseSerializer(new JsonSerializerPlugin())
        .UseCompressor(new GzipCompressorPlugin()))
    .ForEntity<Order>(e => e
        .UseOutbox(new PostgreSqlOutbox<Order>(new PostgreSqlOutboxOptions
        {
            ConnectionString = connectionString
            // OutboxTableName defaults to "order_outbox"
        }))
        .UseQueue(new InMemoryQueue())
        .UseSerializer(new ProtobufSerializerPlugin())
        .UseCompressor(new Lz4CompressorPlugin()));

var tracker = builder.Build();
```

### Global Serializer / Compressor

Extension methods on `IChangeTrackingBuilder` set a default factory applied to every entity type that does not have an explicit override:

```csharp
var builder = new ChangeTrackingBuilder();
builder.UseJsonSerializer();      // RayTree.Plugins.Serializers.Json
builder.UseGzipCompressor();      // RayTree.Plugins.Compressors.Gzip
// builder.UseProtobufSerializer()
// builder.UseMessagePackSerializer()
// builder.UseLz4Compressor()
// builder.UseBrotliCompressor()
// builder.UseNoOpCompressor()

builder.ForEntity<Product>(e => e
    .UseOutbox(new PostgreSqlOutbox<Product>(new PostgreSqlOutboxOptions
    {
        ConnectionString = connectionString
    }))
    .UseQueue(new InMemoryQueue()));
// Inherits JsonSerializer + GzipCompressor from global defaults

var tracker = builder.Build();
```

### Publisher Options

Control the polling interval, batch size, and retry behaviour for all `OutboxPublisherService` instances:

```csharp
builder.UsePublisherOptions(opt =>
{
    opt.PollingInterval = TimeSpan.FromSeconds(5);
    opt.BatchSize       = 100;
    opt.MaxRetryCount   = 3;
    opt.RetryDelay      = TimeSpan.FromSeconds(2);
});
```

### PostgreSQL Repository

Register a source table alongside the outbox:

```csharp
builder.ForEntity<Product>(e => e
    .UseRepository(new PostgreSqlRepository<Product>(new PostgreSqlRepositoryOptions
    {
        ConnectionString = connectionString
        // TableName defaults to "product"
    }))
    .UseOutbox(new PostgreSqlOutbox<Product>(new PostgreSqlOutboxOptions
    {
        ConnectionString = connectionString
    }))
    .UseQueue(new InMemoryQueue())
    .UseSerializer(new JsonSerializerPlugin())
    .UseCompressor(new GzipCompressorPlugin()));
```

### Outbox Notification Mode (Low-Latency)

Enable PostgreSQL `NOTIFY/LISTEN` on the outbox, then create a `NotificationBasedPublisher` alongside the tracker:

```csharp
var outboxOptions = new PostgreSqlOutboxOptions
{
    ConnectionString = connectionString
};
outboxOptions.UseNotificationChannel("product_notify")
             .WithFallbackPolling(TimeSpan.FromSeconds(30));

builder.ForEntity<Product>(e => e
    .UseOutbox(new PostgreSqlOutbox<Product>(outboxOptions))
    .UseQueue(new InMemoryQueue())
    .UseSerializer(new JsonSerializerPlugin())
    .UseCompressor(new GzipCompressorPlugin()));

var tracker = builder.Build(); // creates table + trigger

var notificationPublisher = new NotificationBasedPublisher(tracker, new NotificationBasedPublisherOptions
{
    ConnectionString        = connectionString,
    ChannelName             = "product_notify",
    FallbackPollingInterval = TimeSpan.FromSeconds(30)
});

await notificationPublisher.StartAsync();
```

See [trigger-setup.md](trigger-setup.md) for full details and hosting in ASP.NET Core.

## ChangeTrackingConfiguration

`ChangeTrackingConfiguration` is a thin wrapper around `ChangeTrackingBuilder` that adds `WithPollingInterval()` and `WithBatchSize()` convenience methods. It does **not** expose per-entity fluent configuration — use `ChangeTrackingBuilder` directly for most scenarios.

```csharp
var config = new ChangeTrackingConfiguration()
    .WithPollingInterval(TimeSpan.FromSeconds(5))
    .WithBatchSize(50);

// Register per-entity via the underlying builder factory methods
config.UseSerializer<IChangeSerializer>(_ => new JsonSerializerPlugin());
config.UseCompressor<IChangeCompressor>(_ => new GzipCompressorPlugin());
config.UseOutbox<IOutbox>(_ => new InMemoryOutbox());
config.UseQueue<IQueuePublisher>(_ => new InMemoryQueue());

var tracker = config.Build();
```

## Tracking Changes

```csharp
// Typed convenience methods — State is captured automatically
await tracker.TrackInsertAsync(new Product { Id = 1, Name = "Widget" });
await tracker.TrackUpdateAsync(new Product { Id = 1, Name = "Widget Pro" });
await tracker.TrackDeleteAsync(new Product { Id = 1, Name = "Widget Pro" });

// Generic overload (when change type is dynamic)
await tracker.TrackChangeAsync(entity, ChangeType.Insert);
```

## ChangeSubscriberBuilder

Configure the subscriber side using `ChangeSubscriberBuilder`. Global defaults (serializer, compressor, options) apply to every entity registration; per-entity callbacks can override any of them. The builder produces a `ChangeSubscriber` via `Build()`.

### Basic usage

```csharp
var subscriber = new ChangeSubscriberBuilder()
    .UseSerializer(new JsonSerializerPlugin())   // global default
    .UseCompressor(new GzipCompressorPlugin())   // global default
    .UseOptions(opt =>
    {
        opt.MaxRetries    = 3;
        opt.RetryDelay    = TimeSpan.FromSeconds(1);
        opt.SkipOnFailure = false;
    })
    .ForEntity<Order>(e => e
        .UseQueue(myConsumer)                    // IQueueConsumer for Order messages
        .OnInsert(async (change, ct) =>
        {
            var order = change.State;            // fully-typed Order
            Console.WriteLine($"New order: {order?.Id}");
        })
        .OnUpdate(async (change, ct) => { /* ... */ })
        .OnDelete(async (change, ct) => { /* ... */ }))
    .Build();

await subscriber.ConsumeFromConsumerAsync(myConsumer, cancellationToken);
```

### Multiple entities with global defaults

Set a serializer and compressor once globally, then register each entity with only the overrides it needs:

```csharp
var subscriber = new ChangeSubscriberBuilder()
    .UseSerializer(new JsonSerializerPlugin())
    .UseCompressor(new GzipCompressorPlugin())
    .ForEntity<Order>(e => e
        .UseQueue(orderConsumer)
        // inherits global serializer + compressor
        .OnInsert(async (change, ct) => { /* ... */ }))
    .ForEntity<Product>(e => e
        .UseQueue(productConsumer)
        .UseSerializer(new ProtobufSerializerPlugin())  // per-entity override
        .OnInsert(async (change, ct) => { /* ... */ }))
    .Build();
```

### Per-entity options override

Fine-tune retry behaviour for individual entity types while keeping global defaults for others:

```csharp
var subscriber = new ChangeSubscriberBuilder()
    .UseOptions(opt => opt.MaxRetries = 2)   // global default
    .ForEntity<Order>(e => e
        .UseQueue(orderConsumer)
        .UseOptions(opt => opt.MaxRetries = 5)  // Order-only override
        .OnInsert(async (change, ct) => { /* ... */ }))
    .ForEntity<Product>(e => e
        .UseQueue(productConsumer)
        // inherits MaxRetries = 2 from global
        .OnInsert(async (change, ct) => { /* ... */ }))
    .Build();
```

### Broker-specific queue helpers

Call the broker extension inside the `ForEntity` callback:

```csharp
// Kafka
.ForEntity<Order>(e => e
    .UseKafka(opt =>
    {
        opt.BootstrapServers = "localhost:9092";
        opt.Topic            = "orders";
        opt.GroupId          = "my-service";
    })
    .OnInsert(async (change, ct) => { /* ... */ }))

// RabbitMQ
.ForEntity<Order>(e => e
    .UseRabbitMq(opt =>
    {
        opt.HostName  = "localhost";
        opt.QueueName = "orders";
    })
    .OnInsert(async (change, ct) => { /* ... */ }))

// InMemory (testing)
.ForEntity<Order>(e => e
    .UseInMemoryQueue(inMemoryQueue)
    .OnInsert(async (change, ct) => { /* ... */ }))
```

### ASP.NET Core (DI)

`AddChangeSubscriber` registers `ChangeSubscriber` as a singleton and `ChangeSubscriberHostedService` as a hosted service. It returns `IChangeSubscriberBuilder` so you chain entity registrations directly. Options are bound from the `ChangeTracking:Subscriber` configuration section:

```csharp
builder.Services
    .AddChangeSubscriber(builder.Configuration)
    .UseRedisDeduplication("localhost:6379")     // optional; default is in-memory
    .ForEntity<Order>(e => e
        .UseInMemoryQueue(orderQueue)
        .UseSerializer(new JsonSerializerPlugin())
        .UseCompressor(new GzipCompressorPlugin())
        .OnInsert(async (change, ct) =>
            Console.WriteLine($"New order: {change.State?.Id}")));
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

### Deduplication

Every processed `CorrelationId` is recorded so duplicate deliveries (at-least-once brokers) are silently dropped.

| Store | Package | When to use |
|---|---|---|
| `InMemoryDeduplicationStore` | built-in | Single-process, testing |
| `RedisDeduplicationStore` | `RayTree.Subscriber` | Multiple subscriber instances |

```csharp
// Redis — call at the global builder level
subscriber = new ChangeSubscriberBuilder()
    .UseRedisDeduplication("localhost:6379")
    .ForEntity<Order>(e => e /* ... */)
    .Build();

// Custom store
subscriber = new ChangeSubscriberBuilder()
    .UseDeduplicationStore(new MyCustomStore())
    .ForEntity<Order>(e => e /* ... */)
    .Build();
```

## Cleanup

```csharp
// EntityChangeTracker is IDisposable — stops all publisher services
tracker.Dispose();

// Or use 'using'
using var tracker = builder.Build();
```
