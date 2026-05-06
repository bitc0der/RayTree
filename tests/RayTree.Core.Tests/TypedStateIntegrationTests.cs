using System.IO.Pipelines;
using RayTree.Models;
using RayTree.Plugins;
using RayTree.Plugins.Compressors.Brotli;
using RayTree.Plugins.Compressors.Gzip;
using RayTree.Plugins.Compressors.Lz4;
using RayTree.Plugins.InMemory;
using RayTree.Plugins.Serializers.Json;
using RayTree.Plugins.Serializers.Protobuf;
using RayTree.Tracking;

namespace RayTree.Core.Tests;

/// <summary>5.3 Integration tests for track with typed state (insert).</summary>
public class TrackInsertWithTypedStateTests
{
    private class User
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? Email { get; set; }
    }

    [Test]
    public async Task TrackInsertAsync_CapturesEntityStateAfterInsert()
    {
        var tracker = new EntityChangeTracker();
        var outbox = new InMemoryOutbox();
        tracker.RegisterOutbox(typeof(User), outbox);

        var user = new User { Id = 1, Name = "Alice", Email = "alice@example.com" };
        var change = await tracker.TrackInsertAsync(user);

        Assert.That(change.ChangeType, Is.EqualTo(ChangeType.Insert));
        Assert.That(change.State, Is.SameAs(user));
        Assert.That(change.State!.Id, Is.EqualTo(1));
        Assert.That(change.State.Name, Is.EqualTo("Alice"));
    }

    [Test]
    public async Task TrackInsertAsync_WritesTypedChangeToOutbox()
    {
        var tracker = new EntityChangeTracker();
        var outbox = new InMemoryOutbox();
        tracker.RegisterOutbox(typeof(User), outbox);

        var user = new User { Id = 2, Name = "Bob" };
        await tracker.TrackInsertAsync(user);

        var stored = await outbox.GetUnpublishedAsync<User>(10);
        Assert.That(stored, Has.Count.EqualTo(1));
        Assert.That(stored[0].State!.Name, Is.EqualTo("Bob"));
    }

    [Test]
    public async Task TrackInsertAsync_SetsEntityTypeFromGenericParameter()
    {
        var tracker = new EntityChangeTracker();
        var outbox = new InMemoryOutbox();
        tracker.RegisterOutbox(typeof(User), outbox);

        var user = new User { Id = 3, Name = "Charlie" };
        var change = await tracker.TrackInsertAsync(user);

        Assert.That(change.EntityType, Is.EqualTo(typeof(User).FullName));
    }

    [Test]
    public async Task TrackInsertAsync_SetsEntityIdFromIdProperty()
    {
        var tracker = new EntityChangeTracker();
        var outbox = new InMemoryOutbox();
        tracker.RegisterOutbox(typeof(User), outbox);

        var user = new User { Id = 42, Name = "Dave" };
        var change = await tracker.TrackInsertAsync(user);

        Assert.That(change.EntityId, Is.EqualTo("42"));
    }
}

/// <summary>5.4 Integration tests for track with typed state (update).</summary>
public class TrackUpdateWithTypedStateTests
{
    private class Product
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public decimal Price { get; set; }
    }

    [Test]
    public async Task TrackUpdateAsync_CapturesEntityStateAfterUpdate()
    {
        var tracker = new EntityChangeTracker();
        var outbox = new InMemoryOutbox();
        tracker.RegisterOutbox(typeof(Product), outbox);

        var product = new Product { Id = 10, Name = "Widget", Price = 19.99m };
        var change = await tracker.TrackUpdateAsync(product);

        Assert.That(change.ChangeType, Is.EqualTo(ChangeType.Update));
        Assert.That(change.State, Is.SameAs(product));
        Assert.That(change.State!.Price, Is.EqualTo(19.99m));
    }

    [Test]
    public async Task TrackUpdateAsync_WritesTypedChangeToOutbox()
    {
        var tracker = new EntityChangeTracker();
        var outbox = new InMemoryOutbox();
        tracker.RegisterOutbox(typeof(Product), outbox);

        var product = new Product { Id = 20, Name = "Gadget", Price = 49.99m };
        await tracker.TrackUpdateAsync(product);

        var stored = await outbox.GetUnpublishedAsync<Product>(10);
        Assert.That(stored, Has.Count.EqualTo(1));
        Assert.That(stored[0].State!.Name, Is.EqualTo("Gadget"));
    }
}

/// <summary>5.5 Integration tests for track with typed state (delete).</summary>
public class TrackDeleteWithTypedStateTests
{
    private class Order
    {
        public int Id { get; set; }
        public decimal Total { get; set; }
    }

