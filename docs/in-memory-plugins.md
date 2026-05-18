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

Thread-safe outbox with full query and cleanup support. Stores `EntityChange<TEntity>` including the typed `State` property.

```csharp
var outbox = new InMemoryOutbox();
var change = new EntityChange<Product>
{
    EntityType = typeof(Product).FullName!,
    EntityId   = "1",
    ChangeType = ChangeType.Insert,
    State      = new Product { Id = 1, Name = "Widget" }
};
await outbox.WriteAsync(change);

var unpublished = await outbox.GetUnpublishedAsync<Product>(batchSize: 10);
await outbox.MarkPublishedAsync(unpublished[0].Id);
```

### InMemoryQueue

Channel-based queue that implements both `IQueuePublisher` and `IQueueConsumer`. Messages are `MessageEnvelope` objects containing change metadata and a serialized+compressed `byte[] Payload`.

```csharp
var queue = new InMemoryQueue();

// Publish (done automatically by OutboxPublisherService)
await queue.PublishAsync(new MessageEnvelope
{
    EntityType    = typeof(Product).FullName!,
    EntityId      = "1",
    ChangeType    = ChangeType.Insert,
    CorrelationId = Guid.NewGuid(),
    Payload       = serializedBytes
});

// Consume via IAsyncEnumerable
await foreach (var envelope in queue.ConsumeAsync(cancellationToken))
{
    Console.WriteLine($"Received: {envelope.EntityId}");
}
```

### InMemoryBroadcastQueue

Fan-out queue for **Isolated-mode** testing and local development. Implements `IQueuePublisher`; each call to `Subscribe()` returns a new `IQueueConsumer` backed by its own `Channel<MessageEnvelope>`. Every `PublishAsync` call delivers to **all** currently subscribed channels.

Use this as the factory target for `UseConsumerFactory` — the factory is called once per unique handler name, so each name gets an independent consumer:

```csharp
var broadcast = new InMemoryBroadcastQueue();

var tracker = new ChangeTrackingBuilder()
    .ForEntity<Order>(e => e
        .UseOutbox(new InMemoryOutbox())
        .UsePublisher(broadcast)                              // publishes to all subscribers
        .UseSerializer(new JsonSerializerPlugin())
        .UseCompressor(new NoOpCompressorPlugin())
        .UseConsumerFactory(_ => broadcast.Subscribe())       // one consumer per handler name
        .OnInsert("read-model", async (change, ct) => { /* ... */ })
        .OnInsert("notifier",   async (change, ct) => { /* ... */ }))
    .Build();
```

A subscriber channel is completed (and its `ConsumeAsync` enumerable ends) when:
- `Complete()` is called on the broadcast queue (or it is disposed), **or**
- The individual subscriber is disposed.

Messages published before a `Subscribe()` call are **not** replayed to the new subscriber.

```csharp
// Fan-out to two independent consumers
var broadcast = new InMemoryBroadcastQueue();
var consumerA = broadcast.Subscribe();
var consumerB = broadcast.Subscribe();

await broadcast.PublishAsync(envelope);
// Both consumerA and consumerB receive the envelope independently.

// Dispose a subscriber — removes its channel; subsequent publishes skip it
((IDisposable)consumerA).Dispose();
```

## Quick Start for Testing

Use `EntityChangeTracker` with a `ChangeSubscriber` for a full publish→subscribe round-trip:

```csharp
[Test]
public async Task ChangeTracking_Works_InMemory()
{
    var queue      = new InMemoryQueue();
    var serializer = new JsonSerializerPlugin();
    var compressor = new NoOpCompressorPlugin();
    var received   = new TaskCompletionSource<EntityChange<Product>>();

    var tracker = new ChangeTrackingBuilder()
        .ForEntity<Product>(e => e
            .UseOutbox(new InMemoryOutbox())
            .UsePublisher(queue)
            .UseSerializer(serializer)
            .UseCompressor(compressor)
            .UseConsumer(queue)
            .OnInsert(async (change, ct) => received.TrySetResult(change)))
        .UsePublisherOptions(opt => opt.PollingInterval = TimeSpan.FromMilliseconds(50))
        .Build();

    using var cts = new CancellationTokenSource();
    await tracker.StartAsync(cts.Token);   // starts consumer loop
    // In a .NET Generic Host app, ChangeTrackingHostedService calls StartAsync automatically.

    await tracker.TrackInsertAsync(new Product { Id = 1, Name = "Widget" });

    var change = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Assert.That(change.EntityId,      Is.EqualTo("1"));
    Assert.That(change.State!.Name,   Is.EqualTo("Widget"));

    cts.Cancel();
    await tracker.StopAsync();
    tracker.Dispose();
}
```

## ASP.NET Core (DI)

### Publisher

```csharp
var orderQueue = new InMemoryQueue();

builder.Services
    .AddChangeTracking(builder.Configuration, tracking =>
    {
        tracking.ForEntity<Order>(e => e
            .UseOutbox(new InMemoryOutbox())
            .UsePublisher(orderQueue)
            .UseSerializer(new JsonSerializerPlugin())
            .UseCompressor(new NoOpCompressorPlugin()));
    });
```

### Subscriber

```csharp
builder.Services
    .AddChangeSubscriber(builder.Configuration)
    .ForEntity<Order>(e => e
        .UseInMemoryQueue(orderQueue)   // same instance as publisher
        .UseSerializer(new JsonSerializerPlugin())
        .UseCompressor(new NoOpCompressorPlugin())
        .OnInsert(async (change, ct) =>
        {
            Console.WriteLine($"New order: {change.EntityId}, total: {change.State?.Total}");
        }));
```

`ChangeSubscriberHostedService` starts the consume loop automatically when the host starts.

## Mixed Mode (In-Memory + External)

Use an in-memory outbox for testing while targeting a real broker:

```csharp
tracking.ForEntity<Product>(e => e
    .UseOutbox(new InMemoryOutbox())
    .UsePublisher(new RabbitMqPublisher(rabbitOptions))
    .UseSerializer(new JsonSerializerPlugin())
    .UseCompressor(new GzipCompressorPlugin()));
```
