# Configuration Guide

The primary configuration API is `ChangeTrackingBuilder`. It registers per-entity plugins, sets global defaults, and produces an `EntityChangeTracker` via `Build()` / `BuildAsync()`.

## ChangeTrackingBuilder

### Per-Entity Configuration

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

builder.ForEntity<Order>()
    .UseOutbox(new PostgreSqlOutbox<Order>(new PostgreSqlOutboxOptions
    {
        ConnectionString = connectionString
        // OutboxTableName defaults to "order_outbox"
    }))
    .UseQueue(new InMemoryQueue())
    .UseSerializer(new ProtobufSerializerPlugin())
    .UseCompressor(new Lz4CompressorPlugin());

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

builder.ForEntity<Product>()
    .UseOutbox(new PostgreSqlOutbox<Product>(new PostgreSqlOutboxOptions
    {
        ConnectionString = connectionString
    }))
    .UseQueue(new InMemoryQueue());

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
builder.ForEntity<Product>()
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
    .UseCompressor(new GzipCompressorPlugin());
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

builder.ForEntity<Product>()
    .UseOutbox(new PostgreSqlOutbox<Product>(outboxOptions))
    .UseQueue(new InMemoryQueue())
    .UseSerializer(new JsonSerializerPlugin())
    .UseCompressor(new GzipCompressorPlugin());

var tracker = builder.Build(); // creates table + trigger

var notificationPublisher = new NotificationBasedPublisher(tracker, new NotificationBasedPublisherOptions
{
    ConnectionString    = connectionString,
    ChannelName         = "product_notify",
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
// Typed convenience methods
await tracker.TrackInsertAsync(new Product { Id = 1, Name = "Widget" });
await tracker.TrackUpdateAsync(new Product { Id = 1, Name = "Widget Pro" });
await tracker.TrackDeleteAsync(new Product { Id = 1, Name = "Widget Pro" });

// Generic overload (when change type is dynamic)
await tracker.TrackChangeAsync(entity, ChangeType.Insert);
```

## Cleanup

```csharp
// EntityChangeTracker is IDisposable — stops all publisher services
tracker.Dispose();

// Or use 'using'
using var tracker = builder.Build();
```