    [Test]
    public async Task TrackDeleteAsync_CapturesEntityStateBeforeDelete()
    {
        var tracker = new EntityChangeTracker();
        var outbox = new InMemoryOutbox();
        tracker.RegisterOutbox(typeof(Order), outbox);

        var order = new Order { Id = 100, Total = 99.50m };
        var change = await tracker.TrackDeleteAsync(order);

        Assert.That(change.ChangeType, Is.EqualTo(ChangeType.Delete));
        Assert.That(change.State, Is.SameAs(order));
        Assert.That(change.State!.Total, Is.EqualTo(99.50m));
    }

    [Test]
    public async Task TrackDeleteAsync_WritesTypedChangeToOutbox()
    {
        var tracker = new EntityChangeTracker();
        var outbox = new InMemoryOutbox();
        tracker.RegisterOutbox(typeof(Order), outbox);

        var order = new Order { Id = 200, Total = 150.00m };
        await tracker.TrackDeleteAsync(order);

        var stored = await outbox.GetUnpublishedAsync<Order>(10);
        Assert.That(stored, Has.Count.EqualTo(1));
        Assert.That(stored[0].State!.Id, Is.EqualTo(200));
    }
}

/// <summary>5.6 Integration tests for outbox persistence with typed state.</summary>
public class OutboxTypedStatePersistenceTests
{
    private class Customer
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? Region { get; set; }
    }

    [Test]
    public async Task InMemoryOutbox_WriteAndReadTypedChange_PreservesState()
    {
        var outbox = new InMemoryOutbox();
        var customer = new Customer { Id = 1, Name = "Acme Corp", Region = "US" };
        var change = new EntityChange<Customer>
        {
            EntityType = typeof(Customer).FullName!,
            EntityId = "1",
            ChangeType = ChangeType.Insert,
            State = customer
        };

        await outbox.WriteAsync(change);
        var retrieved = await outbox.GetUnpublishedAsync<Customer>(10);

        Assert.That(retrieved, Has.Count.EqualTo(1));
        Assert.That(retrieved[0].State, Is.Not.Null);
        Assert.That(retrieved[0].State!.Name, Is.EqualTo("Acme Corp"));
        Assert.That(retrieved[0].State.Region, Is.EqualTo("US"));
    }

    [Test]
    public async Task InMemoryOutbox_GetByIdGeneric_ReturnsTypedChange()
    {
        var outbox = new InMemoryOutbox();
        var customer = new Customer { Id = 2, Name = "TechCo" };
        var change = new EntityChange<Customer>
        {
            EntityType = typeof(Customer).FullName!,
            EntityId = "2",
            ChangeType = ChangeType.Update,
            State = customer
        };

        await outbox.WriteAsync(change);
        var retrieved = await outbox.GetByIdAsync<Customer>(change.Id);

        Assert.That(retrieved, Is.Not.Null);
        Assert.That(retrieved!.State!.Name, Is.EqualTo("TechCo"));
    }

    [Test]
    public async Task InMemoryOutbox_FilterByChangeType_ReturnsMatchingTypedChanges()
    {
        var outbox = new InMemoryOutbox();
        var c1 = new Customer { Id = 1, Name = "A" };
        var c2 = new Customer { Id = 2, Name = "B" };

        await outbox.WriteAsync(new EntityChange<Customer> { EntityType = typeof(Customer).FullName!, EntityId = "1", ChangeType = ChangeType.Insert, State = c1 });
        await outbox.WriteAsync(new EntityChange<Customer> { EntityType = typeof(Customer).FullName!, EntityId = "2", ChangeType = ChangeType.Delete, State = c2 });

        var inserts = await outbox.GetUnpublishedAsync<Customer>(changeType: ChangeType.Insert, batchSize: 10);
        Assert.That(inserts, Has.Count.EqualTo(1));
        Assert.That(inserts[0].State!.Name, Is.EqualTo("A"));
    }

    [Test]
    public async Task InMemoryOutbox_WriteNonGeneric_AndReadViaBaseType_Works()
    {
        var outbox = new InMemoryOutbox();
        var change = new EntityChange
        {
            EntityType = "LegacyEntity",
            EntityId = "99",
            ChangeType = ChangeType.Update
        };

        await outbox.WriteAsync(change);
        var all = await outbox.GetUnpublishedAsync(10);

        Assert.That(all, Has.Count.EqualTo(1));
        Assert.That(all[0].EntityType, Is.EqualTo("LegacyEntity"));
    }
}

/// <summary>Shared test entity — must be public so code-generation-based serializers (e.g. MessagePack) can access it.</summary>
public class SerializableItem
{
    public int Id { get; set; }
    public string? Label { get; set; }
    public bool Active { get; set; }
}

