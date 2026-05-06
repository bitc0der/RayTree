using RayTree.Core.Models;
using RayTree.Core.Tracking;

namespace RayTree.Plugins.Serializers.MessagePack.Tests;

public class MessagePackSerializerPluginTests
{
    [Test]
    public async Task SerializeThenDeserialize_RoundTrip_PreservesAllFields()
    {
        var plugin = new MessagePackSerializerPlugin();
        var original = CreateTestChange();

        var data = await SerializeAndCaptureAsync(plugin, original);
        var result = await DeserializeFromDataAsync<Order>(plugin, data);

        Assert.That(result.EntityId, Is.EqualTo(original.EntityId));
        Assert.That(result.EntityType, Is.EqualTo(original.EntityType));
        Assert.That(result.ChangeType, Is.EqualTo(original.ChangeType));
        Assert.That(result.Timestamp, Is.EqualTo(original.Timestamp).Within(1).Seconds);
        Assert.That(result.Version, Is.EqualTo(original.Version));
        Assert.That(result.CorrelationId, Is.EqualTo(original.CorrelationId));
        Assert.That(result.Published, Is.EqualTo(original.Published));
        Assert.That(result.State?.Id, Is.EqualTo(original.State?.Id));
    }

    [Test]
    public async Task SerializeAsync_InsertChangeType()
    {
        var plugin = new MessagePackSerializerPlugin();
        var change = new EntityChange<Order>
        {
            EntityType = typeof(Order).FullName!,
            EntityId = "1",
            ChangeType = ChangeType.Insert,
            Timestamp = DateTime.UtcNow,
            State = new Order { Id = 1, Total = 9.99m }
        };

        var data = await SerializeAndCaptureAsync(plugin, change);
        var result = await DeserializeFromDataAsync<Order>(plugin, data);
        Assert.That(result.ChangeType, Is.EqualTo(ChangeType.Insert));
    }

    [Test]
    public async Task SerializeAsync_UpdateChangeType()
    {
        var plugin = new MessagePackSerializerPlugin();
        var change = new EntityChange<Order>
        {
            EntityType = typeof(Order).FullName!,
            EntityId = "2",
            ChangeType = ChangeType.Update,
            Timestamp = DateTime.UtcNow,
            State = new Order { Id = 2 }
        };

        var data = await SerializeAndCaptureAsync(plugin, change);
        var result = await DeserializeFromDataAsync<Order>(plugin, data);
        Assert.That(result.ChangeType, Is.EqualTo(ChangeType.Update));
    }

    [Test]
    public async Task SerializeAsync_DeleteChangeType()
    {
        var plugin = new MessagePackSerializerPlugin();
        var change = new EntityChange<Order>
        {
            EntityType = typeof(Order).FullName!,
            EntityId = "3",
            ChangeType = ChangeType.Delete,
            Timestamp = DateTime.UtcNow,
            State = new Order { Id = 3 }
        };

        var data = await SerializeAndCaptureAsync(plugin, change);
        var result = await DeserializeFromDataAsync<Order>(plugin, data);
        Assert.That(result.ChangeType, Is.EqualTo(ChangeType.Delete));
    }

    [Test]
    public async Task SerializeAsync_WithCorrelationId_PreservesCorrelationId()
    {
        var plugin = new MessagePackSerializerPlugin();
        var correlationId = Guid.NewGuid();
        var change = new EntityChange<Order>
        {
            EntityType = typeof(Order).FullName!,
            EntityId = "4",
            ChangeType = ChangeType.Insert,
            Timestamp = DateTime.UtcNow,
            CorrelationId = correlationId,
            State = new Order { Id = 4 }
        };

        var data = await SerializeAndCaptureAsync(plugin, change);
        var result = await DeserializeFromDataAsync<Order>(plugin, data);
        Assert.That(result.CorrelationId, Is.EqualTo(correlationId));
    }

    [Test]
    public void Name_ReturnsMessagePack()
    {
        var plugin = new MessagePackSerializerPlugin();
        Assert.That(plugin.Name, Is.EqualTo("MessagePack"));
    }

    [Test]
    public async Task SerializeAsync_ProducesCompactBinary()
    {
        var plugin = new MessagePackSerializerPlugin();
        var change = CreateTestChange();

        var data = await SerializeAndCaptureAsync(plugin, change);
        Assert.That(data.Length, Is.GreaterThan(0));
    }

    [Test]
    public async Task SerializeAsync_GenericTypedState_RoundTrip()
    {
        var plugin = new MessagePackSerializerPlugin();
        var original = new EntityChange<Order>
        {
            EntityType = typeof(Order).FullName!,
            EntityId = "order-42",
            ChangeType = ChangeType.Insert,
            State = new Order { Id = 42, Total = 199.99m }
        };

        using var ms = new MemoryStream();
        await plugin.SerializeAsync(original, ms);
        ms.Position = 0;

        var result = await plugin.DeserializeAsync<Order>(ms);

        Assert.That(result.EntityId, Is.EqualTo("order-42"));
        Assert.That(result.ChangeType, Is.EqualTo(ChangeType.Insert));
        Assert.That(result.State, Is.Not.Null);
        Assert.That(result.State!.Id, Is.EqualTo(42));
        Assert.That(result.State.Total, Is.EqualTo(199.99m));
    }

    private static async Task<byte[]> SerializeAndCaptureAsync<TEntity>(MessagePackSerializerPlugin plugin, EntityChange<TEntity> change)
        where TEntity : class
    {
        using var ms = new MemoryStream();
        await plugin.SerializeAsync(change, ms);
        return ms.ToArray();
    }

    private static async Task<EntityChange<TEntity>> DeserializeFromDataAsync<TEntity>(MessagePackSerializerPlugin plugin, byte[] data)
        where TEntity : class
    {
        return await plugin.DeserializeAsync<TEntity>(new MemoryStream(data));
    }

    private static EntityChange<Order> CreateTestChange() => new()
    {
        Id = 200,
        EntityType = typeof(Order).FullName!,
        EntityId = "order-789",
        ChangeType = ChangeType.Insert,
        Timestamp = DateTime.UtcNow,
        Version = 1,
        CorrelationId = Guid.NewGuid(),
        Published = false,
        State = new Order { Id = 200, Total = 49.99m }
    };

    public class Order
    {
        public int Id { get; set; }
        public decimal Total { get; set; }
    }
}
