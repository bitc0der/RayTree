# Plugin Development Guide

RayTree's plugin system allows you to implement custom providers for outbox storage, queue publishing/consuming, serialization, and compression.

## Plugin Interfaces

### IOutbox

Stores changes and tracks their publish state.

```csharp
public interface IOutbox
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task WriteAsync<TEntity>(EntityChange<TEntity> change, CancellationToken cancellationToken = default)
        where TEntity : class;

    Task<IReadOnlyList<EntityChange<TEntity>>> GetUnpublishedAsync<TEntity>(
        int batchSize,
        CancellationToken cancellationToken = default)
        where TEntity : class;

    Task<IReadOnlyList<EntityChange<TEntity>>> GetUnpublishedAsync<TEntity>(
        ChangeType? changeType = null,
        DateTime? since = null,
        int batchSize = 100,
        CancellationToken cancellationToken = default)
        where TEntity : class;

    Task MarkPublishedAsync(long id, CancellationToken cancellationToken = default);

    Task<int> CleanupPublishedAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default);

    Task<EntityChange<TEntity>?> GetByIdAsync<TEntity>(long id, CancellationToken cancellationToken = default)
        where TEntity : class;
}
```

**Implementation notes:**
- `WriteAsync` should set `change.Id` to the auto-generated row ID (use `RETURNING id` in PostgreSQL)
- `GetUnpublishedAsync` returns entries ordered by `Timestamp`, limited by `batchSize`
- `MarkPublishedAsync` sets `Published = true` for the given ID
- `CleanupPublishedAsync` deletes published rows older than `retentionPeriod` and returns the count deleted

### IRepository

CRUD operations for source entity persistence.

```csharp
public interface IRepository<TEntity> : IRepository where TEntity : class
{
    Task InsertAsync(TEntity entity, CancellationToken cancellationToken = default);
    Task UpdateAsync(TEntity entity, CancellationToken cancellationToken = default);
    Task DeleteAsync(TEntity entity, CancellationToken cancellationToken = default);
    Task<TEntity?> GetByIdAsync(object id, CancellationToken cancellationToken = default);
}

public interface IRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}
```

### IQueuePublisher

Publishes a `MessageEnvelope` to a message broker. The envelope is the only thing that crosses the queue boundary — it contains change metadata plus the already-serialized and compressed entity state as `byte[] Payload`.

```csharp
public interface IQueuePublisher
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task PublishAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default);
}
```

**Implementation notes:**
- `InitializeAsync` should create the broker-side infrastructure (exchange, topic, queue) if it does not exist
- `Payload` is already compressed; write it to the broker as-is without re-encoding

### IQueueConsumer

Receives `MessageEnvelope` messages from a broker and exposes them as an async stream.

```csharp
public interface IQueueConsumer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<MessageEnvelope> ConsumeAsync(CancellationToken cancellationToken = default);
}
```

**Implementation notes:**
- `InitializeAsync` opens the connection and sets up the subscription
- `ConsumeAsync` must be called after `InitializeAsync`
- All broker operations (consume + ack) must run on the same thread for brokers with native single-thread requirements (e.g., Confluent.Kafka); use a dedicated background thread with a `Channel<MessageEnvelope>` buffer
- Accept `ILoggerFactory loggerFactory` as a required constructor parameter and create a typed `ILogger<T>` from it — do not add a `NullLoggerFactory.Instance` fallback inside the class itself

### IChangeSerializer

Serializes an `EntityChange<TEntity>` (including its typed `State`) into a byte stream and deserializes it back.

```csharp
public interface IChangeSerializer
{
    string Name { get; }

    Task SerializeAsync<TEntity>(
        EntityChange<TEntity> change,
        Stream destination,
        CancellationToken cancellationToken = default)
        where TEntity : class;

    Task<EntityChange<TEntity>> DeserializeAsync<TEntity>(
        Stream source,
        CancellationToken cancellationToken = default)
        where TEntity : class;
}
```

### IChangeCompressor

Compresses and decompresses byte streams.

```csharp
public interface IChangeCompressor
{
    string Name { get; }
    Task CompressAsync(Stream source, Stream destination, CancellationToken cancellationToken = default);
    Task DecompressAsync(Stream source, Stream destination, CancellationToken cancellationToken = default);
}
```

## Publishing Pipeline

Changes flow through the pipeline in this order on the **publisher side**:

```
EntityChange<T>
  → IChangeSerializer.SerializeAsync   (writes entity state to a MemoryStream)
  → IChangeCompressor.CompressAsync    (compresses into another MemoryStream)
  → MessageEnvelope { Payload = compressed bytes }
  → IQueuePublisher.PublishAsync       (sends envelope to broker)
```

On the **subscriber side** the envelope is received and the stages reverse:

```
MessageEnvelope
  → IChangeCompressor.DecompressAsync  (expands Payload)
  → IChangeSerializer.DeserializeAsync<TEntity>
  → EntityChange<TEntity> { State = typed entity }
  → ChangeHandlerAsync<TEntity>(change, cancellationToken)
```

## Registration

### Via EntityBuilder (per-entity)

```csharp
builder.ForEntity<MyEntity>()
    .UseOutbox(new MyCustomOutbox(connectionString))
    .UsePublisher(new MyCustomQueuePublisher(brokerOptions))
    .UseSerializer(new MyCustomSerializer())
    .UseCompressor(new MyCustomCompressor());
```

### Via factory (global default for all entity types)

```csharp
builder.UseSerializer<IChangeSerializer>(_ => new MyCustomSerializer());
builder.UseCompressor<IChangeCompressor>(_ => new MyCustomCompressor());
```

### Extension Method Pattern

```csharp
public static class MyOutboxExtensions
{
    public static IEntityBuilder UseMyCustomOutbox(
        this IEntityBuilder builder,
        string connectionString)
        => builder.UseOutbox(new MyCustomOutbox(connectionString));
}
```

## Testing Plugins

Register plugins directly on `ChangePublisher` and construct `EntityChangeTracker` from it:

```csharp
var publisher = new ChangePublisher(NullLoggerFactory.Instance);
publisher.RegisterOutbox(typeof(MyEntity), new InMemoryOutbox());
publisher.RegisterPublisher(typeof(MyEntity), new InMemoryQueue());
publisher.RegisterSerializer(typeof(MyEntity), new MyCustomSerializer());
publisher.RegisterCompressor(typeof(MyEntity), new MyCustomCompressor());

var tracker = new EntityChangeTracker(publisher);
await tracker.InitializeAsync();
```

Verify serializer round-trips:

```csharp
var change = new EntityChange<MyEntity>
{
    EntityId   = "1",
    ChangeType = ChangeType.Insert,
    EntityType = typeof(MyEntity).FullName!,
    State      = new MyEntity { Id = 1 }
};

using var stream = new MemoryStream();
await serializer.SerializeAsync(change, stream);
stream.Position = 0;

var deserialized = await serializer.DeserializeAsync<MyEntity>(stream);
Assert.That(deserialized.EntityId,    Is.EqualTo(change.EntityId));
Assert.That(deserialized.State!.Id,   Is.EqualTo(1));
```