/// <summary>5.7 Integration tests for serialization/deserialization with typed state.</summary>
public class SerializationTypedStateTests
{
    private static EntityChange<SerializableItem> CreateTypedChange(int id = 1) => new()
    {
        Id = id,
        EntityType = typeof(SerializableItem).FullName!,
        EntityId = id.ToString(),
        ChangeType = ChangeType.Insert,
        Version = 1,
        CorrelationId = Guid.NewGuid(),
        State = new SerializableItem { Id = id, Label = $"Item-{id}", Active = true }
    };

    private static async Task<byte[]> SerializeGenericAsync<TEntity>(IChangeSerializer serializer, EntityChange<TEntity> change)
    {
        var pipe = new Pipe();
        await serializer.SerializeAsync(change, pipe.Writer);
        using var ms = new MemoryStream();
        var result = await pipe.Reader.ReadAsync();
        foreach (var segment in result.Buffer)
        {
            await ms.WriteAsync(segment);
        }
        pipe.Reader.AdvanceTo(result.Buffer.End);
        await pipe.Reader.CompleteAsync();
        return ms.ToArray();
    }

    private static async Task<EntityChange<TEntity>> DeserializeGenericAsync<TEntity>(IChangeSerializer serializer, byte[] data)
    {
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(data);
        await pipe.Writer.FlushAsync();
        await pipe.Writer.CompleteAsync();
        return await serializer.DeserializeAsync<TEntity>(pipe.Reader);
    }

    [Test]
    public async Task JsonSerializer_SerializeDeserialize_RoundTripWithTypedState()
    {
        var serializer = new JsonSerializerPlugin();
        var original = CreateTypedChange(1);

        var data = await SerializeGenericAsync(serializer, original);
        var result = await DeserializeGenericAsync<SerializableItem>(serializer, data);

        Assert.That(result.EntityId, Is.EqualTo(original.EntityId));
        Assert.That(result.ChangeType, Is.EqualTo(original.ChangeType));
        Assert.That(result.State, Is.Not.Null);
        Assert.That(result.State!.Label, Is.EqualTo("Item-1"));
        Assert.That(result.State.Active, Is.True);
    }

    [Test]
    public async Task ProtobufSerializer_SerializeDeserialize_RoundTripWithTypedState()
    {
        var serializer = new ProtobufSerializerPlugin();
        var original = CreateTypedChange(3);

        var data = await SerializeGenericAsync(serializer, original);
        var result = await DeserializeGenericAsync<SerializableItem>(serializer, data);

        Assert.That(result.EntityId, Is.EqualTo(original.EntityId));
        Assert.That(result.ChangeType, Is.EqualTo(original.ChangeType));
        Assert.That(result.State, Is.Not.Null);
        Assert.That(result.State!.Id, Is.EqualTo(3));
        Assert.That(result.State.Label, Is.EqualTo("Item-3"));
        Assert.That(result.State.Active, Is.True);
    }

    [Test]
    public async Task FullPipeline_JsonGzip_RoundTripWithTypedState()
    {
        var serializer = new JsonSerializerPlugin();
        var compressor = new GzipCompressorPlugin();
        var original = CreateTypedChange(10);

        var serializedBytes = await SerializeGenericAsync(serializer, original);
        var compressed = await CompressAsync(compressor, serializedBytes);
        var decompressed = await DecompressAsync(compressor, compressed);
        var result = await DeserializeGenericAsync<SerializableItem>(serializer, decompressed);

        Assert.That(result.State!.Label, Is.EqualTo("Item-10"));
    }

    [Test]
    public async Task FullPipeline_JsonBrotli_RoundTripWithTypedState()
    {
        var serializer = new JsonSerializerPlugin();
        var compressor = new BrotliCompressorPlugin();
        var original = CreateTypedChange(11);

        var serializedBytes = await SerializeGenericAsync(serializer, original);
        var compressed = await CompressAsync(compressor, serializedBytes);
        var decompressed = await DecompressAsync(compressor, compressed);
        var result = await DeserializeGenericAsync<SerializableItem>(serializer, decompressed);

        Assert.That(result.State!.Label, Is.EqualTo("Item-11"));
    }

    [Test]
    public async Task FullPipeline_JsonLz4_RoundTripWithTypedState()
    {
        var serializer = new JsonSerializerPlugin();
        var compressor = new Lz4CompressorPlugin();
        var original = CreateTypedChange(12);

        var serializedBytes = await SerializeGenericAsync(serializer, original);
        var compressed = await CompressAsync(compressor, serializedBytes);
        var decompressed = await DecompressAsync(compressor, compressed);
        var result = await DeserializeGenericAsync<SerializableItem>(serializer, decompressed);

        Assert.That(result.State!.Label, Is.EqualTo("Item-12"));
    }

