# Plugin Development Guide

RayTree's plugin system allows you to implement custom providers for storage, outbox, queue publishing, serialization, and compression.

## Plugin Interfaces

### IOutbox

Stores changes atomically within the same transaction as the entity save.

```csharp
public interface IOutbox
{
    Task<long> WriteAsync(EntityChange change, CancellationToken ct = default);
    Task<IEnumerable<EntityChange>> GetUnpublishedAsync(int batchSize, CancellationToken ct = default);
    Task MarkAsPublishedAsync(long id, CancellationToken ct = default);
    Task<int> DeleteOlderThanAsync(DateTime cutoff, CancellationToken ct = default);
}
```

**Implementation notes:**
- `WriteAsync` must return the auto-generated ID (use `RETURNING id` in PostgreSQL)
- `GetUnpublishedAsync` should return entries ordered by timestamp, limited by `batchSize`
- `MarkAsPublishedAsync` sets `is_published = true` for the given ID

### IRepository

CRUD operations for entity persistence.

```csharp
public interface IRepository<TEntity> where TEntity : class
{
    Task<TEntity?> GetByIdAsync(object id, CancellationToken ct = default);
    Task<IEnumerable<TEntity>> GetAllAsync(CancellationToken ct = default);
    Task AddAsync(TEntity entity, CancellationToken ct = default);
    Task UpdateAsync(TEntity entity, CancellationToken ct = default);
    Task DeleteAsync(object id, CancellationToken ct = default);
}
```

### IQueuePublisher

Publishes serialized/compressed change messages to a message broker.

```csharp
public interface IQueuePublisher : IDisposable
{
    Task PublishAsync(EntityChange change, PipeReader data, CancellationToken ct = default);
}
```

**Implementation notes:**
- The `PipeReader` contains the serialized + compressed data
- Read from the pipe and publish to your broker
- Dispose should clean up connections/channels

### IChangeSerializer

Serializes `EntityChange` + entity data into a byte stream.

```csharp
public interface IChangeSerializer
{
    ValueTask SerializeAsync(EntityChange change, object entityData, PipeWriter writer, CancellationToken ct = default);
    ValueTask<(EntityChange Change, object? EntityData)> DeserializeAsync(PipeReader reader, CancellationToken ct = default);
}
```

**Implementation notes:**
- Write to `PipeWriter` directly - avoid intermediate buffers
- `DeserializeAsync` should return the deserialized `EntityChange` and entity data

### IChangeCompressor

Compresses/decompresses byte streams.

```csharp
public interface IChangeCompressor
{
    ValueTask CompressAsync(PipeReader source, PipeWriter destination, CancellationToken ct = default);
    ValueTask DecompressAsync(PipeReader source, PipeWriter destination, CancellationToken ct = default);
}
```

### IDeduplicationStore

Prevents processing duplicate messages on the subscriber side.

```csharp
public interface IDeduplicationStore
{
    Task<bool> IsDuplicateAsync(string messageId, CancellationToken ct = default);
    Task MarkAsProcessedAsync(string messageId, CancellationToken ct = default);
}
```

## Registration

### Via Builder API

```csharp
tracking.ForEntity<MyEntity>()
    .UseOutbox(new MyCustomOutbox(connectionString))
    .UseQueue(new MyCustomQueuePublisher(connectionString))
    .UseSerializer(new MyCustomSerializer())
    .UseCompressor(new MyCustomCompressor());
```

### Extension Method Pattern

Create extension methods for a fluent API:

```csharp
public static class MyCustomOutboxExtensions
{
    public static EntityConfigurationBuilder<T> UseMyCustomOutbox<T>(
        this EntityConfigurationBuilder<T> builder,
        string connectionString)
    {
        return builder.UseOutbox(new MyCustomOutbox(connectionString));
    }
}
```

## Pipeline Order

The publishing pipeline processes changes in this order:

```
EntityChange → IChangeSerializer → IChangeCompressor → IQueuePublisher
```

The subscriber reverses this:

```
Message → IChangeCompressor (decompress) → IChangeSerializer (deserialize) → Handlers
```

## Testing Plugins

Use the in-memory plugins for integration testing:

```csharp
var tracker = new EntityChangeTracker();
tracker.RegisterOutbox(typeof(MyEntity), new InMemoryOutbox());
tracker.RegisterPublisher(typeof(MyEntity), new InMemoryQueue());
tracker.RegisterSerializer(typeof(MyEntity), new MyCustomSerializer());
tracker.RegisterCompressor(typeof(MyEntity), new MyCustomCompressor());
```

Verify your serializer round-trips correctly:

```csharp
var pipe = new Pipe();
await serializer.SerializeAsync(change, entityData, pipe.Writer);
pipe.Writer.Complete();

var (deserializedChange, deserializedData) = await serializer.DeserializeAsync(pipe.Reader);
Assert.That(deserializedChange.EntityId, Is.EqualTo(change.EntityId));
```
