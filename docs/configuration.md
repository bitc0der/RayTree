# Configuration Guide

RayTree supports two configuration modes: **Dependency Injection** (recommended for ASP.NET Core) and **Standalone** (for console apps, workers, or manual control).

## Dependency Injection Mode

### Basic Setup

```csharp
builder.Services.AddChangeTracking(tracking =>
{
    tracking.ForEntity<Product>()
        .UsePostgreSqlOutbox(connectionString, "products");

    tracking.UseJsonSerializer();
    tracking.UseGzipCompressor();
});
```

### Multiple DbContexts

Each `DbContext` can have independent interceptor configuration:

```csharp
builder.Services.AddChangeTracking(tracking =>
{
    // Apply to all DbContexts by default
    tracking.ForEntity<Product>()
        .UsePostgreSqlOutbox(conn1, "products");

    // Opt-out specific DbContexts
    tracking.ExcludeDbContext<ReportingDbContext>();

    // Per-DbContext overrides
    tracking.ForDbContext<OrderDbContext>(ctx =>
    {
        ctx.ForEntity<Order>()
            .UsePostgreSqlOutbox(conn2, "orders")
            .UseKafkaQueue(kafkaConn, "orders");
    });
});
```

### Per-Entity Plugin Overrides

```csharp
builder.Services.AddChangeTracking(tracking =>
{
    // Global defaults
    tracking.UseJsonSerializer();
    tracking.UseGzipCompressor();

    // Product: JSON + Gzip (uses defaults)
    tracking.ForEntity<Product>()
        .UsePostgreSqlOutbox(conn, "products");

    // Order: Protobuf + LZ4 (overrides)
    tracking.ForEntity<Order>()
        .UsePostgreSqlOutbox(conn, "orders")
        .UseProtobufSerializer()
        .UseLz4Compressor();

    // AuditLog: MessagePack + NoOp
    tracking.ForEntity<AuditLog>()
        .UsePostgreSqlOutbox(conn, "audit_logs")
        .UseMessagePackSerializer()
        .UseNoOpCompressor();
});
```

### appsettings.json Configuration

```json
{
  "ChangeTracking": {
    "Publisher": {
      "PollingInterval": "00:00:05",
      "BatchSize": 100,
      "RetryCount": 3
    },
    "Notification": {
      "Channel": "entity_changes",
      "EnableFallbackPolling": true,
      "FallbackPollingInterval": "00:00:30"
    },
    "Outbox": {
      "RetentionPeriod": "7.00:00:00",
      "CleanupInterval": "1.00:00:00"
    }
  }
}
```

### Outbox Notification Mode (Low-Latency)

```csharp
tracking.ForEntity<Product>()
    .UsePostgreSqlOutbox(conn, "products", config =>
    {
        config.UseNotificationChannel("entity_changes");
        config.WithFallbackPolling(TimeSpan.FromSeconds(30));
    });
```

This uses PostgreSQL `NOTIFY/LISTEN` for near-instant change detection, with automatic fallback to polling if the connection drops.

## Standalone Mode

For console applications or scenarios without a DI container:

```csharp
var config = new ChangeTrackingConfiguration()
    .UsePostgreSqlOutbox(connectionString, "products")
    .UseRabbitMqQueue("amqp://localhost", "products", "product_exchange")
    .UseJsonSerializer()
    .UseGzipCompressor();

// Build() automatically initializes database schemas and queues
var tracker = config.Build();

// For async contexts, use BuildAsync()
// var tracker = await config.BuildAsync();

// Start the publisher
await config.StartPublisherAsync();

// Track changes
await tracker.TrackChangesAsync(changes);

// Stop when done
await config.StopPublisherAsync();
```

### Auto-Initialization

When you call `Build()` or `BuildAsync()`, RayTree automatically:

1. **Initializes storage** - Creates source tables, outbox tables, triggers, and indexes (PostgreSQL)
2. **Initializes queues** - Creates exchanges/queues (RabbitMQ), topics (Kafka), or no-op (InMemory)
3. **Is idempotent** - Uses `IF NOT EXISTS` and `CREATE OR REPLACE` - safe to call multiple times

```csharp
// Automatic initialization on Build()
var tracker = config.Build(); // Initializes everything

// Async version for non-blocking initialization
var tracker = await config.BuildAsync();
```

### Repository Registration

You can now register repositories for entities, which will also be auto-initialized:

```csharp
var config = new ChangeTrackingConfiguration()
    .ForEntity<Product>()
        .UseRepository(new PostgreSqlRepository<Product>(options))
        .UseOutbox(new PostgreSqlOutbox(outboxOptions))
        .UseQueue(new RabbitMqPublisher(queueOptions))
    .UseJsonSerializer()
    .UseGzipCompressor();

var tracker = config.Build(); // Initializes repository, outbox, and queue
```

### Resource Cleanup

```csharp
await using var config = new ChangeTrackingConfiguration()
    .UseInMemoryOutbox()
    .UseInMemoryQueue();

var tracker = config.Build();
// ... use tracker ...
// Dispose() called automatically by await using
```

## Subscriber Configuration

### DI Mode

```csharp
builder.Services.AddChangeSubscriber(subscriber =>
{
    subscriber.ForEntity<Product>()
        .FromRabbitMq("product_exchange", "product_queue")
        .UseJsonSerializer()
        .UseGzipCompressor()
        .OnInsert(p => HandleNewProduct(p))
        .OnUpdate(p => HandleUpdatedProduct(p))
        .OnDelete(p => HandleDeletedProduct(p));

    subscriber.ForEntity<Order>()
        .FromKafka("orders-topic", "order-consumer-group")
        .UseProtobufSerializer()
        .UseLz4Compressor()
        .OnChange((change, order) => HandleAnyChange(change, order));
});
```

### Standalone Subscriber

```csharp
var subscriberConfig = new ChangeSubscriberConfiguration()
    .ForEntity<Product>()
        .FromInMemory()
        .UseJsonSerializer()
        .OnInsert(p => Console.WriteLine($"New: {p.Name}"));

var subscriber = subscriberConfig.Build();
await subscriber.StartAsync();

// Process messages manually
await subscriber.ProcessMessageAsync(rabbitMqMessage);
```

### Deduplication

```csharp
subscriber.ForEntity<Product>()
    .FromRabbitMq("exchange", "queue")
    .UseDeduplication(new RedisDeduplicationStore("localhost:6379"))
    .OnInsert(p => HandleProduct(p));
```

### Error Handling

```csharp
subscriber.ForEntity<Product>()
    .FromRabbitMq("exchange", "queue")
    .OnErrorPolicy(ErrorHandlingPolicy.Retry(3, TimeSpan.FromSeconds(5)))
    .OnInsert(p => HandleProduct(p));
```

Available policies:
- `ErrorHandlingPolicy.Retry(maxRetries, delay)` - Retry with fixed delay
- `ErrorHandlingPolicy.Skip()` - Log and skip failed messages
- `ErrorHandlingPolicy.DeadLetter(queue)` - Send to dead-letter queue
