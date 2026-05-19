# Configuration Guide

The primary configuration API is accessed via `EntityChangeTracker.Create()`, which returns `IChangeTrackingBuilder`. It registers per-entity plugins, sets global defaults, and produces an `EntityChangeTracker` via `Build()` / `BuildAsync()`.

## EntityChangeTracker.Create()

### Per-Entity Configuration

`ForEntity<T>` accepts a callback that scopes all per-entity configuration. The parent builder is always returned, so multiple entity registrations chain cleanly:

```csharp
var builder = EntityChangeTracker.Create();

builder
    .ForEntity<Product>(e => e
        .UseOutbox(new PostgreSqlOutbox<Product>(new PostgreSqlOutboxOptions
        {
            ConnectionString = connectionString
            // OutboxTableName defaults to "product_outbox"
        }))
        .UsePublisher(new InMemoryQueue())
        .UseSerializer(new JsonSerializerPlugin())
        .UseCompressor(new GzipCompressorPlugin()))
    .ForEntity<Order>(e => e
        .UseOutbox(new PostgreSqlOutbox<Order>(new PostgreSqlOutboxOptions
        {
            ConnectionString = connectionString
            // OutboxTableName defaults to "order_outbox"
        }))
        .UsePublisher(new InMemoryQueue())
        .UseSerializer(new ProtobufSerializerPlugin())
        .UseCompressor(new Lz4CompressorPlugin()));

var tracker = builder.Build();
```

### Global Serializer / Compressor

Extension methods on `IChangeTrackingBuilder` set a default factory applied to every entity type that does not have an explicit override:

```csharp
var builder = EntityChangeTracker.Create();
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
    .UsePublisher(new InMemoryQueue()));
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
    .UsePublisher(new InMemoryQueue())
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
    .UsePublisher(new InMemoryQueue())
    .UseSerializer(new JsonSerializerPlugin())
    .UseCompressor(new GzipCompressorPlugin()));

var tracker = builder.Build(); // creates table + trigger

var notificationPublisher = new NotificationBasedPublisher(
    tracker,
    new NotificationBasedPublisherOptions
    {
        ConnectionString        = connectionString,
        ChannelName             = "product_notify",
        FallbackPollingInterval = TimeSpan.FromSeconds(30)
    },
    loggerFactory);  // ILoggerFactory — required; use NullLoggerFactory.Instance in tests

await notificationPublisher.StartAsync();
```

See [trigger-setup.md](trigger-setup.md) for full details and hosting in ASP.NET Core.

## ChangeTrackingConfiguration

`ChangeTrackingConfiguration` is a thin wrapper around `IChangeTrackingBuilder` that adds `WithPollingInterval()` and `WithBatchSize()` convenience methods. It does **not** expose per-entity fluent configuration — use `EntityChangeTracker.Create()` directly for most scenarios.

```csharp
var config = new ChangeTrackingConfiguration()
    .WithPollingInterval(TimeSpan.FromSeconds(5))
    .WithBatchSize(50);

// Register per-entity via the underlying builder factory methods
config.UseSerializer<IChangeSerializer>(_ => new JsonSerializerPlugin());
config.UseCompressor<IChangeCompressor>(_ => new GzipCompressorPlugin());
config.UseOutbox<IOutbox>(_ => new InMemoryOutbox());
config.UsePublisher<IQueuePublisher>(_ => new InMemoryQueue());

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
        .UseConsumer(myConsumer)                 // IQueueConsumer for Order messages
        .OnInsert(async (change, ct) =>
        {
            var order = change.State;            // fully-typed Order
            Console.WriteLine($"New order: {order?.Id}");
        })
        .OnUpdate(async (change, ct) => { /* ... */ })
        .OnDelete(async (change, ct) => { /* ... */ }))
    .Build();
```
### Multiple entities with global defaults

Set a serializer and compressor once globally, then register each entity with only the overrides it needs:

```csharp
var subscriber = new ChangeSubscriberBuilder()
    .UseSerializer(new JsonSerializerPlugin())
    .UseCompressor(new GzipCompressorPlugin())
    .ForEntity<Order>(e => e
        .UseConsumer(orderConsumer)
        // inherits global serializer + compressor
        .OnInsert(async (change, ct) => { /* ... */ }))
    .ForEntity<Product>(e => e
        .UseSerializer(new ProtobufSerializerPlugin())  // per-entity override
        .UseConsumer(productConsumer)
        .OnInsert(async (change, ct) => { /* ... */ }))
    .Build();
```

### Per-entity options override

Fine-tune retry behaviour for individual entity types while keeping global defaults for others:

