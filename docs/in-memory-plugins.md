# In-Memory Plugin Guide

The `RayTree.Plugins.InMemory` package provides fully in-memory implementations for testing and development without any external dependencies.

## Package

**Name:** `RayTree.Plugins.InMemory`

**Dependencies:** `RayTree.Core` only

## Components

### InMemoryRepository

Thread-safe repository using `ConcurrentDictionary<TKey, TEntity>`.

```csharp
var repo = new InMemoryRepository<int, Product>();
await repo.AddAsync(new Product { Id = 1, Name = "Test" });
var product = await repo.GetByIdAsync(1);
```

### InMemoryOutbox

Thread-safe outbox with full query and cleanup support.

```csharp
var outbox = new InMemoryOutbox();
var id = await outbox.WriteAsync(new EntityChange
{
    EntityType = typeof(Product).AssemblyQualifiedName!,
    EntityId = "1",
    ChangeType = ChangeType.Insert,
    Timestamp = DateTime.UtcNow
});

var unpublished = await outbox.GetUnpublishedAsync(10);
await outbox.MarkAsPublishedAsync(id);
await outbox.DeleteOlderThanAsync(DateTime.UtcNow.AddHours(-1));
```

### InMemoryQueue

Channel-based queue with per-entity-type broadcast.

```csharp
var queue = new InMemoryQueue();

// Publish
var pipe = new Pipe();
await pipe.Writer.WriteAsync(...);
await pipe.Writer.Complete();
await queue.PublishAsync(change, pipe.Reader);

// Consume
var message = await queue.Reader.ReadAsync();
```

## Quick Start for Testing

```csharp
[Test]
public async Task ChangeTracking_Works_InMemory()
{
    var tracker = new EntityChangeTracker();
    var outbox = new InMemoryOutbox();
    var queue = new InMemoryQueue();
    var serializer = new JsonSerializerPlugin();
    var compressor = new NoOpCompressorPlugin();

    tracker.RegisterOutbox(typeof(Product), outbox);
    tracker.RegisterPublisher(typeof(Product), queue);
    tracker.RegisterSerializer(typeof(Product), serializer);
    tracker.RegisterCompressor(typeof(Product), compressor);

    // Track a change
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

    // Verify outbox received it
    var entries = outbox.GetAll();
    Assert.That(entries, Has.Count.EqualTo(1));

    // Verify queue received it
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var message = await queue.Reader.ReadAsync(cts.Token);
    Assert.That(message.Change.EntityId, Is.EqualTo("1"));
}
```

## Configuration Builder

```csharp
var config = new ChangeTrackingConfiguration()
    .UseInMemoryOutbox()
    .UseInMemoryQueue()
    .UseJsonSerializer()
    .UseNoOpCompressor();

var tracker = config.Build();
```

## Mixed Mode (In-Memory + External)

Use in-memory components alongside real external services:

```csharp
// In-memory outbox for testing, real RabbitMQ for publishing
tracking.ForEntity<Product>()
    .UseInMemoryOutbox()
    .UseRabbitMqQueue("amqp://localhost", "products", "exchange");
```

## Subscriber Integration

### In-Memory Deduplication Store

```csharp
subscriber.ForEntity<Product>()
    .FromInMemory()
    .UseDeduplication(new InMemoryDeduplicationStore())
    .OnInsert(p => HandleProduct(p));
```

### Consume from In-Memory Queue

```csharp
subscriber.ForEntity<Product>()
    .FromInMemory()
    .UseJsonSerializer()
    .OnInsert(p => Console.WriteLine($"New: {p.Name}"));
```

### Subscription Handles

```csharp
var subscription = subscriber.Subscribe<Product>(
    ChangeType.Insert,
    p => Console.WriteLine($"New: {p.Name}"));

// Later: unsubscribe
subscription.Unsubscribe();
```

## Transaction Simulation

The in-memory outbox simulates EF Core transactions:

```csharp
using var tx = outbox.BeginTransaction();

try
{
    await outbox.WriteAsync(change1);
    await outbox.WriteAsync(change2);
    await tx.CommitAsync();
}
catch
{
    await tx.RollbackAsync(); // Changes are discarded
}
```
