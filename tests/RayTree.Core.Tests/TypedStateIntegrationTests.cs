using RayTree.Core.Models;
using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Serialization;
using RayTree.Core.Tracking;
using RayTree.Plugins;
using RayTree.Plugins.Compressors.Brotli;
using RayTree.Plugins.Compressors.Gzip;
using RayTree.Plugins.Compressors.Lz4;
using RayTree.Plugins.InMemory;
using RayTree.Plugins.Serializers.Json;
using RayTree.Plugins.Serializers.Protobuf;

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
        where TEntity : class
    {
        using var ms = new MemoryStream();
        await serializer.SerializeAsync(change, ms);
        return ms.ToArray();
    }

    private static async Task<EntityChange<TEntity>> DeserializeGenericAsync<TEntity>(
        IChangeSerializer serializer,
        byte[] data)
        where TEntity : class
    {
        return await serializer.DeserializeAsync<TEntity>(new MemoryStream(data));
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
        using var dst = new MemoryStream();
        await compressor.CompressAsync(new MemoryStream(data), dst);
        return dst.ToArray();
    }

    private static async Task<byte[]> DecompressAsync(IChangeCompressor compressor, byte[] data)
    {
        using var dst = new MemoryStream();
        await compressor.DecompressAsync(new MemoryStream(data), dst);
        return dst.ToArray();
    }
}