```csharp
var subscriber = new ChangeSubscriberBuilder()
    .UseOptions(opt => opt.MaxRetries = 2)   // global default
    .ForEntity<Order>(e => e
        .UseOptions(opt => opt.MaxRetries = 5)  // Order-only override
        .UseConsumer(orderConsumer)
        .OnInsert(async (change, ct) => { /* ... */ }))
    .ForEntity<Product>(e => e
        .UseConsumer(productConsumer)
        // inherits MaxRetries = 2 from global
        .OnInsert(async (change, ct) => { /* ... */ }))
    .Build();
```

### Kafka publisher — partition key

`KafkaPublisherOptions.KeySelector` controls which Kafka partition key is stamped on each outgoing message. Messages with the same key are guaranteed to land on the same partition, so they are consumed in order.

The default selector uses `EntityType:EntityId`:

```csharp
builder.ForEntity<Order>(e => e
    .UsePublisher(new KafkaPublisher(new KafkaPublisherOptions
    {
        BootstrapServers = "localhost:9092",
        Topic            = "orders"
        // KeySelector defaults to envelope => $"{envelope.EntityType}:{envelope.EntityId}"
    })));
```

Override `KeySelector` to shard by a different field. For example, to shard by tenant so all tenant changes land on the same partition — and different tenants can be processed in parallel by separate consumer-group members:

```csharp
new KafkaPublisherOptions
{
    BootstrapServers = "localhost:9092",
    Topic            = "orders",
    KeySelector      = envelope => envelope.EntityId.Split(':')[0]  // "tenantId:entityId" → tenantId
}
```

Or use any envelope metadata — change type, entity type, a custom field embedded in `EntityId`, etc. The selector runs on the publisher side; the consumer side is unaffected.

#### Consumer-group parallelism

Kafka distributes partitions across all members of a consumer group. To process different entities (or entity key ranges) in parallel, run multiple `KafkaConsumer` instances that share the same `GroupId` and point at the same topic — Kafka assigns each instance a disjoint set of partitions automatically. No RayTree configuration is needed beyond the standard `KafkaConsumerOptions`:

```csharp
// Instance A and Instance B both use GroupId = "order-processors"
// Kafka assigns ~half the partitions to each.
new KafkaConsumerOptions
{
    BootstrapServers = "localhost:9092",
    Topic            = "orders",
    GroupId          = "order-processors"
}
```

With `AckAfterHandler = true`, keep `MaxDegreeOfParallelism = 1` per consumer instance (offset commits are monotonic — out-of-order commits can skip messages).

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
```

### RabbitMQ publisher — routing key

`RabbitMqPublisherOptions.RoutingKeySelector` controls the AMQP routing key stamped on each message. On a `topic` exchange, consumers bind queues with wildcard patterns to receive only the messages they need — that is how RabbitMQ routes and parallelises processing.

The default produces `{RoutingKey}.{EntityType}.{changeType}` (e.g. `change.Order.insert`):

```csharp
builder.ForEntity<Order>(e => e
    .UsePublisher(new RabbitMqPublisher(new RabbitMqPublisherOptions
    {
        ExchangeName = "entity_changes",
        RoutingKey   = "change"
        // RoutingKeySelector is null → falls back to "change.Order.insert" / "change.Order.update" etc.
    })));
```

Override `RoutingKeySelector` to route by any envelope field. For example, to shard by tenant so each tenant's messages land on a dedicated queue:

```csharp
new RabbitMqPublisherOptions
{
    ExchangeName       = "entity_changes",
    RoutingKeySelector = envelope => $"change.{envelope.EntityId.Split(':')[0]}.{envelope.EntityType}"
    // "tenantId:entityId" → "change.tenantId.Order"
    // Consumer binds with "change.acme.*" to receive only ACME tenant messages
}
```

When `RoutingKeySelector` is set it takes full control of the key; the `RoutingKey` base prefix is ignored. Call `options.ResolveRoutingKey(envelope)` directly if you need to compute the key outside the publisher (e.g. in tests or queue-binding setup).

```csharp
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
    .UseRedisDeduplication(multiplexer)          // optional; default is in-memory
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
| `RedisDeduplicationStore` | `RayTree.Plugins.Deduplication.Redis` | Multiple subscriber instances or cross-restart dedup |

```csharp
// Redis — supply an IConnectionMultiplexer from StackExchange.Redis
using StackExchange.Redis;
using RayTree.Plugins.Deduplication.Redis;

var multiplexer = await ConnectionMultiplexer.ConnectAsync("localhost:6379");

subscriber = new ChangeSubscriberBuilder()
    .UseRedisDeduplication(multiplexer)                 // default options
    .ForEntity<Order>(e => e /* ... */)
    .Build();

// With custom options
subscriber = new ChangeSubscriberBuilder()
    .UseRedisDeduplication(multiplexer, opt =>
    {
        opt.KeyPrefix       = "my-service";  // namespace on shared Redis; default "default"
        opt.RetentionPeriod = TimeSpan.FromHours(48);
        opt.Database        = 1;             // logical DB index; default -1 (connection default)
    })
    .ForEntity<Order>(e => e /* ... */)
    .Build();

// Custom store
subscriber = new ChangeSubscriberBuilder()
    .UseDeduplicationStore(new MyCustomStore())
    .ForEntity<Order>(e => e /* ... */)
    .Build();
```

