# Plugin Development Guide

RayTree's plugin system allows you to implement custom providers for outbox storage, queue publishing, serialization, and compression.

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

Publishes serialized+compressed change messages to a message broker.

```csharp
public interface IQueuePublisher
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task PublishAsync(EntityChange change, PipeReader payload, CancellationToken cancellationToken = default);
}
```

**Implementation notes:**
- `payload` is a `PipeReader` containing the already-serialized and compressed data
- Read from the pipe and write to your broker; do not buffer unnecessarily

### IChangeSerializer

Serializes an `EntityChange<TEntity>` into a byte stream and deserializes it back.

```csharp
public interface IChangeSerializer
{
    string Name { get; }

    Task SerializeAsync<TEntity>(
        EntityChange<TEntity> change,
        PipeWriter destination,
        CancellationToken cancellationToken = default)
        where TEntity : class;

    Task<EntityChange<TEntity>> DeserializeAsync<TEntity>(
        PipeReader source,
        CancellationToken cancellationToken = default)
        where TEntity : class;
}
```

**Implementation notes:**
- Write directly to `PipeWriter` — avoid intermediate `MemoryStream` allocations
- Call `destination.Complete()` when done writing

### IChangeCompressor

Compresses and decompresses byte streams.

```csharp
public interface IChangeCompressor
{
    string Name { get; }
    Task CompressAsync(PipeReader source, PipeWriter destination, CancellationToken cancellationToken = default);
    Task DecompressAsync(PipeReader source, PipeWriter destination, CancellationToken cancellationToken = default);
}
```

## Registration

### Via EntityBuilder (per-entity)

```csharp
builder.ForEntity<MyEntity>()
    .UseOutbox(new MyCustomOutbox(connectionString))
    .UseQueue(new MyCustomQueuePublisher(connectionString))
    .UseSerializer(new MyCustomSerializer())
    .UseCompressor(new MyCustomCompressor());
```

### Via factory (global default for all entity types)

```csharp
builder.UseSerializer<IChangeSerializer>(_ => new MyCustomSerializer());
builder.UseCompressor<IChangeCompressor>(_ => new MyCustomCompressor());
```

### Extension Method Pattern

Create extension methods for a fluent API:

```csharp
public static class MyOutboxExtensions
{
    public static IEntityBuilder UseMyCustomOutbox(
        this IEntityBuilder builder,
        string connectionString)
        => builder.UseOutbox(new MyCustomOutbox(connectionString));
}
```

## Publishing Pipeline

Changes flow through the pipeline in this order:

```
EntityChange<T> → IChangeSerializer.SerializeAsync → IChangeCompressor.CompressAsync → IQueuePublisher.PublishAsync
```

All three stages run concurrently connected by `Pipe` instances — data flows from writer to reader without buffering the full payload.

## Testing Plugins

Register plugins directly on `EntityChangeTracker` without using the builder:

```csharp
var tracker = new EntityChangeTracker();
tracker.RegisterOutbox(typeof(MyEntity), new InMemoryOutbox());
tracker.RegisterPublisher(typeof(MyEntity), new InMemoryQueue());
tracker.RegisterSerializer(typeof(MyEntity), new MyCustomSerializer());
tracker.RegisterCompressor(typeof(MyEntity), new MyCustomCompressor());
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

var pipe = new Pipe();
await serializer.SerializeAsync(change, pipe.Writer);
pipe.Writer.Complete();

var deserialized = await serializer.DeserializeAsync<MyEntity>(pipe.Reader);
Assert.That(deserialized.EntityId, Is.EqualTo(change.EntityId));
Assert.That(deserialized.State!.Id, Is.EqualTo(1));
```