    private static async Task<byte[]> CompressAsync(IChangeCompressor compressor, byte[] data)
    {
        var src = new Pipe();
        var dst = new Pipe();
        await src.Writer.WriteAsync(data);
        src.Writer.Complete();
        await compressor.CompressAsync(src.Reader, dst.Writer);
        return await ReadPipeAsync(dst.Reader);
    }

    private static async Task<byte[]> DecompressAsync(IChangeCompressor compressor, byte[] data)
    {
        var src = new Pipe();
        var dst = new Pipe();
        await src.Writer.WriteAsync(data);
        src.Writer.Complete();
        await compressor.DecompressAsync(src.Reader, dst.Writer);
        return await ReadPipeAsync(dst.Reader);
    }

    private static async Task<byte[]> ReadPipeAsync(PipeReader reader)
    {
        using var ms = new MemoryStream();
        var result = await reader.ReadAsync();
        foreach (var segment in result.Buffer)
        {
            await ms.WriteAsync(segment);
        }
        reader.AdvanceTo(result.Buffer.End);
        await reader.CompleteAsync();
        return ms.ToArray();
    }
}

/// <summary>5.8 Verify backward compatibility: non-generic EntityChange still works.</summary>
public class BackwardCompatibilityTests
{
    [Test]
    public async Task NonGenericEntityChange_CanBeTrackedAndWrittenToOutbox()
    {
        var tracker = new EntityChangeTracker();
        var outbox = new InMemoryOutbox();
        tracker.RegisterOutbox(typeof(object), outbox);

        var change = new EntityChange
        {
            EntityType = typeof(object).AssemblyQualifiedName!,
            EntityId = "legacy-1",
            ChangeType = ChangeType.Update
        };

        await tracker.TrackChangeAsync(change);

        var all = await outbox.GetUnpublishedAsync(10);
        Assert.That(all, Has.Count.EqualTo(1));
        Assert.That(all[0].EntityId, Is.EqualTo("legacy-1"));
    }

    [Test]
    public async Task NonGenericEntityChange_SerializesAndDeserializesCorrectly()
    {
        var serializer = new JsonSerializerPlugin();
        var change = new EntityChange
        {
            EntityType = "LegacyEntity",
            EntityId = "old-42",
            ChangeType = ChangeType.Delete,
            Version = 1
        };

        var pipe = new Pipe();
        await serializer.SerializeAsync(change, pipe.Writer);

        var deserialized = await serializer.DeserializeAsync(pipe.Reader, "LegacyEntity");

        Assert.That(deserialized.EntityId, Is.EqualTo("old-42"));
        Assert.That(deserialized.ChangeType, Is.EqualTo(ChangeType.Delete));
    }

    [Test]
    public async Task TrackChangesAsync_BatchWithNonGenericChanges_SetsCorrelationId()
    {
        var tracker = new EntityChangeTracker();
        var outbox = new InMemoryOutbox();
        tracker.RegisterOutbox(typeof(object), outbox);

        var changes = new[]
        {
            new EntityChange { EntityType = typeof(object).AssemblyQualifiedName!, EntityId = "1", ChangeType = ChangeType.Insert },
            new EntityChange { EntityType = typeof(object).AssemblyQualifiedName!, EntityId = "2", ChangeType = ChangeType.Update }
        };

        await tracker.TrackChangesAsync(changes);

        Assert.That(changes[0].CorrelationId, Is.EqualTo(changes[1].CorrelationId));
        Assert.That(changes[0].CorrelationId, Is.Not.EqualTo(Guid.Empty));
    }

    [Test]
    public async Task GenericAndNonGeneric_CanCoexistInSameOutbox_RetrievableIndependently()
    {
        var outbox = new InMemoryOutbox();
        var baseChange = new EntityChange { EntityType = "Base", EntityId = "b-1", ChangeType = ChangeType.Insert };
        var typedChange = new EntityChange<string> { EntityType = "Typed", EntityId = "t-1", ChangeType = ChangeType.Insert, State = "hello" };

        await outbox.WriteAsync(baseChange);
        await outbox.WriteAsync(typedChange);

        var all = await outbox.GetUnpublishedAsync(10);
        Assert.That(all, Has.Count.EqualTo(2));

        var typed = await outbox.GetUnpublishedAsync<string>(10);
        Assert.That(typed, Has.Count.EqualTo(1));
        Assert.That(typed[0].State, Is.EqualTo("hello"));
    }
}