## Logging

RayTree uses `Microsoft.Extensions.Logging` throughout. All runtime service classes require a logger — there is no silent NullLogger fallback inside services.

### Standalone (no DI)

Pass an `ILoggerFactory` to `EntityChangeTracker.Create()`:

```csharp
// No logging (tests, scripts)
var tracker = EntityChangeTracker.Create().Build();

// With logging
using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
var tracker = EntityChangeTracker.Create(loggerFactory).Build();
```

`EntityChangeTracker.Create()` normalises `null` to `NullLoggerFactory.Instance`, so calling `EntityChangeTracker.Create()` without an argument produces a working tracker with no log output.

### ASP.NET Core (DI)

`AddChangeTracking` resolves `ILoggerFactory` from the DI container automatically:

```csharp
builder.Services.AddLogging(b => b.AddConsole()); // standard host setup
builder.Services.AddChangeTracking(builder.Configuration, tracking => { ... });
// No UseLoggerFactory call needed — the host's ILoggerFactory is wired in automatically
```

### Broker-specific consumers

`KafkaConsumer` and `RabbitMqConsumer` require `ILoggerFactory` as a second constructor argument. When constructing them directly outside a builder, pass the factory explicitly:

```csharp
// In tests
var consumer = new KafkaConsumer(options, NullLoggerFactory.Instance);

// In production code
var consumer = new KafkaConsumer(options, loggerFactory);
```

When using the `.UseKafka(...)` / `.UseRabbitMq(...)` extension methods inside a `ForEntity` callback, `NullLoggerFactory.Instance` is used internally — to get real logging from these consumers, construct them directly and pass to `.UseConsumer(consumer)`.

### What gets logged

| Class | Level | When |
|---|---|---|
| `OutboxPublisherService` | `Information` | Polling loop start / stop |
| `OutboxPublisherService` | `Warning` | Per-retry publish failure |
| `OutboxPublisherService` | `Error` | Batch error; retries exhausted |
| `ChangeSubscriber` | `Warning` | Unknown entity type in envelope |
| `ChangeSubscriber` | `Debug` | Dedup hit; no handlers matched |
| `ChangeSubscriber` | `Warning` | Handler retry attempt |
| `ChangeSubscriber` | `Error` | Handler dropped (SkipOnFailure) |
| `ChangePublisher` | `Information` | Publisher service registered per entity |
| `EntityChangeTracker` | `Information` | Consumer loop start per entity type / handler (in `StartAsync`) |
| `ChangeTrackingHostedService` | `Information` | Service stop |
| `NotificationBasedPublisher` | `Information` | Start / stop |
| `NotificationBasedPublisher` | `Warning` | Listen-loop error; fallback-poll error; per-change publish failure |
| `KafkaConsumer` | `Error` | Fatal Kafka error |
| `KafkaConsumer` | `Warning` | Consume error; envelope parse failure |
| `RabbitMqConsumer` | `Warning` | Message processing error (before requeue) |

## Observability — OpenTelemetry Metrics

RayTree emits `System.Diagnostics.Metrics` instruments on a `Meter` named `"RayTree"` (counters, histograms, and an observable gauge for outbox depth). Instrument calls are silent no-ops when no listener is attached, so there is no overhead for consumers that opt out.

### Default (built-in meter)

`EntityChangeTracker.Create()` creates a `RayTreeMeter` automatically and `EntityChangeTracker` disposes it. To collect the metrics, attach a `MeterListener` to the meter named `"RayTree"`, or use the OTel SDK via the `RayTree.OpenTelemetry` package:

```csharp
services.AddOpenTelemetry()
    .WithMetrics(b => b
        .AddRayTreeMetrics()
        .AddPrometheusExporter());
```

### Custom meter

Pass a `RayTreeMeter` instance to share it across trackers or to control its lifetime:

```csharp
var meter = new RayTreeMeter();
var tracker = EntityChangeTracker.Create(loggerFactory)
    .UseMeter(meter)
    .ForEntity<Order>(/* ... */)
    .Build();
// Caller-supplied meter is NOT disposed by the tracker.
```

Full instrument inventory, unit conventions, suggested bucket boundaries, and sample dashboard queries are in [opentelemetry-metrics.md](opentelemetry-metrics.md).

## Cleanup

```csharp
// EntityChangeTracker is IDisposable — stops all publisher services
tracker.Dispose();

// Or use 'using'
using var tracker = builder.Build();
```
